package tasks

import (
	"context"
	"encoding/json"
	"errors"
	"regexp"
	"time"
)

type Kind int

const (
	Groups Kind = iota
	Lists
	Tasks
)

func (k Kind) String() string { return [...]string{"group", "list", "task"}[k] }

// ParseKind maps a resource_type of the sync protocol to a Kind.
func ParseKind(value string) (Kind, bool) {
	switch value {
	case "group":
		return Groups, true
	case "list":
		return Lists, true
	case "task":
		return Tasks, true
	}
	return 0, false
}

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

// Envelope is the API and sync representation of a record: {id, version, schema_version, updated_at, <kind>: object}.
func Envelope(kind Kind, r Record) map[string]any {
	return map[string]any{"id": r.ID, "version": r.Version, "schema_version": SchemaVersion,
		"updated_at": r.UpdatedAt.UTC().Format(time.RFC3339Nano), kind.String(): r.Data}
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

// Change is one entry of the space's change log. VisibleTo is the only user who may receive it ("" = every member).
// Payload is exactly what sync clients receive (bootstrap items have the same shape).
type Change struct {
	ResourceType string
	ResourceID   string
	Action       string // "upsert" or "delete"
	Payload      json.RawMessage
	VisibleTo    string
}

var (
	ErrNotMember        = errors.New("not a member of this space")
	ErrNotFound         = errors.New("not found")
	ErrDefaultList      = errors.New("the default Tasks list cannot be deleted")
	ErrDefaultExists    = errors.New("there is already a default list")
	ErrDefaultImmutable = errors.New("isDefault cannot change")
	ErrIDImmutable      = errors.New("id cannot change")
	ErrPrecondition     = errors.New("If-Match with the current version is required")
	ErrTooLarge         = errors.New("too much data for a synchronous export")
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
	// Log appends to the space's change log in this transaction, in commit order (see the store).
	Log(ctx context.Context, c Change, now time.Time) error
	// Receipt returns the stored result of an applied sync mutation (ok=false if none); SaveReceipt stores one.
	Receipt(ctx context.Context, deviceID, mutationID string) (result json.RawMessage, ok bool, err error)
	SaveReceipt(ctx context.Context, deviceID, mutationID string, result json.RawMessage, now time.Time) error
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

// ---------- change log ----------

func logUpsert(ctx context.Context, tx Tx, scope Scope, kind Kind, r Record, now time.Time) error {
	payload, err := json.Marshal(map[string]any{"resource_type": kind.String(), "action": "upsert", "resource_id": r.ID, "record": Envelope(kind, r)})
	if err != nil {
		return err
	}
	return tx.Log(ctx, Change{ResourceType: kind.String(), ResourceID: r.ID, Action: "upsert", Payload: payload, VisibleTo: scope.UserID}, now)
}

func logDelete(ctx context.Context, tx Tx, scope Scope, kind Kind, id string, version int64, now time.Time) error {
	payload, err := json.Marshal(map[string]any{"resource_type": kind.String(), "action": "delete", "resource_id": id, "version": version})
	if err != nil {
		return err
	}
	return tx.Log(ctx, Change{ResourceType: kind.String(), ResourceID: id, Action: "delete", Payload: payload, VisibleTo: scope.UserID}, now)
}

// ---------- operations (each runs inside the caller's transaction) ----------

func (s *Service) createIn(ctx context.Context, tx Tx, scope Scope, kind Kind, raw json.RawMessage, id string, cols Columns) (Record, error) {
	existing, err := tx.Get(ctx, kind, id, true)
	if err == nil {
		return Record{}, &ConflictError{Kind: "exists", Current: &existing}
	}
	if !errors.Is(err, ErrNotFound) {
		return Record{}, err
	}
	if err := references(ctx, tx, kind, cols); err != nil {
		return Record{}, err
	}
	if kind == Lists && cols.IsDefault {
		exists, err := tx.DefaultListExists(ctx)
		if err != nil {
			return Record{}, err
		}
		if exists {
			return Record{}, ErrDefaultExists
		}
	}
	now := s.now().UTC()
	out := Record{ID: id, Data: raw, Version: 1, UpdatedAt: now}
	if err := tx.Insert(ctx, kind, out, cols, now); err != nil {
		return Record{}, err
	}
	return out, logUpsert(ctx, tx, scope, kind, out, now)
}

type completion struct {
	record CompletionRecord
	raw    json.RawMessage
}

func parseCompletion(kind Kind, id string, data json.RawMessage) (*completion, error) {
	if len(data) == 0 || string(data) == "null" {
		return nil, nil
	}
	if kind != Tasks {
		return nil, &ValidationError{Problems: []string{"append_completion is only for tasks"}}
	}
	c, err := ParseCompletion(data)
	if err != nil {
		return nil, err
	}
	if c.TaskID != id {
		return nil, &ValidationError{Problems: []string{"append_completion.taskId must be this task's id"}}
	}
	raw, err := json.Marshal(c)
	return &completion{record: c, raw: raw}, err
}

func (s *Service) patchIn(ctx context.Context, tx Tx, scope Scope, kind Kind, id string, expected int64, changes json.RawMessage, done *completion) (Record, error) {
	if expected <= 0 {
		return Record{}, ErrPrecondition
	}
	current, err := tx.Get(ctx, kind, id, true)
	if err != nil {
		return Record{}, err
	}
	if current.Deleted {
		return Record{}, ErrNotFound
	}
	if current.Version != expected {
		return Record{}, &ConflictError{Kind: "version", Current: &current}
	}
	if len(changes) == 0 {
		changes = json.RawMessage(`{}`)
	}
	merged, err := MergePatch(current.Data, changes)
	if err != nil {
		return Record{}, err
	}
	raw, newID, cols, err := s.parse(kind, merged)
	if err != nil {
		return Record{}, err
	}
	if newID != id {
		return Record{}, ErrIDImmutable
	}
	if kind == Lists {
		var before List
		_ = json.Unmarshal(current.Data, &before)
		if before.IsDefault != cols.IsDefault {
			return Record{}, ErrDefaultImmutable
		}
	}
	if err := references(ctx, tx, kind, cols); err != nil {
		return Record{}, err
	}
	now := s.now().UTC()
	if done != nil {
		completedAt, _ := time.Parse(time.RFC3339Nano, done.record.CompletedUtc)
		inserted, existing, err := tx.InsertCompletion(ctx, done.record, done.raw, completedAt, now)
		if err != nil {
			return Record{}, err
		}
		if !inserted && !sameJSON(existing, done.raw) {
			return Record{}, &ConflictError{Kind: "completion"}
		}
		if inserted {
			payload, err := json.Marshal(map[string]any{"resource_type": "completion", "action": "upsert", "resource_id": done.record.ID, "record": json.RawMessage(done.raw)})
			if err != nil {
				return Record{}, err
			}
			if err := tx.Log(ctx, Change{ResourceType: "completion", ResourceID: done.record.ID, Action: "upsert", Payload: payload, VisibleTo: scope.UserID}, now); err != nil {
				return Record{}, err
			}
		}
	}
	out := Record{ID: id, Data: raw, Version: current.Version + 1, UpdatedAt: now}
	if err := tx.Update(ctx, kind, out, cols, now); err != nil {
		return Record{}, err
	}
	return out, logUpsert(ctx, tx, scope, kind, out, now)
}

func (s *Service) deleteIn(ctx context.Context, tx Tx, scope Scope, kind Kind, id string, expected int64) error {
	if expected <= 0 {
		return ErrPrecondition
	}
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
			next := Record{ID: l.ID, Data: ungrouped, Version: l.Version + 1, UpdatedAt: now}
			if err := tx.Update(ctx, Lists, next, Columns{IsDefault: list.IsDefault}, now); err != nil {
				return err
			}
			if err := logUpsert(ctx, tx, scope, Lists, next, now); err != nil {
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
			if err := logDelete(ctx, tx, scope, Tasks, t.ID, t.Version+1, now); err != nil {
				return err
			}
		}
	}
	if err := tx.SoftDelete(ctx, kind, id, current.Version+1, now); err != nil {
		return err
	}
	return logDelete(ctx, tx, scope, kind, id, current.Version+1, now)
}

// ---------- the REST operations ----------

// Create stores a new resource with the client's id. An existing id (live or deleted) is a conflict that returns the
// server copy: a retry recognizes its own record, and a deleted record is never resurrected by accident.
func (s *Service) Create(ctx context.Context, scope Scope, kind Kind, data []byte) (Record, error) {
	raw, id, cols, err := s.parse(kind, data)
	if err != nil {
		return Record{}, err
	}
	var out Record
	err = s.store.InScope(ctx, scope, func(tx Tx) (err error) {
		out, err = s.createIn(ctx, tx, scope, kind, raw, id, cols)
		return err
	})
	return out, err
}

// Patch applies an RFC 7386 merge patch to the stored object if expected is the current version. For tasks, an
// optional completion record is appended in the same transaction (e.g. completing one occurrence of a repeating task
// and advancing its due date), idempotently by the record's id.
func (s *Service) Patch(ctx context.Context, scope Scope, kind Kind, id string, expected int64, changes json.RawMessage, completionData json.RawMessage) (Record, error) {
	if expected <= 0 {
		return Record{}, ErrPrecondition
	}
	done, err := parseCompletion(kind, id, completionData)
	if err != nil {
		return Record{}, err
	}
	var out Record
	err = s.store.InScope(ctx, scope, func(tx Tx) (err error) {
		out, err = s.patchIn(ctx, tx, scope, kind, id, expected, changes, done)
		return err
	})
	return out, err
}

// Delete soft-deletes (tombstone). Deleting a group ungroups its lists; deleting a list deletes its tasks; the default
// list cannot be deleted. Completion history is kept.
func (s *Service) Delete(ctx context.Context, scope Scope, kind Kind, id string, expected int64) error {
	if expected <= 0 {
		return ErrPrecondition
	}
	return s.store.InScope(ctx, scope, func(tx Tx) error { return s.deleteIn(ctx, tx, scope, kind, id, expected) })
}

// ---------- sync push ----------

// Mutation is one queued local write from a device's outbox.
type Mutation struct {
	MutationID       string          `json:"mutation_id"`
	ResourceType     string          `json:"resource_type"` // group, list, task
	Action           string          `json:"action"`        // create, patch, delete
	ResourceID       string          `json:"resource_id"`
	BaseVersion      int64           `json:"base_version,omitempty"` // patch, delete: the version the edit was made on
	Resource         json.RawMessage `json:"resource,omitempty"`     // create: the todo-v1 object
	Changes          json.RawMessage `json:"changes,omitempty"`      // patch: RFC 7386 merge patch
	AppendCompletion json.RawMessage `json:"append_completion,omitempty"`
}

// MutationResult: "applied" (with the new record, or none for a delete), "conflict" (with the server copy; nothing was
// changed), or "rejected" (invalid; nothing was changed). Applied results are stored per device and mutation id, so a
// retried batch returns the same results and never applies anything twice.
type MutationResult struct {
	MutationID     string          `json:"mutation_id"`
	Status         string          `json:"status"`
	Code           string          `json:"code,omitempty"`
	Message        string          `json:"message,omitempty"`
	Problems       []string        `json:"problems,omitempty"`
	Record         map[string]any  `json:"record,omitempty"`
	Current        map[string]any  `json:"current,omitempty"`
	CurrentDeleted bool            `json:"current_deleted,omitempty"`
	Replayed       bool            `json:"replayed,omitempty"`

}

const MaxPushBatch = 100

var mutationIDPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

// Push applies a batch of mutations in order, each in its own transaction (one failure does not undo the others).
// A membership loss stops the batch with ErrNotMember.
func (s *Service) Push(ctx context.Context, scope Scope, deviceID string, mutations []Mutation) ([]MutationResult, error) {
	results := make([]MutationResult, 0, len(mutations))
	for _, m := range mutations {
		result, err := s.pushOne(ctx, scope, deviceID, m)
		if err != nil {
			return results, err
		}
		results = append(results, result)
	}
	return results, nil
}

func (s *Service) pushOne(ctx context.Context, scope Scope, deviceID string, m Mutation) (MutationResult, error) {
	result := MutationResult{MutationID: m.MutationID}
	reject := func(code, message string, problems ...string) (MutationResult, error) {
		result.Status, result.Code, result.Message, result.Problems = "rejected", code, message, problems
		return result, nil
	}
	if !mutationIDPattern.MatchString(m.MutationID) {
		return reject("INVALID_MUTATION", "mutation_id must be 1-64 letters, digits, '-' or '_'")
	}
	kind, ok := ParseKind(m.ResourceType)
	if !ok {
		return reject("INVALID_MUTATION", "resource_type must be group, list, or task")
	}
	var raw json.RawMessage
	var cols Columns
	var done *completion
	switch m.Action {
	case "create":
		var id string
		var err error
		raw, id, cols, err = s.parse(kind, m.Resource)
		if err != nil {
			return s.domainResult(result, kind, err)
		}
		if id != m.ResourceID {
			return reject("INVALID_MUTATION", "resource_id must equal the resource's id")
		}
	case "patch", "delete":
		if m.BaseVersion <= 0 {
			return reject("INVALID_MUTATION", "base_version is required for patch and delete")
		}
		var err error
		if done, err = parseCompletion(kind, m.ResourceID, m.AppendCompletion); err != nil {
			return s.domainResult(result, kind, err)
		}
	default:
		return reject("INVALID_MUTATION", "action must be create, patch, or delete")
	}

	err := s.store.InScope(ctx, scope, func(tx Tx) error {
		if stored, ok, err := tx.Receipt(ctx, deviceID, m.MutationID); err != nil {
			return err
		} else if ok {
			if err := json.Unmarshal(stored, &result); err != nil {
				return err
			}
			result.Replayed = true
			return nil
		}
		var record *Record
		switch m.Action {
		case "create":
			r, err := s.createIn(ctx, tx, scope, kind, raw, m.ResourceID, cols)
			if err != nil {
				return err
			}
			record = &r
		case "patch":
			r, err := s.patchIn(ctx, tx, scope, kind, m.ResourceID, m.BaseVersion, m.Changes, done)
			if err != nil {
				return err
			}
			record = &r
		case "delete":
			if err := s.deleteIn(ctx, tx, scope, kind, m.ResourceID, m.BaseVersion); err != nil {
				return err
			}
		}
		result.Status = "applied"
		if record != nil {
			result.Record = Envelope(kind, *record)
		}
		stored, err := json.Marshal(result)
		if err != nil {
			return err
		}
		return tx.SaveReceipt(ctx, deviceID, m.MutationID, stored, s.now().UTC())
	})
	if err != nil {
		if errors.Is(err, ErrNotMember) {
			return result, err
		}
		return s.domainResult(result, kind, err)
	}
	return result, nil
}

// domainResult turns a domain error into a per-mutation result; anything else is a server error for the whole batch.
func (s *Service) domainResult(result MutationResult, kind Kind, err error) (MutationResult, error) {
	var validation *ValidationError
	var conflict *ConflictError
	var reference *ReferenceError
	switch {
	case errors.As(err, &conflict):
		result.Status = "conflict"
		result.Code = map[string]string{"version": "VERSION_CONFLICT", "exists": "ALREADY_EXISTS", "completion": "COMPLETION_CONFLICT"}[conflict.Kind]
		if conflict.Current != nil {
			result.Current = Envelope(kind, *conflict.Current)
			result.CurrentDeleted = conflict.Current.Deleted
		}
	case errors.Is(err, ErrNotFound):
		result.Status, result.Code = "conflict", "NOT_FOUND" // deleted elsewhere: an edit/delete conflict to surface
		result.CurrentDeleted = true
	case errors.As(err, &validation):
		result.Status, result.Code, result.Problems = "rejected", "VALIDATION_FAILED", validation.Problems
	case errors.As(err, &reference):
		result.Status, result.Code, result.Message = "rejected", "INVALID_REFERENCE", reference.Error()
	case errors.Is(err, ErrDefaultList), errors.Is(err, ErrDefaultExists), errors.Is(err, ErrDefaultImmutable), errors.Is(err, ErrIDImmutable):
		result.Status, result.Code, result.Message = "rejected", "INVALID_MUTATION", err.Error()
	default:
		return result, err
	}
	return result, nil
}

// ---------- history and export ----------

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
			doc[kind.String()+"s"] = items
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

func sameJSON(a, b json.RawMessage) bool {
	var x, y any
	if json.Unmarshal(a, &x) != nil || json.Unmarshal(b, &y) != nil {
		return false
	}
	ax, _ := json.Marshal(x)
	by, _ := json.Marshal(y)
	return string(ax) == string(by)
}
