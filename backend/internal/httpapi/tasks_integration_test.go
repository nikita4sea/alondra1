//go:build integration

package httpapi_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

// space sends X-Tenant-ID with every request.
type space struct {
	w        *world
	tenantID string
}

func (s space) do(t *testing.T, method, path, token string, body any, headers ...string) response {
	t.Helper()
	return s.w.do(t, method, path, token, body, append([]string{"X-Tenant-ID", s.tenantID}, headers...)...)
}

func etag(r response) string { return r.header.Get("ETag") }

func windowsFixture(t *testing.T) map[string][]json.RawMessage {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "packages", "shared-models", "fixtures", "todo-v1", "full-document.json"))
	if err != nil {
		t.Fatal(err)
	}
	var doc map[string]json.RawMessage
	_ = json.Unmarshal(data, &doc)
	out := map[string][]json.RawMessage{}
	for _, key := range []string{"groups", "lists", "tasks", "history"} {
		var items []json.RawMessage
		_ = json.Unmarshal(doc[key], &items)
		out[key] = items
	}
	return out
}

func field(t *testing.T, r response, path ...string) any {
	t.Helper()
	var current any = r.body
	for _, key := range path {
		m, ok := current.(map[string]any)
		if !ok {
			t.Fatalf("no %v in %v", path, r.body)
		}
		current = m[key]
	}
	return current
}

func TestTasksAndSettingsEndToEnd(t *testing.T) {
	w := newWorld(t)
	maya, sam, ali, eve := w.signIn(t, "maya@example.com"), w.signIn(t, "sam@example.com"), w.signIn(t, "ali@example.com"), w.signIn(t, "eve@example.com")
	familyID := w.expect(t, w.do(t, "POST", "/v1/tenants", maya, map[string]string{"type": "family", "name": "The Parks"}), 201, "").body["id"].(string)
	personalID := w.expect(t, w.do(t, "POST", "/v1/tenants", maya, map[string]string{"type": "personal"}), 201, "").body["id"].(string)
	w.expect(t, w.do(t, "POST", "/v1/invites/"+w.invite(t, maya, familyID, "sam@example.com", "member")+"/accept", sam, nil), 200, "")
	w.expect(t, w.do(t, "POST", "/v1/invites/"+w.invite(t, maya, familyID, "ali@example.com", "admin")+"/accept", ali, nil), 200, "")
	family := space{w, familyID}

	t.Run("a space is required and must be the caller's", func(t *testing.T) {
		w.expect(t, w.do(t, "GET", "/v1/tasks", maya, nil), 400, "TENANT_REQUIRED")
		w.expect(t, family.do(t, "GET", "/v1/tasks", eve, nil), 404, "TENANT_NOT_FOUND")
		w.expect(t, space{w, "00000000-0000-7000-8000-000000000000"}.do(t, "GET", "/v1/tasks", maya, nil), 404, "TENANT_NOT_FOUND")
		w.expect(t, space{w, "not-a-uuid"}.do(t, "GET", "/v1/task-lists", maya, nil), 404, "TENANT_NOT_FOUND")
	})

	doc := windowsFixture(t)
	t.Run("the Windows data enrolls with its ids and values intact", func(t *testing.T) {
		for _, step := range []struct{ path, key string }{{"/v1/list-groups", "group"}, {"/v1/task-lists", "list"}, {"/v1/tasks", "task"}} {
			for _, item := range doc[step.key+"s"] {
				r := w.expect(t, family.do(t, "POST", step.path, maya, map[string]json.RawMessage{step.key: item}), 201, "")
				if etag(r) != `"1"` || r.body["version"].(float64) != 1 {
					t.Fatalf("new resources start at version 1: %v", r.body)
				}
			}
		}
		tasks := w.expect(t, family.do(t, "GET", "/v1/tasks", maya, nil), 200, "").body["tasks"].([]any)
		if len(tasks) != len(doc["tasks"]) {
			t.Fatalf("tasks: %d", len(tasks))
		}
		got := w.expect(t, family.do(t, "GET", "/v1/tasks/task-month-end", maya, nil), 200, "")
		task := field(t, got, "task").(map[string]any)
		if task["dueDate"] != "2026-03-31" || task["dueTime"] != "16:00:00" || task["createdUtc"] != "2026-01-05T08:30:00+00:00" ||
			task["recurrence"].(map[string]any)["anchor"] != "2026-01-31" {
			t.Fatalf("values changed: %v", task)
		}
		reminder := task["reminders"].([]any)[0].(map[string]any)
		if reminder["at"] != "2026-03-29T02:30:00" || reminder["deliveredFor"] != nil {
			t.Fatalf("reminder: wall clock kept, delivery state dropped: %v", reminder)
		}
		steps := task["subtasks"].([]any)
		if steps[0].(map[string]any)["id"] != "step-1" || steps[2].(map[string]any)["id"] != "step-3" {
			t.Fatal("step order changed")
		}
		launch := w.expect(t, family.do(t, "GET", "/v1/task-lists/list-launch", maya, nil), 200, "")
		if field(t, launch, "list", "name") != "🚀 Launch" || field(t, launch, "list", "groupId") != "group-work" {
			t.Fatalf("list: %v", launch.body)
		}
		w.expect(t, family.do(t, "GET", "/v1/tasks?list_id=list-launch", maya, nil), 200, "")
		w.expect(t, family.do(t, "POST", "/v1/task-lists", maya, map[string]any{"list": map[string]any{"id": "second-default", "name": "Tasks 2", "isDefault": true}}), 409, "DEFAULT_LIST_EXISTS")
	})

	t.Run("tasks are private to their owner, also towards admins", func(t *testing.T) {
		if tasks := w.expect(t, family.do(t, "GET", "/v1/tasks", sam, nil), 200, "").body["tasks"].([]any); len(tasks) != 0 {
			t.Fatalf("sam sees maya's tasks: %v", tasks)
		}
		w.expect(t, family.do(t, "GET", "/v1/tasks/task-month-end", sam, nil), 404, "NOT_FOUND")
		w.expect(t, family.do(t, "GET", "/v1/tasks/task-month-end", ali, nil), 404, "NOT_FOUND")
		w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-month-end", ali, map[string]any{"changes": map[string]any{"title": "pwned"}}, "If-Match", `"1"`), 404, "NOT_FOUND")
		w.expect(t, family.do(t, "DELETE", "/v1/tasks/task-month-end", ali, nil, "If-Match", `"1"`), 404, "NOT_FOUND")
		// The same client id in another user's data is a different record.
		w.expect(t, family.do(t, "POST", "/v1/task-lists", sam, map[string]any{"list": map[string]any{"id": "list-launch", "name": "Sam's list"}}), 201, "")
		w.expect(t, family.do(t, "POST", "/v1/tasks", sam, map[string]any{"task": map[string]any{"id": "task-month-end", "listId": "list-launch", "title": "Sam's own"}}), 201, "")
		if field(t, w.expect(t, family.do(t, "GET", "/v1/tasks/task-month-end", maya, nil), 200, ""), "task", "title") != "Close the books" {
			t.Fatal("sam's record must not affect maya's")
		}
		// The same ids in maya's other space do not exist.
		w.expect(t, space{w, personalID}.do(t, "GET", "/v1/tasks/task-month-end", maya, nil), 404, "NOT_FOUND")
		// A list id that exists only for another user cannot be referenced.
		w.expect(t, family.do(t, "POST", "/v1/task-lists", sam, map[string]any{"list": map[string]any{"id": "sams-only", "name": "Mine"}}), 201, "")
		w.expect(t, family.do(t, "POST", "/v1/tasks", maya, map[string]any{"task": map[string]any{"id": "sneaky", "listId": "sams-only", "title": "x"}}), 422, "INVALID_REFERENCE")
	})

	t.Run("versions protect against silent overwrites", func(t *testing.T) {
		w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-weekly", maya, map[string]any{"changes": map[string]any{"title": "No version"}}), 428, "PRECONDITION_REQUIRED")
		updated := w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-weekly", maya, map[string]any{"changes": map[string]any{"title": "Water the plants (balcony)", "myDayDate": nil}}, "If-Match", `"1"`), 200, "")
		if etag(updated) != `"2"` || field(t, updated, "task", "myDayDate") != nil || field(t, updated, "task", "dueDate") != "2026-11-23" {
			t.Fatalf("patch: %v", updated.body)
		}
		stale := w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-weekly", maya, map[string]any{"changes": map[string]any{"title": "Other device"}}, "If-Match", `"1"`), 409, "VERSION_CONFLICT")
		current := stale.body["error"].(map[string]any)["current"].(map[string]any)
		if current["version"].(float64) != 2 || current["task"].(map[string]any)["title"] != "Water the plants (balcony)" {
			t.Fatalf("the conflict must carry the server copy: %v", current)
		}
		w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-weekly", maya, map[string]any{"changes": map[string]any{"dueDate": "tomorrow"}}, "If-Match", `"2"`), 422, "VALIDATION_FAILED")
		w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-weekly", maya, map[string]any{"changes": map[string]any{"id": "renamed"}}, "If-Match", `"2"`), 422, "IMMUTABLE_FIELD")
		w.expect(t, family.do(t, "PATCH", "/v1/task-lists/list-tasks", maya, map[string]any{"changes": map[string]any{"isDefault": false}}, "If-Match", `"1"`), 422, "IMMUTABLE_FIELD")

		// Two devices with the same base version: exactly one wins.
		var wg sync.WaitGroup
		statuses := make(chan int, 2)
		for _, title := range []string{"Laptop edit", "Phone edit"} {
			wg.Add(1)
			go func(title string) {
				defer wg.Done()
				statuses <- family.do(t, "PATCH", "/v1/tasks/task-done", maya, map[string]any{"changes": map[string]any{"title": title}}, "If-Match", `"1"`).status
			}(title)
		}
		wg.Wait()
		close(statuses)
		got := map[int]int{}
		for s := range statuses {
			got[s]++
		}
		if got[200] != 1 || got[409] != 1 {
			t.Fatalf("concurrent edits: %v", got)
		}
	})

	t.Run("completing an occurrence appends history atomically and idempotently", func(t *testing.T) {
		record := map[string]any{"id": "done-mar", "taskId": "task-month-end", "title": "Close the books", "listId": "list-launch", "completedUtc": "2026-03-31T16:02:00+00:00", "occurrenceDate": "2026-03-31"}
		r := w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-month-end", maya, map[string]any{"changes": map[string]any{"dueDate": "2026-04-30"}, "append_completion": record}, "If-Match", `"1"`), 200, "")
		if field(t, r, "task", "dueDate") != "2026-04-30" {
			t.Fatal("due date not advanced")
		}
		// A retry of the same completion (after a lost response) does not duplicate it.
		w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-month-end", maya, map[string]any{"append_completion": record}, "If-Match", `"2"`), 200, "")
		changed := map[string]any{}
		for k, v := range record {
			changed[k] = v
		}
		changed["completedUtc"] = "2026-03-31T18:00:00+00:00"
		w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-month-end", maya, map[string]any{"append_completion": changed}, "If-Match", `"3"`), 409, "COMPLETION_CONFLICT")
		history := w.expect(t, family.do(t, "GET", "/v1/tasks/completion-history", maya, nil), 200, "").body["history"].([]any)
		if len(history) != 1 || history[0].(map[string]any)["id"] != "done-mar" {
			t.Fatalf("history: %v", history)
		}
		w.expect(t, family.do(t, "PATCH", "/v1/tasks/task-weekly", maya, map[string]any{"append_completion": record}, "If-Match", `"2"`), 422, "VALIDATION_FAILED")
		if h := w.expect(t, family.do(t, "GET", "/v1/tasks/completion-history", sam, nil), 200, "").body["history"].([]any); len(h) != 0 {
			t.Fatal("history is private")
		}
	})

	t.Run("deletes keep tombstones and follow the cascade rules", func(t *testing.T) {
		w.expect(t, family.do(t, "POST", "/v1/tasks", maya, map[string]any{"task": json.RawMessage(doc["tasks"][1])}), 409, "ALREADY_EXISTS")
		w.expect(t, family.do(t, "DELETE", "/v1/task-lists/list-tasks", maya, nil, "If-Match", `"1"`), 409, "DEFAULT_LIST")
		w.expect(t, family.do(t, "DELETE", "/v1/task-lists/list-launch", maya, nil, "If-Match", `"1"`), 204, "")
		w.expect(t, family.do(t, "GET", "/v1/tasks/task-month-end", maya, nil), 404, "NOT_FOUND")
		again := w.expect(t, family.do(t, "POST", "/v1/tasks", maya, map[string]any{"task": map[string]any{"id": "task-month-end", "listId": "list-tasks", "title": "back"}}), 409, "ALREADY_EXISTS")
		if again.body["error"].(map[string]any)["current_deleted"] != true {
			t.Fatal("a deleted task must not be resurrected by a create")
		}
		if h := w.expect(t, family.do(t, "GET", "/v1/tasks/completion-history", maya, nil), 200, "").body["history"].([]any); len(h) != 1 {
			t.Fatal("completion history survives deletion")
		}
		w.expect(t, family.do(t, "POST", "/v1/task-lists", maya, map[string]any{"list": map[string]any{"id": "list-grouped", "name": "Grouped", "groupId": "group-work"}}), 201, "")
		w.expect(t, family.do(t, "DELETE", "/v1/list-groups/group-work", maya, nil, "If-Match", `"1"`), 204, "")
		ungrouped := w.expect(t, family.do(t, "GET", "/v1/task-lists/list-grouped", maya, nil), 200, "")
		if field(t, ungrouped, "list", "groupId") != nil || etag(ungrouped) != `"2"` {
			t.Fatalf("the list must become ungrouped with a new version: %v", ungrouped.body)
		}
	})

	t.Run("settings: allowlisted namespaces, versions, and roles", func(t *testing.T) {
		fresh := w.expect(t, family.do(t, "GET", "/v1/config/user/appearance", maya, nil), 200, "")
		if fresh.body["version"].(float64) != 0 || etag(fresh) != `"0"` {
			t.Fatalf("unwritten: %v", fresh.body)
		}
		w.expect(t, family.do(t, "PUT", "/v1/config/user/appearance", maya, map[string]any{"data": map[string]any{"theme": "Dark"}}), 428, "PRECONDITION_REQUIRED")
		w.expect(t, family.do(t, "PUT", "/v1/config/user/appearance", maya, map[string]any{"data": map[string]any{"theme": "Dark"}}, "If-Match", `"0"`), 200, "")
		w.expect(t, family.do(t, "PUT", "/v1/config/user/appearance", maya, map[string]any{"data": map[string]any{"theme": "Light"}}, "If-Match", `"0"`), 409, "VERSION_CONFLICT")
		patched := w.expect(t, family.do(t, "PATCH", "/v1/config/user/appearance", maya, map[string]any{"changes": map[string]any{"darkColors": map[string]string{"ChromeBrush": "#101018"}}}, "If-Match", `"1"`), 200, "")
		if field(t, patched, "data", "theme") != "Dark" || patched.body["version"].(float64) != 2 {
			t.Fatalf("merge: %v", patched.body)
		}
		w.expect(t, family.do(t, "PATCH", "/v1/config/user/appearance", maya, map[string]any{"changes": map[string]any{"smtpPassword": "hunter2"}}, "If-Match", `"2"`), 422, "VALIDATION_FAILED")
		if v := w.expect(t, family.do(t, "GET", "/v1/config/user/appearance", sam, nil), 200, "").body["version"].(float64); v != 0 {
			t.Fatal("personal settings are private")
		}
		w.expect(t, family.do(t, "GET", "/v1/config/user/secrets", maya, nil), 404, "UNKNOWN_NAMESPACE")
		w.expect(t, family.do(t, "GET", "/v1/config/tenant/appearance", maya, nil), 404, "UNKNOWN_NAMESPACE")
		w.expect(t, family.do(t, "PUT", "/v1/config/tenant/space.profile", sam, map[string]any{"data": map[string]any{"accentColor": "#FF0000"}}, "If-Match", `"0"`), 403, "FORBIDDEN")
		w.expect(t, family.do(t, "PUT", "/v1/config/tenant/space.profile", ali, map[string]any{"data": map[string]any{"accentColor": "#6C6DB0", "description": "The Parks"}}, "If-Match", `"0"`), 200, "")
		if field(t, w.expect(t, family.do(t, "GET", "/v1/config/tenant/space.profile", sam, nil), 200, ""), "data", "accentColor") != "#6C6DB0" {
			t.Fatal("members read space settings")
		}
		w.expect(t, family.do(t, "GET", "/v1/config/tenant/space.profile", eve, nil), 404, "TENANT_NOT_FOUND")
	})

	t.Run("export holds only the caller's data, importable by Windows", func(t *testing.T) {
		export := w.expect(t, w.do(t, "GET", "/v1/data/export", maya, nil), 200, "")
		if export.body["format"] != "alondra1-account-export" {
			t.Fatalf("export: %v", export.body)
		}
		var familyExport map[string]any
		for _, s := range export.body["spaces"].([]any) {
			if s.(map[string]any)["id"] == familyID {
				familyExport = s.(map[string]any)
			}
		}
		todo := familyExport["todo"].(map[string]any)
		if todo["format"] != "alondra1-todo" || todo["version"].(float64) != 1 {
			t.Fatalf("todo document: %v", todo)
		}
		raw, _ := json.Marshal(todo)
		if strings.Contains(string(raw), "Sam's own") || !strings.Contains(string(raw), "Water the plants (balcony)") {
			t.Fatal("the export must contain maya's data only")
		}
		if familyExport["settings"].(map[string]any)["appearance"] == nil {
			t.Fatal("personal settings missing from the export")
		}
	})

	t.Run("a removed member loses access at once", func(t *testing.T) {
		var samID string
		for _, m := range w.do(t, "GET", "/v1/tenants/"+familyID+"/members", maya, nil).body["members"].([]any) {
			if m.(map[string]any)["email"] == "sam@example.com" {
				samID = m.(map[string]any)["user_id"].(string)
			}
		}
		w.expect(t, w.do(t, "DELETE", "/v1/tenants/"+familyID+"/members/"+samID, maya, nil), 204, "")
		w.expect(t, family.do(t, "GET", "/v1/tasks", sam, nil), 404, "TENANT_NOT_FOUND")
		w.expect(t, family.do(t, "POST", "/v1/tasks", sam, map[string]any{"task": map[string]any{"id": "late", "listId": "list-launch", "title": "x"}}), 404, "TENANT_NOT_FOUND")
	})
}
