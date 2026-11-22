// Package cloudsync serves the pull side of sync (docs/BACKEND_SPEC.md §10): a paged bootstrap snapshot with a
// watermark, and an incremental change feed read from the change log with an opaque, signed cursor bound to the user
// and the space. The push side (idempotent mutation batches) is tasks.Service.Push.
package cloudsync

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"time"

	"alondra1/backend/internal/tasks"
)

var (
	ErrInvalidCursor = errors.New("the cursor is not valid for this user and space")
	ErrCursorExpired = errors.New("the cursor is older than the retained change log")
)

// Stages of a bootstrap, in order. Lists come after groups and tasks after lists, so a client can apply items as they
// arrive without dangling references.
const (
	StageGroups = iota
	StageLists
	StageTasks
	StageCompletions
	StageUserConfigs
	StageTenantConfigs
	stageDone
)

type Position struct {
	Stage int    `json:"g"`
	After string `json:"a"`
}

type Entry struct {
	Sequence int64
	Payload  json.RawMessage
}

type Tx interface {
	MaxSequence(ctx context.Context) (int64, error)
	Floor(ctx context.Context) (int64, error)
	// Changes returns entries visible to the scope's user with after < sequence <= upTo, in order.
	Changes(ctx context.Context, after, upTo int64, limit int) ([]Entry, error)
	// Snapshot returns up to limit current items from pos on, and the next position (nil when done).
	Snapshot(ctx context.Context, pos Position, limit int) ([]json.RawMessage, *Position, error)
}

type Store interface {
	InScope(ctx context.Context, scope tasks.Scope, fn func(Tx) error) error
}

const (
	DefaultLimit = 500
	MaxLimit     = 1000
)

type Service struct {
	store    Store
	key      []byte
	previous [][]byte // verification only, during a token-key rotation
}

// NewService derives the cursor-signing key from the server's token key, so cursors cannot be forged or moved to
// another user or space.
func NewService(store Store, tokenKey []byte, previousTokenKeys ...[]byte) *Service {
	s := &Service{store: store, key: deriveCursorKey(tokenKey)}
	for _, k := range previousTokenKeys {
		if len(k) > 0 {
			s.previous = append(s.previous, deriveCursorKey(k))
		}
	}
	return s
}

func deriveCursorKey(tokenKey []byte) []byte {
	mac := hmac.New(sha256.New, tokenKey)
	mac.Write([]byte("alondra1 sync cursor v1"))
	return mac.Sum(nil)
}

type token struct {
	Tenant    string    `json:"t"`
	User      string    `json:"u"`
	Sequence  int64     `json:"s"`
	Position  *Position `json:"p,omitempty"` // bootstrap page tokens only
	Issued    int64     `json:"i"`
}

func (s *Service) sign(t token) string {
	payload, _ := json.Marshal(t)
	body := base64.RawURLEncoding.EncodeToString(payload)
	mac := hmac.New(sha256.New, s.key)
	mac.Write([]byte(body))
	return body + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
}

func (s *Service) verify(value string, scope tasks.Scope, wantPosition bool) (token, error) {
	body, signature, ok := strings.Cut(value, ".")
	if !ok || len(value) > 2048 {
		return token{}, ErrInvalidCursor
	}
	got, err := base64.RawURLEncoding.DecodeString(signature)
	if err != nil {
		return token{}, ErrInvalidCursor
	}
	valid := false
	for _, key := range append([][]byte{s.key}, s.previous...) {
		mac := hmac.New(sha256.New, key)
		mac.Write([]byte(body))
		valid = valid || hmac.Equal(got, mac.Sum(nil))
	}
	if !valid {
		return token{}, ErrInvalidCursor
	}
	payload, err := base64.RawURLEncoding.DecodeString(body)
	var t token
	if err != nil || json.Unmarshal(payload, &t) != nil || t.Tenant != scope.TenantID || t.User != scope.UserID || t.Sequence < 0 || (t.Position != nil) != wantPosition {
		return token{}, ErrInvalidCursor
	}
	return t, nil
}

func clamp(limit int) int {
	if limit <= 0 || limit > MaxLimit {
		return DefaultLimit
	}
	return limit
}

type BootstrapPage struct {
	Items         []json.RawMessage
	NextPageToken string // "" on the last page
	Cursor        string // the watermark: after the last page, read /changes from here
}

// Bootstrap returns the current state in pages. The first call fixes a watermark (the newest change-log sequence);
// every later change has a higher sequence and arrives through Changes, so nothing is missed even if records change
// while the pages are read (a record may then arrive twice; clients apply by version).
func (s *Service) Bootstrap(ctx context.Context, scope tasks.Scope, pageToken string, limit int) (BootstrapPage, error) {
	limit = clamp(limit)
	var page BootstrapPage
	err := s.store.InScope(ctx, scope, func(tx Tx) error {
		t := token{Tenant: scope.TenantID, User: scope.UserID, Position: &Position{Stage: StageGroups}}
		if pageToken == "" {
			watermark, err := tx.MaxSequence(ctx)
			if err != nil {
				return err
			}
			t.Sequence = watermark
		} else {
			var err error
			if t, err = s.verify(pageToken, scope, true); err != nil {
				return err
			}
			floor, err := tx.Floor(ctx)
			if err != nil {
				return err
			}
			if t.Sequence < floor {
				return ErrCursorExpired
			}
		}
		items, next, err := tx.Snapshot(ctx, *t.Position, limit)
		if err != nil {
			return err
		}
		page.Items = items
		if next != nil {
			page.NextPageToken = s.sign(token{Tenant: t.Tenant, User: t.User, Sequence: t.Sequence, Position: next, Issued: time.Now().Unix()})
		}
		page.Cursor = s.sign(token{Tenant: t.Tenant, User: t.User, Sequence: t.Sequence, Issued: time.Now().Unix()})
		return nil
	})
	return page, err
}

type ChangesPage struct {
	Entries []Entry
	Cursor  string
	HasMore bool
}

// Changes returns what changed after the cursor. Entries are ordered by sequence; deletions are tombstones. The new
// cursor also moves past entries this user may not see, so a client never scans other members' activity twice.
func (s *Service) Changes(ctx context.Context, scope tasks.Scope, cursor string, limit int) (ChangesPage, error) {
	limit = clamp(limit)
	t, err := s.verify(cursor, scope, false)
	if err != nil {
		return ChangesPage{}, err
	}
	var page ChangesPage
	err = s.store.InScope(ctx, scope, func(tx Tx) error {
		floor, err := tx.Floor(ctx)
		if err != nil {
			return err
		}
		if t.Sequence < floor {
			return ErrCursorExpired
		}
		upTo, err := tx.MaxSequence(ctx)
		if err != nil {
			return err
		}
		entries, err := tx.Changes(ctx, t.Sequence, upTo, limit)
		if err != nil {
			return err
		}
		next := upTo
		if len(entries) == limit {
			next = entries[len(entries)-1].Sequence
			page.HasMore = next < upTo
		}
		page.Entries = entries
		page.Cursor = s.sign(token{Tenant: scope.TenantID, User: scope.UserID, Sequence: next, Issued: time.Now().Unix()})
		return nil
	})
	return page, err
}
