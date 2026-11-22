//go:build integration

package httpapi_test

import (
	"context"
	"encoding/json"
	"net/url"
	"strings"
	"sync"
	"testing"

	"alondra1/backend/internal/cloudsync/client"
)

// device is one signed-in installation running the reference offline-first client.
func (w *world) device(t *testing.T, email, tenantID string) *client.Client {
	t.Helper()
	return client.New(w.server.URL, w.signIn(t, email), tenantID)
}

func mustSync(t *testing.T, d *client.Client) {
	t.Helper()
	if err := d.Sync(context.Background()); err != nil {
		t.Fatalf("sync: %v", err)
	}
}

func canonical(raw json.RawMessage) string {
	var v any
	_ = json.Unmarshal(raw, &v)
	out, _ := json.Marshal(v)
	return string(out)
}

func objects(t *testing.T, items []json.RawMessage) []map[string]any {
	t.Helper()
	out := make([]map[string]any, 0, len(items))
	for _, raw := range items {
		var m map[string]any
		if err := json.Unmarshal(raw, &m); err != nil {
			t.Fatal(err)
		}
		out = append(out, m)
	}
	return out
}

func TestMultiDeviceSync(t *testing.T) {
	w := newWorld(t)
	ctx := context.Background()
	maya := w.signIn(t, "maya@example.com")
	familyID := w.expect(t, w.do(t, "POST", "/v1/tenants", maya, map[string]string{"type": "family", "name": "The Parks"}), 201, "").body["id"].(string)
	sam := w.signIn(t, "sam@example.com")
	w.expect(t, w.do(t, "POST", "/v1/invites/"+w.invite(t, maya, familyID, "sam@example.com", "member")+"/accept", sam, nil), 200, "")

	laptop := w.device(t, "maya@example.com", familyID)
	phone := w.device(t, "maya@example.com", familyID)
	doc := windowsFixture(t)

	t.Run("a second device receives everything the first enrolled", func(t *testing.T) {
		for _, step := range []struct{ key, kind string }{{"groups", "group"}, {"lists", "list"}, {"tasks", "task"}} {
			for _, raw := range doc[step.key] {
				var object map[string]any
				_ = json.Unmarshal(raw, &object)
				laptop.Create(step.kind, object)
			}
		}
		mustSync(t, laptop)
		if len(laptop.Outbox) != 0 || len(laptop.Conflicts) != 0 || len(laptop.Rejected) != 0 {
			t.Fatalf("enrollment: outbox=%d conflicts=%d rejected=%v", len(laptop.Outbox), len(laptop.Conflicts), laptop.Rejected)
		}
		mustSync(t, phone)
		if got := strings.Join(phone.Live("task"), ","); got != strings.Join(laptop.Live("task"), ",") || got == "" {
			t.Fatalf("phone tasks %q", got)
		}
		if canonical(phone.Get("task", "task-month-end").Data) != canonical(laptop.Get("task", "task-month-end").Data) {
			t.Fatal("the second device must see identical data")
		}
		if phone.Get("list", "list-launch").Version != 1 {
			t.Fatal("versions travel with the data")
		}
	})

	t.Run("a day of offline work syncs completely", func(t *testing.T) {
		laptop.Online = false
		laptop.Create("task", map[string]any{"id": "offline-new", "listId": "list-tasks", "title": "Written offline"})
		if err := laptop.Patch("task", "offline-new", map[string]any{"important": true}, nil); err != nil { // coalesces into the create
			t.Fatal(err)
		}
		if err := laptop.Patch("task", "task-weekly", map[string]any{"title": "Water the plants"}, nil); err != nil {
			t.Fatal(err)
		}
		if err := laptop.Patch("task", "task-weekly", map[string]any{"myDayDate": nil}, nil); err != nil { // coalesces
			t.Fatal(err)
		}
		laptop.Delete("task", "task-done")
		if err := laptop.Patch("task", "task-month-end", map[string]any{"dueDate": "2026-04-30"}, map[string]any{
			"id": "done-mar", "taskId": "task-month-end", "title": "Close the books", "listId": "list-launch", "completedUtc": "2026-03-31T16:02:00+00:00", "occurrenceDate": "2026-03-31",
		}); err != nil {
			t.Fatal(err)
		}
		if err := laptop.Sync(ctx); err != client.ErrOffline {
			t.Fatalf("offline sync must not touch the network: %v", err)
		}
		if len(laptop.Outbox) != 4 {
			t.Fatalf("outbox after coalescing: %d", len(laptop.Outbox))
		}
		laptop.Online = true
		mustSync(t, laptop)
		if len(laptop.Outbox) != 0 || len(laptop.Conflicts) != 0 || len(laptop.Rejected) != 0 {
			t.Fatalf("after reconnect: outbox=%d conflicts=%v rejected=%v", len(laptop.Outbox), laptop.Conflicts, laptop.Rejected)
		}
		mustSync(t, phone)
		var weekly, created map[string]any
		_ = json.Unmarshal(phone.Get("task", "task-weekly").Data, &weekly)
		_ = json.Unmarshal(phone.Get("task", "offline-new").Data, &created)
		if weekly["title"] != "Water the plants" || weekly["myDayDate"] != nil || created["important"] != true {
			t.Fatalf("phone: weekly=%v new=%v", weekly, created)
		}
		if !phone.Get("task", "task-done").Deleted || phone.Get("completion", "done-mar") == nil {
			t.Fatal("the deletion tombstone and the completion record must reach the phone")
		}
	})

	t.Run("concurrent offline edits conflict visibly and lose nothing", func(t *testing.T) {
		laptop.Online, phone.Online = false, false
		_ = laptop.Patch("task", "task-weekly", map[string]any{"title": "Laptop title"}, nil)
		_ = phone.Patch("task", "task-weekly", map[string]any{"title": "Phone title"}, nil)
		laptop.Online, phone.Online = true, true
		mustSync(t, laptop)
		mustSync(t, phone)
		if len(phone.Conflicts) != 1 || phone.Conflicts[0].Server == nil {
			t.Fatalf("the phone must get a conflict with the server copy: %+v", phone.Conflicts)
		}
		var local map[string]any
		_ = json.Unmarshal(phone.Conflicts[0].Local, &local)
		if local["title"] != "Phone title" {
			t.Fatalf("the phone's edit must be kept in the conflict queue: %v", local)
		}
		var shown map[string]any
		_ = json.Unmarshal(phone.Get("task", "task-weekly").Data, &shown)
		if shown["title"] != "Laptop title" {
			t.Fatalf("the phone shows the server version until the user decides: %v", shown)
		}
		// The user keeps the phone's title, deliberately, on top of the current version.
		if err := phone.ResolveReapply(0); err != nil {
			t.Fatal(err)
		}
		mustSync(t, phone)
		mustSync(t, laptop)
		_ = json.Unmarshal(laptop.Get("task", "task-weekly").Data, &shown)
		if shown["title"] != "Phone title" || len(phone.Conflicts) != 0 {
			t.Fatalf("after resolving: %v conflicts=%d", shown, len(phone.Conflicts))
		}
	})

	t.Run("an edit of a task deleted elsewhere is surfaced, not dropped", func(t *testing.T) {
		laptop.Create("task", map[string]any{"id": "contested", "listId": "list-tasks", "title": "Contested"})
		mustSync(t, laptop)
		mustSync(t, phone)
		laptop.Online, phone.Online = false, false
		laptop.Delete("task", "contested")
		_ = phone.Patch("task", "contested", map[string]any{"title": "Edited on the phone"}, nil)
		laptop.Online, phone.Online = true, true
		mustSync(t, laptop)
		mustSync(t, phone)
		if len(phone.Conflicts) != 1 || !phone.Conflicts[0].Deleted {
			t.Fatalf("delete-vs-edit must be a visible conflict: %+v", phone.Conflicts)
		}
		var local map[string]any
		_ = json.Unmarshal(phone.Conflicts[0].Local, &local)
		if local["title"] != "Edited on the phone" || !phone.Get("task", "contested").Deleted {
			t.Fatalf("the edit is kept aside and the task shows as deleted: %v", local)
		}
		phone.ResolveKeepServer(0)
	})

	t.Run("a retried batch is answered from receipts and never applied twice", func(t *testing.T) {
		family := space{w, familyID}
		before := w.expect(t, family.do(t, "GET", "/v1/tasks/offline-new", laptop.Token, nil), 200, "").body["version"].(float64)
		batch := map[string]any{"mutations": []map[string]any{{"mutation_id": "retry-1", "resource_type": "task", "action": "patch",
			"resource_id": "offline-new", "base_version": before, "changes": map[string]any{"notes": "once"}}}}
		first := w.expect(t, family.do(t, "POST", "/v1/sync/push", laptop.Token, batch), 200, "").body["results"].([]any)[0].(map[string]any)
		again := w.expect(t, family.do(t, "POST", "/v1/sync/push", laptop.Token, batch), 200, "").body["results"].([]any)[0].(map[string]any)
		if first["status"] != "applied" || again["status"] != "applied" || again["replayed"] != true {
			t.Fatalf("first=%v again=%v", first, again)
		}
		after := w.expect(t, family.do(t, "GET", "/v1/tasks/offline-new", laptop.Token, nil), 200, "").body["version"].(float64)
		if after != before+1 {
			t.Fatalf("version moved by %v; the retry was applied again", after-before)
		}
		// The same mutation id from another device is a different mutation.
		other := w.expect(t, family.do(t, "POST", "/v1/sync/push", phone.Token, batch), 200, "").body["results"].([]any)[0].(map[string]any)
		if other["status"] != "conflict" || other["code"] != "VERSION_CONFLICT" {
			t.Fatalf("another device: %v", other)
		}
		w.expect(t, family.do(t, "POST", "/v1/sync/push", laptop.Token, map[string]any{"mutations": []any{}}), 422, "INVALID_BATCH")
		mustSync(t, laptop)
		mustSync(t, phone)
	})

	t.Run("an expired cursor bootstraps again without losing unsent edits", func(t *testing.T) {
		if err := phone.Patch("task", "offline-new", map[string]any{"title": "Unsent while the cursor expired"}, nil); err != nil {
			t.Fatal(err)
		}
		// Activity after the phone's last sync, then a purge of everything up to now: the phone's cursor is too old.
		laptop.Create("task", map[string]any{"id": "while-phone-slept", "listId": "list-tasks", "title": "x"})
		mustSync(t, laptop)
		var max int64
		if err := w.pool.QueryRow(ctx, `SELECT max(sequence) FROM change_log WHERE tenant_id = $1`, familyID).Scan(&max); err != nil {
			t.Fatal(err)
		}
		if _, err := w.pool.Exec(ctx, `INSERT INTO change_log_floor (tenant_id, floor_sequence) VALUES ($1, $2)`, familyID, max); err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() { _, _ = w.pool.Exec(context.Background(), `DELETE FROM change_log_floor WHERE tenant_id = $1`, familyID) })
		w.expect(t, space{w, familyID}.do(t, "GET", "/v1/sync/changes?cursor="+url.QueryEscape(phone.Cursor), phone.Token, nil), 410, "CURSOR_EXPIRED")
		if err := phone.Pull(ctx); err != nil {
			t.Fatalf("pull: %v", err)
		}
		if len(phone.Outbox) != 1 || phone.Get("task", "while-phone-slept") == nil {
			t.Fatalf("the phone must bootstrap again (seeing the new task) and keep its outbox: outbox=%d", len(phone.Outbox))
		}
		var shown map[string]any
		_ = json.Unmarshal(phone.Get("task", "offline-new").Data, &shown)
		if shown["title"] != "Unsent while the cursor expired" {
			t.Fatalf("the unsent edit must stay visible after the bootstrap: %v", shown)
		}
		mustSync(t, phone)
		if len(phone.Outbox) != 0 || len(phone.Conflicts) != 0 {
			t.Fatalf("after pushing: outbox=%d conflicts=%v", len(phone.Outbox), phone.Conflicts)
		}
		mustSync(t, laptop) // the laptop's cursor is at the floor, not below it: still valid
	})

	samDevice := w.device(t, "sam@example.com", familyID)
	t.Run("the feed carries only what the member may see", func(t *testing.T) {
		mustSync(t, samDevice)
		if tasks := samDevice.Live("task"); len(tasks) != 0 {
			t.Fatalf("sam received maya's tasks: %v", tasks)
		}
		family := space{w, familyID}
		w.expect(t, family.do(t, "PUT", "/v1/config/user/appearance", maya, map[string]any{"data": map[string]any{"theme": "Dark"}}, "If-Match", `"0"`), 200, "")
		w.expect(t, family.do(t, "PUT", "/v1/config/tenant/space.profile", maya, map[string]any{"data": map[string]any{"accentColor": "#6C6DB0"}}, "If-Match", `"0"`), 200, "")
		laptop.Create("task", map[string]any{"id": "after-sam", "listId": "list-tasks", "title": "Private"})
		mustSync(t, laptop)
		mustSync(t, samDevice)
		if samDevice.Get("tenant_config", "space.profile") == nil {
			t.Fatal("space settings reach every member")
		}
		if samDevice.Get("user_config", "appearance") != nil || samDevice.Get("task", "after-sam") != nil {
			t.Fatal("personal settings and tasks of others must not reach sam")
		}
		if laptop.Get("user_config", "appearance") == nil {
			t.Fatal("maya's own settings reach her devices")
		}
	})

	t.Run("cursors are bound to their user and space and cannot be forged", func(t *testing.T) {
		family := space{w, familyID}
		w.expect(t, family.do(t, "GET", "/v1/sync/changes?cursor="+url.QueryEscape(laptop.Cursor), samDevice.Token, nil), 400, "INVALID_CURSOR")
		forged := laptop.Cursor[:len(laptop.Cursor)-2] + "AA"
		w.expect(t, family.do(t, "GET", "/v1/sync/changes?cursor="+url.QueryEscape(forged), laptop.Token, nil), 400, "INVALID_CURSOR")
		w.expect(t, family.do(t, "GET", "/v1/sync/changes", laptop.Token, nil), 400, "INVALID_CURSOR")
	})

	t.Run("bootstrap pages return every item exactly once", func(t *testing.T) {
		family := space{w, familyID}
		seen := map[string]int{}
		token, pages := "", 0
		for {
			path := "/v1/sync/bootstrap?limit=2"
			if token != "" {
				path += "&page_token=" + url.QueryEscape(token)
			}
			page := w.expect(t, family.do(t, "GET", path, maya, nil), 200, "")
			for _, raw := range page.body["items"].([]any) {
				item := raw.(map[string]any)
				seen[item["resource_type"].(string)+"/"+item["resource_id"].(string)]++
			}
			pages++
			next, ok := page.body["next_page_token"].(string)
			if !ok {
				break
			}
			token = next
		}
		for k, n := range seen {
			if n != 1 {
				t.Fatalf("%s delivered %d times", k, n)
			}
		}
		if pages < 3 || seen["task/offline-new"] != 1 || seen["completion/done-mar"] != 1 || seen["tenant_config/space.profile"] != 1 || seen["task/task-done"] != 0 {
			t.Fatalf("pages=%d seen=%v", pages, seen)
		}
	})

	t.Run("concurrent writers produce a complete, ordered feed", func(t *testing.T) {
		cursor := laptop.Cursor
		var wg sync.WaitGroup
		for i := 0; i < 20; i++ {
			wg.Add(1)
			go func(i int) {
				defer wg.Done()
				family := space{w, familyID}
				family.do(t, "POST", "/v1/tasks", maya, map[string]any{"task": map[string]any{"id": "burst-" + string(rune('a'+i)), "listId": "list-tasks", "title": "burst"}})
			}(i)
		}
		wg.Wait()
		family := space{w, familyID}
		page := w.expect(t, family.do(t, "GET", "/v1/sync/changes?limit=1000&cursor="+url.QueryEscape(cursor), maya, nil), 200, "")
		last, bursts := 0.0, 0
		for _, raw := range page.body["changes"].([]any) {
			change := raw.(map[string]any)
			if seq := change["sequence"].(float64); seq <= last {
				t.Fatalf("sequence not increasing: %v after %v", seq, last)
			} else {
				last = seq
			}
			if strings.HasPrefix(change["resource_id"].(string), "burst-") {
				bursts++
			}
		}
		if bursts != 20 {
			t.Fatalf("expected 20 burst changes, got %d", bursts)
		}
	})

	t.Run("a removed member's device purges the space and keeps unsent edits aside", func(t *testing.T) {
		samDevice.Create("list", map[string]any{"id": "sams-list", "name": "Sam's"})
		var samID string
		for _, m := range w.do(t, "GET", "/v1/tenants/"+familyID+"/members", maya, nil).body["members"].([]any) {
			if m.(map[string]any)["email"] == "sam@example.com" {
				samID = m.(map[string]any)["user_id"].(string)
			}
		}
		w.expect(t, w.do(t, "DELETE", "/v1/tenants/"+familyID+"/members/"+samID, maya, nil), 204, "")
		mustSync(t, samDevice)
		if !samDevice.Purged || len(samDevice.Items) != 0 || len(samDevice.Orphaned) != 1 {
			t.Fatalf("purged=%v items=%d orphaned=%d", samDevice.Purged, len(samDevice.Items), len(samDevice.Orphaned))
		}
	})
}
