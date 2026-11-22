package tasks

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

var now = time.Date(2026, 11, 22, 9, 0, 0, 0, time.UTC)

// The golden document written by the Windows serializer (packages/shared-models) must be accepted as-is.
func fixture(t *testing.T) map[string][]json.RawMessage {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "packages", "shared-models", "fixtures", "todo-v1", "full-document.json"))
	if err != nil {
		t.Fatal(err)
	}
	var doc map[string]json.RawMessage
	if err := json.Unmarshal(data, &doc); err != nil {
		t.Fatal(err)
	}
	out := map[string][]json.RawMessage{}
	for _, key := range []string{"groups", "lists", "tasks", "history"} {
		var items []json.RawMessage
		if err := json.Unmarshal(doc[key], &items); err != nil {
			t.Fatal(err)
		}
		out[key] = items
	}
	return out
}

func TestTheWindowsFixtureIsAcceptedAndKeepsItsValues(t *testing.T) {
	doc := fixture(t)
	for _, raw := range doc["groups"] {
		g, err := ParseGroup(raw)
		if err != nil {
			t.Fatalf("group: %v", err)
		}
		if out, _ := json.Marshal(g); strings.Contains(string(out), "collapsed") {
			t.Fatal("collapsed is device-local and must be dropped")
		}
	}
	for _, raw := range doc["lists"] {
		if _, err := ParseList(raw, now); err != nil {
			t.Fatalf("list: %v", err)
		}
	}
	for _, raw := range doc["history"] {
		if _, err := ParseCompletion(raw); err != nil {
			t.Fatalf("history: %v", err)
		}
	}
	var monthEnd Task
	for _, raw := range doc["tasks"] {
		task, err := ParseTask(raw, now)
		if err != nil {
			t.Fatalf("task: %v", err)
		}
		if task.ID == "task-month-end" {
			monthEnd = task
		}
	}
	// Distinct formats survive untouched: date-only, local time, local wall clock (a DST gap time), offset instants.
	if *monthEnd.DueDate != "2026-03-31" || *monthEnd.DueTime != "16:00:00" || *monthEnd.Recurrence.Anchor != "2026-01-31" {
		t.Fatalf("dates changed: %+v", monthEnd)
	}
	if monthEnd.Reminders[0].At != "2026-03-29T02:30:00" || monthEnd.CreatedUtc != "2026-01-05T08:30:00+00:00" {
		t.Fatalf("wall-clock or instant changed: %+v", monthEnd.Reminders)
	}
	out, _ := json.Marshal(monthEnd)
	for _, deviceLocal := range []string{"deliveredFor", "snoozedUntilUtc", "dismissed"} {
		if strings.Contains(string(out), deviceLocal) {
			t.Fatalf("%s is device-local and must be dropped", deviceLocal)
		}
	}
	if len(monthEnd.Subtasks) != 3 || monthEnd.Subtasks[0].ID != "step-1" || monthEnd.Subtasks[2].ID != "step-3" {
		t.Fatal("step order must be preserved")
	}
}

func TestInvalidTasksAreRejectedWithReasons(t *testing.T) {
	base := `{"id":"t1","listId":"l1","title":"x"%s}`
	cases := map[string]string{
		"unknown field":         `,"priority":3`,
		"dueDate as instant":    `,"dueDate":"2026-11-22T00:00:00Z"`,
		"bad due time":          `,"dueTime":"25:00"`,
		"reminder with offset":  `,"reminders":[{"id":"r","at":"2026-11-22T09:00:00+01:00"}]`,
		"bad frequency":         `,"recurrence":{"frequency":"Hourly","interval":1,"daysOfWeek":[]}`,
		"zero interval":         `,"recurrence":{"frequency":"Daily","interval":0,"daysOfWeek":[]}`,
		"repeated weekday":      `,"recurrence":{"frequency":"Weekly","interval":1,"daysOfWeek":["Monday","Monday"]}`,
		"path traversal":        `,"attachments":[{"id":"a","fileName":"x","relativePath":"../secrets.bin","size":1,"addedUtc":"2026-11-22T09:00:00Z"}]`,
		"absolute path":         `,"attachments":[{"id":"a","fileName":"x","relativePath":"C:\\Windows\\x","size":1,"addedUtc":"2026-11-22T09:00:00Z"}]`,
		"duplicate step ids":    `,"subtasks":[{"id":"s","title":"a"},{"id":"s","title":"b"}]`,
		"misspelled field":      `,"notes":"","title2":""`,
		"instant without zone":  `,"completedUtc":"2026-11-22T09:00:00"`,
		"start date garbage":    `,"startDate":"2026-02-30"`,
	}
	for name, extra := range cases {
		if _, err := ParseTask([]byte(strings.Replace(base, "%s", extra, 1)), now); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	long := `{"id":"t1","listId":"l1","title":"` + strings.Repeat("a", MaxTitleRunes+1) + `"}`
	var verr *ValidationError
	if _, err := ParseTask([]byte(long), now); !errors.As(err, &verr) {
		t.Fatalf("long title: %v", err)
	}
	if _, err := ParseTask([]byte(`{"id":"../x","listId":"l1","title":"x"}`), now); err == nil {
		t.Fatal("unsafe id accepted")
	}
}

func TestMissingTimestampsAreFilledButOthersAreNotInvented(t *testing.T) {
	task, err := ParseTask([]byte(`{"id":"t1","listId":"l1","title":"x"}`), now)
	if err != nil {
		t.Fatal(err)
	}
	if task.CreatedUtc == "" || task.ModifiedUtc == "" || task.DueDate != nil || task.Reminders == nil || task.Subtasks == nil {
		t.Fatalf("defaults: %+v", task)
	}
}

func TestListsAndGroups(t *testing.T) {
	if _, err := ParseList([]byte(`{"id":"l","name":"  "}`), now); err == nil {
		t.Fatal("empty name accepted")
	}
	if _, err := ParseList([]byte(`{"id":"l","name":"🚀 Launch","color":"purple"}`), now); err == nil {
		t.Fatal("bad color accepted")
	}
	if l, err := ParseList([]byte(`{"id":"l","name":"🚀 Launch","color":"#6C6DB0","groupId":"g"}`), now); err != nil || *l.GroupID != "g" {
		t.Fatalf("list: %+v %v", l, err)
	}
	if _, err := ParseGroup([]byte(`{"id":"g","name":"Work","collapsed":true}`)); err != nil {
		t.Fatal(err)
	}
}

func TestSafeRelativePath(t *testing.T) {
	for path, ok := range map[string]bool{
		"task/file.pdf": true, `task\file.pdf`: true, "file.pdf": true,
		"": false, "/etc/passwd": false, `\\server\share`: false, "C:file": false, "a/../b": false, "./a": false, "a//b": false,
	} {
		if SafeRelativePath(path) != ok {
			t.Errorf("%q: expected %v", path, ok)
		}
	}
}

func TestComposePatchKeepsRemovals(t *testing.T) {
	composed, err := ComposePatch(json.RawMessage(`{"title":"a","recurrence":{"interval":2}}`), json.RawMessage(`{"myDayDate":null,"recurrence":{"until":null}}`))
	if err != nil {
		t.Fatal(err)
	}
	// Applying the composed patch equals applying both in order.
	target := json.RawMessage(`{"title":"old","myDayDate":"2026-11-21","recurrence":{"frequency":"Daily","interval":1,"until":"2026-12-31"}}`)
	once, _ := MergePatch(target, composed)
	step, _ := MergePatch(target, json.RawMessage(`{"title":"a","recurrence":{"interval":2}}`))
	twice, _ := MergePatch(step, json.RawMessage(`{"myDayDate":null,"recurrence":{"until":null}}`))
	if string(once) != string(twice) {
		t.Fatalf("composed %s\nonce  %s\ntwice %s", composed, once, twice)
	}
	if strings.Contains(string(once), "myDayDate") || strings.Contains(string(once), "until") {
		t.Fatalf("removals lost: %s", once)
	}
}

func TestMergePatch(t *testing.T) {
	merged, err := MergePatch(json.RawMessage(`{"title":"a","dueDate":"2026-11-22","recurrence":{"frequency":"Daily","interval":1}}`),
		json.RawMessage(`{"title":"b","dueDate":null,"recurrence":{"interval":2}}`))
	if err != nil {
		t.Fatal(err)
	}
	var out map[string]any
	_ = json.Unmarshal(merged, &out)
	if out["title"] != "b" || out["dueDate"] != nil || out["recurrence"].(map[string]any)["interval"].(float64) != 2 || out["recurrence"].(map[string]any)["frequency"] != "Daily" {
		t.Fatalf("merge: %v", out)
	}
	if _, err := MergePatch(json.RawMessage(`{}`), json.RawMessage(`[1]`)); err == nil {
		t.Fatal("non-object patch accepted")
	}
}
