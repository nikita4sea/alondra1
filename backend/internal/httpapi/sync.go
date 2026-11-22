package httpapi

import (
	"encoding/json"
	"errors"
	"net/http"
	"strconv"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/cloudsync"
	"alondra1/backend/internal/tasks"
)

// A push carries at most 100 mutations; each resource is at most 512 KiB, but whole batches are capped lower.
const maxPushBody = 8 << 20

func (s *Server) syncRoutes(r *router) {
	r.handle("GET", "/v1/sync/bootstrap", s.inSpace(s.bootstrap))
	r.handle("GET", "/v1/sync/changes", s.inSpace(s.changes))
	r.handle("POST", "/v1/sync/push", s.authenticated(func(w http.ResponseWriter, req *http.Request, p auth.Principal) {
		s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.push(w, req, scope, p) }).ServeHTTP(w, req)
	}))
}

func (s *Server) bootstrap(w http.ResponseWriter, r *http.Request, scope tasks.Scope) {
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	page, err := s.Sync.Bootstrap(r.Context(), scope, r.URL.Query().Get("page_token"), limit)
	if err != nil {
		s.syncError(w, r, err)
		return
	}
	items := page.Items
	if items == nil {
		items = []json.RawMessage{}
	}
	body := map[string]any{"items": items, "cursor": page.Cursor}
	if page.NextPageToken != "" {
		body["next_page_token"] = page.NextPageToken
	}
	writeJSON(w, http.StatusOK, body)
}

func (s *Server) changes(w http.ResponseWriter, r *http.Request, scope tasks.Scope) {
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	page, err := s.Sync.Changes(r.Context(), scope, r.URL.Query().Get("cursor"), limit)
	if err != nil {
		s.syncError(w, r, err)
		return
	}
	entries := make([]json.RawMessage, 0, len(page.Entries))
	for _, e := range page.Entries {
		// The stored payload plus its sequence number.
		var item map[string]json.RawMessage
		if err := json.Unmarshal(e.Payload, &item); err != nil {
			s.internal(w, r, err)
			return
		}
		item["sequence"] = json.RawMessage(strconv.FormatInt(e.Sequence, 10))
		raw, _ := json.Marshal(item)
		entries = append(entries, raw)
	}
	writeJSON(w, http.StatusOK, map[string]any{"changes": entries, "cursor": page.Cursor, "has_more": page.HasMore})
}

type pushRequest struct {
	Mutations []tasks.Mutation `json:"mutations"`
}

func (s *Server) push(w http.ResponseWriter, r *http.Request, scope tasks.Scope, p auth.Principal) {
	var body pushRequest
	if !decodeLimit(w, r, &body, maxPushBody) {
		return
	}
	if len(body.Mutations) == 0 || len(body.Mutations) > tasks.MaxPushBatch {
		writeError(w, r, http.StatusUnprocessableEntity, "INVALID_BATCH", "Send 1 to 100 mutations per request.")
		return
	}
	results, err := s.Tasks.Push(r.Context(), scope, p.DeviceID, body.Mutations)
	for _, result := range results {
		s.Metrics.Inc("alondra1_sync_push_mutations_total", "status", result.Status)
	}
	if err != nil {
		s.syncError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"results": results})
}

func (s *Server) syncError(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, cloudsync.ErrInvalidCursor):
		writeError(w, r, http.StatusBadRequest, "INVALID_CURSOR", "The cursor or page token is not valid for this user and space. Bootstrap again.")
	case errors.Is(err, cloudsync.ErrCursorExpired):
		writeError(w, r, http.StatusGone, "CURSOR_EXPIRED", "The cursor is too old. Bootstrap again; keep your unsent changes and push them afterwards.")
	default:
		s.taskError(w, r, err, "record")
	}
}
