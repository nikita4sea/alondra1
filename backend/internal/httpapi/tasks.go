package httpapi

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"
	"time"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/settings"
	"alondra1/backend/internal/tasks"
)

// Task resources can carry long notes, so their bodies may be larger than auth requests.
const maxResourceBody = tasks.MaxResourceSize + 4<<10

// Export limit for the synchronous export (spec §9: "synchronous small export MVP").
const exportMaxTasks = 50_000

type resourceRoute struct {
	path, single, plural string
	kind                 tasks.Kind
}

var resourceRoutes = []resourceRoute{
	{"/v1/list-groups", "group", "groups", tasks.Groups},
	{"/v1/task-lists", "list", "lists", tasks.Lists},
	{"/v1/tasks", "task", "tasks", tasks.Tasks},
}

func (s *Server) taskRoutes(r *router) {
	r.handle("GET", "/v1/tasks/completion-history", s.inSpace(s.history))
	for _, route := range resourceRoutes {
		route := route
		r.handle("GET", route.path, s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.listResources(w, req, scope, route) }))
		r.handle("POST", route.path, s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.createResource(w, req, scope, route) }))
		r.handle("GET", route.path+"/{id}", s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.getResource(w, req, scope, route) }))
		r.handle("PATCH", route.path+"/{id}", s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.patchResource(w, req, scope, route) }))
		r.handle("DELETE", route.path+"/{id}", s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.deleteResource(w, req, scope, route) }))
	}
	for _, level := range []struct {
		path  string
		level settings.Level
	}{{"/v1/config/user/{namespace}", settings.User}, {"/v1/config/tenant/{namespace}", settings.Space}} {
		level := level
		r.handle("GET", level.path, s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.getSettings(w, req, scope, level.level) }))
		r.handle("PUT", level.path, s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.writeSettings(w, req, scope, level.level, false) }))
		r.handle("PATCH", level.path, s.inSpace(func(w http.ResponseWriter, req *http.Request, scope tasks.Scope) { s.writeSettings(w, req, scope, level.level, true) }))
	}
	r.handle("GET", "/v1/data/export", s.authenticated(s.export))
}

// inSpace authenticates the caller and takes the space from X-Tenant-ID. Membership is checked inside each
// transaction by the store (never cached), so a removed member loses access at once.
func (s *Server) inSpace(next func(http.ResponseWriter, *http.Request, tasks.Scope)) http.Handler {
	return s.authenticated(func(w http.ResponseWriter, r *http.Request, p auth.Principal) {
		tenantID := strings.TrimSpace(r.Header.Get("X-Tenant-ID"))
		if tenantID == "" {
			writeError(w, r, http.StatusBadRequest, "TENANT_REQUIRED", "Send the space id in the X-Tenant-ID header.")
			return
		}
		next(w, r, tasks.Scope{TenantID: tenantID, UserID: p.UserID})
	})
}

// envelope is the API shape of a record (shared with sync).
func envelope(single string, rec tasks.Record) map[string]any {
	kind, _ := tasks.ParseKind(single)
	if single == "record" || single == "settings" || single == "export" {
		return map[string]any{"id": rec.ID, "version": rec.Version, "data": rec.Data}
	}
	return tasks.Envelope(kind, rec)
}

func setETag(w http.ResponseWriter, version int64) {
	w.Header().Set("ETag", `"`+strconv.FormatInt(version, 10)+`"`)
}

// ifMatch reads the expected version from If-Match ("3", 3, or W/"3"); -1 when missing or malformed.
func ifMatch(r *http.Request) int64 {
	value := strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(r.Header.Get("If-Match")), "W/"))
	value = strings.Trim(value, `"`)
	version, err := strconv.ParseInt(value, 10, 64)
	if err != nil || version < 0 {
		return -1
	}
	return version
}

func (s *Server) listResources(w http.ResponseWriter, r *http.Request, scope tasks.Scope, route resourceRoute) {
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	q := tasks.Query{After: r.URL.Query().Get("after"), Limit: limit}
	if route.kind == tasks.Tasks {
		q.ListID = r.URL.Query().Get("list_id")
	}
	records, err := s.Tasks.List(r.Context(), scope, route.kind, q)
	if err != nil {
		s.taskError(w, r, err, route.single)
		return
	}
	items := make([]map[string]any, 0, len(records))
	for _, rec := range records {
		items = append(items, envelope(route.single, rec))
	}
	body := map[string]any{route.plural: items}
	pageSize := q.Limit
	if pageSize <= 0 || pageSize > tasks.MaxPageSize {
		pageSize = tasks.DefaultPageSize
	}
	if len(records) == pageSize {
		body["next_after"] = records[len(records)-1].ID // more may follow: ?after=<next_after>
	}
	writeJSON(w, http.StatusOK, body)
}

func (s *Server) getResource(w http.ResponseWriter, r *http.Request, scope tasks.Scope, route resourceRoute) {
	rec, err := s.Tasks.Get(r.Context(), scope, route.kind, r.PathValue("id"))
	if err != nil {
		s.taskError(w, r, err, route.single)
		return
	}
	setETag(w, rec.Version)
	writeJSON(w, http.StatusOK, envelope(route.single, rec))
}

func (s *Server) createResource(w http.ResponseWriter, r *http.Request, scope tasks.Scope, route resourceRoute) {
	var body map[string]json.RawMessage
	if !decodeLimit(w, r, &body, maxResourceBody) {
		return
	}
	raw, ok := body[route.single]
	if !ok || len(body) != 1 {
		writeError(w, r, http.StatusBadRequest, "MALFORMED_JSON", `Send {"`+route.single+`": {...}}.`)
		return
	}
	rec, err := s.Tasks.Create(r.Context(), scope, route.kind, raw)
	if err != nil {
		s.taskError(w, r, err, route.single)
		return
	}
	setETag(w, rec.Version)
	writeJSON(w, http.StatusCreated, envelope(route.single, rec))
}

type patchRequest struct {
	Changes          json.RawMessage `json:"changes"`
	AppendCompletion json.RawMessage `json:"append_completion"`
}

func (s *Server) patchResource(w http.ResponseWriter, r *http.Request, scope tasks.Scope, route resourceRoute) {
	var body patchRequest
	if !decodeLimit(w, r, &body, maxResourceBody) {
		return
	}
	if len(body.Changes) == 0 {
		body.Changes = json.RawMessage(`{}`)
	}
	rec, err := s.Tasks.Patch(r.Context(), scope, route.kind, r.PathValue("id"), ifMatch(r), body.Changes, body.AppendCompletion)
	if err != nil {
		s.taskError(w, r, err, route.single)
		return
	}
	setETag(w, rec.Version)
	writeJSON(w, http.StatusOK, envelope(route.single, rec))
}

func (s *Server) deleteResource(w http.ResponseWriter, r *http.Request, scope tasks.Scope, route resourceRoute) {
	if err := s.Tasks.Delete(r.Context(), scope, route.kind, r.PathValue("id"), ifMatch(r)); err != nil {
		s.taskError(w, r, err, route.single)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func encodeCursor(c *tasks.Cursor) string {
	if c == nil {
		return ""
	}
	return base64.RawURLEncoding.EncodeToString([]byte(c.CompletedAt.UTC().Format(time.RFC3339Nano) + "|" + c.ID))
}

func decodeCursor(value string) (*tasks.Cursor, bool) {
	if value == "" {
		return nil, true
	}
	raw, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil {
		return nil, false
	}
	at, id, ok := strings.Cut(string(raw), "|")
	when, err := time.Parse(time.RFC3339Nano, at)
	if !ok || err != nil {
		return nil, false
	}
	return &tasks.Cursor{CompletedAt: when, ID: id}, true
}

func (s *Server) history(w http.ResponseWriter, r *http.Request, scope tasks.Scope) {
	before, ok := decodeCursor(r.URL.Query().Get("before"))
	if !ok {
		writeError(w, r, http.StatusBadRequest, "INVALID_CURSOR", "The cursor is not valid; start without one.")
		return
	}
	limit, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	items, next, err := s.Tasks.History(r.Context(), scope, before, limit)
	if err != nil {
		s.taskError(w, r, err, "record")
		return
	}
	if items == nil {
		items = []json.RawMessage{}
	}
	body := map[string]any{"history": items}
	if next != nil {
		body["next_before"] = encodeCursor(next)
	}
	writeJSON(w, http.StatusOK, body)
}

func settingsJSON(d settings.Doc) map[string]any { return settings.Record(d) }

func (s *Server) getSettings(w http.ResponseWriter, r *http.Request, scope tasks.Scope, level settings.Level) {
	doc, err := s.Settings.Get(r.Context(), scope, level, r.PathValue("namespace"))
	if err != nil {
		s.taskError(w, r, err, "settings")
		return
	}
	setETag(w, doc.Version)
	writeJSON(w, http.StatusOK, settingsJSON(doc))
}

func (s *Server) writeSettings(w http.ResponseWriter, r *http.Request, scope tasks.Scope, level settings.Level, patch bool) {
	var body map[string]json.RawMessage
	if !decode(w, r, &body) {
		return
	}
	key := "data"
	if patch {
		key = "changes"
	}
	payload, ok := body[key]
	if !ok || len(body) != 1 {
		writeError(w, r, http.StatusBadRequest, "MALFORMED_JSON", `Send {"`+key+`": {...}}.`)
		return
	}
	expected := ifMatch(r)
	if expected < 0 {
		s.taskError(w, r, tasks.ErrPrecondition, "settings")
		return
	}
	doc, err := s.Settings.Write(r.Context(), scope, level, r.PathValue("namespace"), expected, payload, patch)
	if err != nil {
		s.taskError(w, r, err, "settings")
		return
	}
	setETag(w, doc.Version)
	writeJSON(w, http.StatusOK, settingsJSON(doc))
}

// export returns everything the cloud holds for the caller: profile, spaces, and per space the caller's own tasks (as
// an alondra1-todo document the Windows app can import) and personal settings. Other members' data is never included.
func (s *Server) export(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	user, err := s.Auth.User(r.Context(), p.UserID)
	if err != nil {
		s.internal(w, r, err)
		return
	}
	views, err := s.Tenancy.List(r.Context(), p.UserID)
	if err != nil {
		s.internal(w, r, err)
		return
	}
	spaces := make([]map[string]any, 0, len(views))
	for _, v := range views {
		scope := tasks.Scope{TenantID: v.ID, UserID: p.UserID}
		todo, err := s.Tasks.Export(r.Context(), scope, exportMaxTasks)
		if err != nil {
			s.taskError(w, r, err, "export")
			return
		}
		personal := map[string]json.RawMessage{}
		for _, name := range settings.UserNamespaces() {
			doc, err := s.Settings.Get(r.Context(), scope, settings.User, name)
			if err != nil {
				s.taskError(w, r, err, "export")
				return
			}
			if doc.Version > 0 {
				personal[name] = doc.Data
			}
		}
		space := tenantJSON(v)
		space["todo"] = todo
		space["settings"] = personal
		spaces = append(spaces, space)
	}
	w.Header().Set("Content-Disposition", `attachment; filename="alondra1-export.json"`)
	writeJSON(w, http.StatusOK, map[string]any{
		"format": "alondra1-account-export", "version": 1, "exported_at": time.Now().UTC().Format(time.RFC3339),
		"account": map[string]any{"id": user.ID, "email": user.EmailNormalized, "created_at": user.CreatedAt.UTC().Format(time.RFC3339)},
		"spaces":  spaces,
		"not_included": "Task attachment files stay on your devices; the cloud stores only their names and sizes.",
	})
}

func (s *Server) taskError(w http.ResponseWriter, r *http.Request, err error, single string) {
	var validation *tasks.ValidationError
	var conflict *tasks.ConflictError
	var reference *tasks.ReferenceError
	switch {
	case errors.Is(err, tasks.ErrNotMember):
		writeError(w, r, http.StatusNotFound, "TENANT_NOT_FOUND", "No such space, or you are not a member.")
	case errors.Is(err, tasks.ErrNotFound):
		writeError(w, r, http.StatusNotFound, "NOT_FOUND", "No such "+single+".")
	case errors.As(err, &validation):
		writeJSON(w, http.StatusUnprocessableEntity, map[string]any{"error": map[string]any{
			"code": "VALIDATION_FAILED", "message": "The " + single + " is not valid.", "request_id": requestID(r), "problems": validation.Problems}})
	case errors.As(err, &reference):
		writeError(w, r, http.StatusUnprocessableEntity, "INVALID_REFERENCE", reference.Field+" must name one of your lists or groups in this space.")
	case errors.As(err, &conflict):
		code, message := "VERSION_CONFLICT", "The "+single+" changed since you read it. Keep your copy aside and decide."
		switch conflict.Kind {
		case "exists":
			code, message = "ALREADY_EXISTS", "A "+single+" with this id already exists (or was deleted)."
		case "completion":
			code, message = "COMPLETION_CONFLICT", "A different completion record with this id already exists."
		}
		e := map[string]any{"code": code, "message": message, "request_id": requestID(r)}
		if conflict.Current != nil {
			e["current"] = envelope(single, *conflict.Current)
			e["current_deleted"] = conflict.Current.Deleted
		}
		writeJSON(w, http.StatusConflict, map[string]any{"error": e})
	case errors.Is(err, tasks.ErrPrecondition):
		writeError(w, r, http.StatusPreconditionRequired, "PRECONDITION_REQUIRED", `Send If-Match with the version you last read (use "0" for settings never written).`)
	case errors.Is(err, tasks.ErrDefaultList):
		writeError(w, r, http.StatusConflict, "DEFAULT_LIST", "The default Tasks list cannot be deleted.")
	case errors.Is(err, tasks.ErrDefaultExists):
		writeError(w, r, http.StatusConflict, "DEFAULT_LIST_EXISTS", "There is already a default list.")
	case errors.Is(err, tasks.ErrDefaultImmutable), errors.Is(err, tasks.ErrIDImmutable):
		writeError(w, r, http.StatusUnprocessableEntity, "IMMUTABLE_FIELD", err.Error()+".")
	case errors.Is(err, settings.ErrUnknownNamespace):
		writeError(w, r, http.StatusNotFound, "UNKNOWN_NAMESPACE", "No such settings namespace.")
	case errors.Is(err, settings.ErrForbidden):
		writeError(w, r, http.StatusForbidden, "FORBIDDEN", "Only owners and admins change space settings.")
	case errors.Is(err, tasks.ErrTooLarge):
		writeError(w, r, http.StatusRequestEntityTooLarge, "EXPORT_TOO_LARGE", "Too much data for a direct export.")
	default:
		s.internal(w, r, err)
	}
}
