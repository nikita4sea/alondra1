package tasks

import (
	"context"
	"encoding/json"
	"errors"
	"time"
)

type Kind int

const (
	Groups Kind = iota
	Lists
	Tasks
)

func (k Kind) String() string { return [...]string{"group", "list", "task"}[k] }

// Scope is the caller in one space. Every read and write is limited to records with this tenant AND this owner.
type Scope struct {
	TenantID string
	UserID   string
}

// Record is one stored resource: the todo-v1 object plus its server version.
type Record struct {
	ID        string
	Data      json.RawMessage
	Version   int64
	UpdatedAt time.Time
	Deleted   bool
}

// Columns are the values extracted from Data for constraints and queries.
type Columns struct {
	ListID    string  // tasks
	GroupID   *string // lists
	IsDefault bool    // lists
}

type Query struct {
	After  string // ids greater than this (keyset pagination)
	Limit  int
	ListID string // tasks only
}

var (
	ErrNotMember        = errors.New("not a member of this space")
	ErrNotFound         = errors.New("not found")
	ErrDefaultList      = errors.New("the default Tasks list cannot be deleted")
	ErrDefaultExists    = errors.New("there is already a default list")
	ErrDefaultImmutable = errors.New("isDefault cannot change")
	ErrIDImmutable      = errors.New("id cannot change")
	ErrPrecondition     = errors.New("If-Match with the current version is required")
)

// ConflictError: the expected version is not current (or the id already exists). Current is the server's record, so the
// client can keep its local edit in a conflict queue and decide; nothing is overwritten silently.
type ConflictError struct {
	Kind    string // "version", "exists", "completion"
	Current *Record
}

func (e *ConflictError) Error() string { return e.Kind + " conflict" }

// ReferenceError: a listId/groupId does not name a live list/group of the same user in the same space.
type ReferenceError struct{ Field, ID string }

func (e *ReferenceError) Error() string { return e.Field + " does not name one of your lists or groups in this space" }

type Tx interface {
	Get(ctx context.Context, kind Kind, id string, lock bool) (Record, error) // includes tombstones; ErrNotFound if never existed
	List(ctx context.Context, kind Kind, q Query) ([]Record, error)
	Insert(ctx context.Context, kind Kind, r Record, c Columns, now time.Time) error
	Update(ctx context.Context, kind Kind, r Record, c Columns, now time.Time) error
	SoftDelete(ctx context.Context, kind Kind, id string, version int64, now time.Time) error
	ListsInGroup(ctx context.Context, groupID string) ([]Record, error)
	TasksInList(ctx context.Context, listID string) ([]Record, error)
	DefaultListExists(ctx context.Context) (bool, error)
	// InsertCompletion stores a record unless its id exists; then it returns the existing data and inserted=false.
	InsertCompletion(ctx context.Context, c CompletionRecord, data json.RawMessage, completedAt, now time.Time) (inserted bool, existing json.RawMessage, err error)
	Completions(ctx context.Context, before *Cursor, limit int) ([]json.RawMessage, *Cursor, error)
}

// Cursor pages completion history (newest first).
type Cursor struct {
	CompletedAt time.Time
	ID          string
}

type Store interface {
	// InScope runs fn in one transaction after checking, with a share lock on the membership row, that the user is an
	// active member of a live space; ErrNotMember otherwise. A removal waits for the transaction and then applies.
	InScope(ctx context.Context, scope Scope, fn func(Tx) error) error
}

type Service struct {
	store Store
	now   func() time.Time
}

func NewService(store Store, now func() time.Time) *Service { return &Service{store: store, now: now} }

const (
	DefaultPageSize = 500
	MaxPageSize     = 1000
)

func (s *Service) List(ctx context.Context, scope Scope, kind Kind, q Query) ([]Record, error) {
	if q.Limit <= 0 || q.Limit > MaxPageSize {
		q.Limit = DefaultPageSize
	}
	var out []Record
	err := s.store.InScope(ctx, scope, func(tx Tx) (err error) {
		out, err = tx.List(ctx, kind, q)
		return err
	})
	return out, err
}

func (s *Service) Get(ctx context.Context, scope Scope, kind Kind, id string) (Record, error) {
	var out Record
	err := s.store.InScope(ctx, scope, func(tx Tx) error {
		r, err := tx.Get(ctx, kind, id, false)
		if err != nil {
			return err
		}
		if r.Deleted {
			return ErrNotFound
		}
		out = r
		return nil
	})
	return out, err
}

// parse validates a todo-v1 object of the kind and returns its canonical JSON, its id, and its key columns.
func (s *Service) parse(kind Kind, data []byte) (json.RawMessage, string, Columns, error) {
	switch kind {
	case Groups:
		g, err := ParseGroup(data)
		if err != nil {
			return nil, "", Columns{}, err
		}
		raw, err := json.Marshal(g)
		return raw, g.ID, Columns{}, err
	case Lists:
		l, err := ParseList(data, s.now())
		if err != nil {
			return nil, "", Columns{}, err
		}
		raw, err := json.Marshal(l)
		return raw, l.ID, Columns{GroupID: l.GroupID, IsDefault: l.IsDefault}, err
	default:
		t, err := ParseTask(data, s.now())
		if err != nil {
			return nil, "", Columns{}, err
		}
		raw, err := json.Marshal(t)
		return raw, t.ID, Columns{ListID: t.ListID}, err
	}
}

// references checks that a task's list and a list's group are live records of the same user in the same space.
func references(ctx context.Context, tx Tx, kind Kind, c Columns) error {
	check := func(k Kind, field, id string) error {
		r, err := tx.Get(ctx, k, id, false)
		if errors.Is(err, ErrNotFound) || (err == nil && r.Deleted) {
			return &ReferenceError{Field: field, ID: id}
		}
		return err
	}
	switch {
	case kind == Tasks:
		return check(Lists, "listId", c.ListID)
	case kind == Lists && c.GroupID != nil:
		return check(Groups, "groupId", *c.GroupID)
	}
	return nil
}

// Create stores a new resource with the client's id. An existing id (live or deleted) is a conflict that returns the
// server copy: a retry recognizes its own record, and a deleted record is never resurrected by accident.
func (s *Service) Create(ctx context.Context, scope Scope, kind Kind, data []byte) (Record, error) {
	raw, id, cols, err := s.parse(kind, data)
	if err != nil {
		return Record{}, err
	}
	var out Record
	err = s.store.InScope(ctx, scope, func(tx Tx) error {
		existing, err := tx.Get(ctx, kind, id, true)
		if err == nil {
			return &ConflictError{Kind: "exists", Current: &existing}
		}
		if !errors.Is(err, ErrNotFound) {
			return err
		}
		if err := references(ctx, tx, kind, cols); err != nil {
			return err
		}
		if kind == Lists && cols.IsDefault {
			exists, err := tx.DefaultListExists(ctx)
			if err != nil {
				return err
			}
			if exists {
				return ErrDefaultExists
			}
		}
		now := s.now().UTC()
		out = Record{ID: id, Data: raw, Version: 1, UpdatedAt: now}
		return tx.Insert(ctx, kind, out, cols, now)
	})
	return out, err
}

// Patch applies an RFC 7386 merge patch to the stored object if expected is the current version. For tasks, an
// optional completion record is appended in the same transaction (e.g. completing one occurrence of a repeating task
// and advancing its due date), idempotently by the record's id.
func (s *Service) Patch(ctx context.Context, scope Scope, kind Kind, id string, expected int64, changes json.RawMessage, completion json.RawMessage) (Record, error) {
	if expected <= 0 {
		return Record{}, ErrPrecondition
	}
	var record *CompletionRecord
	var completionRaw json.RawMessage
	if len(completion) > 0 && string(completion) != "null" {
		if kind != Tasks {
			return Record{}, &ValidationError{Problems: []string{"append_completion is only for tasks"}}
		}
		c, err := ParseCompletion(completion)
		if err != nil {
			return Record{}, err
		}
		if c.TaskID != id {
			return Record{}, &ValidationError{Problems: []string{"append_completion.taskId must be this task's id"}}
		}
		record = &c
		completionRaw, _ = json.Marshal(c)
	}
	var out Record
	err := s.store.InScope(ctx, scope, func(tx Tx) error {
		current, err := tx.Get(ctx, kind, id, true)
		if err != nil {
			return err
		}
		if current.Deleted {
			return ErrNotFound
		}
		if current.Version != expected {
			return &ConflictError{Kind: "version", Current: &current}
		}
		merged, err := MergePatch(current.Data, changes)
		if err != nil {
			return err
		}
		raw, newID, cols, err := s.parse(kind, merged)
		if err != nil {
			return err
		}
		if newID != id {
			return ErrIDImmutable
		}
		if kind == Lists {
			var before List
			_ = json.Unmarshal(current.Data, &before)
			if before.IsDefault != cols.IsDefault {
				return ErrDefaultImmutable
			}
		}
		if err := references(ctx, tx, kind, cols); err != nil {
			return err
		}
		now := s.now().UTC()
		if record != nil {
			completedAt, _ := time.Parse(time.RFC3339Nano, record.CompletedUtc)
			inserted, existing, err := tx.InsertCompletion(ctx, *record, completionRaw, completedAt, now)
			if err != nil {
				return err
			}
			if !inserted && !sameJSON(existing, completionRaw) {
				return &ConflictError{Kind: "completion"}
			}
		}
		out = Record{ID: id, Data: raw, Version: current.Version + 1, UpdatedAt: now}
		return tx.Update(ctx, kind, out, cols, now)
	})
	return out, err
}

// Delete soft-deletes (tombstone). Deleting a group ungroups its lists; deleting a list deletes its tasks; the default
// list cannot be deleted. Completion history is kept.
func (s *Service) Delete(ctx context.Context, scope Scope, kind Kind, id string, expected int64) error {
	if expected <= 0 {
		return ErrPrecondition
	}
	return s.store.InScope(ctx, scope, func(tx Tx) error {
		current, err := tx.Get(ctx, kind, id, true)
		if err != nil {
			return err
		}
		if current.Deleted {
			return ErrNotFound
		}
		if current.Version != expected {
			return &ConflictError{Kind: "version", Current: &current}
		}
		now := s.now().UTC()
		switch kind {
		case Groups:
			lists, err := tx.ListsInGroup(ctx, id)
			if err != nil {
				return err
			}
			for _, l := range lists {
				ungrouped, err := MergePatch(l.Data, json.RawMessage(`{"groupId":null}`))
				if err != nil {
					return err
				}
				var list List
				_ = json.Unmarshal(ungrouped, &list)
				if err := tx.Update(ctx, Lists, Record{ID: l.ID, Data: ungrouped, Version: l.Version + 1}, Columns{IsDefault: list.IsDefault}, now); err != nil {
					return err
				}
			}
		case Lists:
			var list List
			_ = json.Unmarshal(current.Data, &list)
			if list.IsDefault {
				return ErrDefaultList
			}
			tasks, err := tx.TasksInList(ctx, id)
			if err != nil {
				return err
			}
			for _, t := range tasks {
				if err := tx.SoftDelete(ctx, Tasks, t.ID, t.Version+1, now); err != nil {
					return err
				}
			}
		}
		return tx.SoftDelete(ctx, kind, id, current.Version+1, now)
	})
}

func (s *Service) History(ctx context.Context, scope Scope, before *Cursor, limit int) ([]json.RawMessage, *Cursor, error) {
	if limit <= 0 || limit > MaxPageSize {
		limit = DefaultPageSize
	}
	var items []json.RawMessage
	var next *Cursor
	err := s.store.InScope(ctx, scope, func(tx Tx) (err error) {
		items, next, err = tx.Completions(ctx, before, limit)
		return err
	})
	return items, next, err
}

// Export returns the caller's data in this space as an alondra1-todo document (importable by the Windows app).
func (s *Service) Export(ctx context.Context, scope Scope, maxTasks int) (map[string]any, error) {
	doc := map[string]any{"format": "alondra1-todo", "version": 1, "exportedUtc": s.now().UTC().Format(time.RFC3339Nano)}
	err := s.store.InScope(ctx, scope, func(tx Tx) error {
		for _, kind := range []Kind{Groups, Lists, Tasks} {
			items := []json.RawMessage{}
			after := ""
			for {
				page, err := tx.List(ctx, kind, Query{After: after, Limit: MaxPageSize})
				if err != nil {
					return err
				}
				for _, r := range page {
					items = append(items, r.Data)
				}
				if len(items) > maxTasks {
					return ErrTooLarge
				}
				if len(page) < MaxPageSize {
					break
				}
				after = page[len(page)-1].ID
			}
			doc[[...]string{"groups", "lists", "tasks"}[kind]] = items
		}
		history := []json.RawMessage{}
		var cursor *Cursor
		for {
			page, next, err := tx.Completions(ctx, cursor, MaxPageSize)
			if err != nil {
				return err
			}
			history = append(history, page...)
			if next == nil {
				break
			}
			if len(history) > maxTasks*10 {
				return ErrTooLarge
			}
			cursor = next
		}
		doc["history"] = history
		return nil
	})
	return doc, err
}

var ErrTooLarge = errors.New("too much data for a synchronous export")

func sameJSON(a, b json.RawMessage) bool {
	var x, y any
	if json.Unmarshal(a, &x) != nil || json.Unmarshal(b, &y) != nil {
		return false
	}
	ax, _ := json.Marshal(x)
	by, _ := json.Marshal(y)
	return string(ax) == string(by)
}
