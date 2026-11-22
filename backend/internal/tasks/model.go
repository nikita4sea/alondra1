// Package tasks stores the Alondra1 1.3.0 task model in the cloud (docs/BACKEND_SPEC.md §7). The wire format is
// packages/api-contract/schemas/todo-v1.schema.json: camelCase, enums as strings, date-only / local wall-clock / UTC
// values as distinct string formats that are validated but never converted. Every record belongs to one space and one
// user and is visible only to that user.
package tasks

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"regexp"
	"strings"
	"time"
	"unicode/utf8"
)

// Limits match the Windows model (TodoTransfer), so anything the app stores locally can be uploaded.
const (
	SchemaVersion   = 1
	MaxTitleRunes   = 2000
	MaxNotesRunes   = 100_000
	MaxNameRunes    = 200
	MaxSteps        = 500
	MaxReminders    = 50
	MaxAttachments  = 100
	MaxResourceSize = 512 << 10
)

// ValidationError explains what is wrong with a resource; it maps to 422.
type ValidationError struct{ Problems []string }

func (e *ValidationError) Error() string { return "invalid: " + strings.Join(e.Problems, "; ") }

type validator struct{ problems []string }

func (v *validator) add(format string, args ...any) { v.problems = append(v.problems, fmt.Sprintf(format, args...)) }
func (v *validator) err() error {
	if len(v.problems) == 0 {
		return nil
	}
	return &ValidationError{Problems: v.problems}
}

var (
	idPattern        = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)
	colorPattern     = regexp.MustCompile(`^#[0-9A-Fa-f]{6}$`)
	localTimePattern = regexp.MustCompile(`^([01][0-9]|2[0-3]):[0-5][0-9](:[0-5][0-9](\.[0-9]{1,7})?)?$`)
	localDTPattern   = regexp.MustCompile(`^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,7})?$`)
)

func (v *validator) id(field, value string) {
	if !idPattern.MatchString(value) {
		v.add("%s must be 1-64 letters, digits, '-' or '_'", field)
	}
}

func (v *validator) optionalID(field string, value *string) {
	if value != nil {
		v.id(field, *value)
	}
}

func (v *validator) text(field, value string, max int, required bool) {
	if required && strings.TrimSpace(value) == "" {
		v.add("%s is required", field)
	}
	if !utf8.ValidString(value) || utf8.RuneCountInString(value) > max {
		v.add("%s must be at most %d characters", field, max)
	}
}

// utc checks an instant with an explicit offset (System.DateTimeOffset). The original string, offset included, is kept.
func (v *validator) utc(field string, value *string) {
	if value == nil {
		return
	}
	if _, err := time.Parse(time.RFC3339Nano, *value); err != nil {
		v.add("%s must be a date-time with an offset", field)
	}
}

func (v *validator) date(field string, value *string) {
	if value == nil {
		return
	}
	if _, err := time.Parse("2006-01-02", *value); err != nil {
		v.add("%s must be a calendar date (YYYY-MM-DD)", field)
	}
}

func (v *validator) localDateTime(field string, value *string) {
	if value == nil {
		return
	}
	if !localDTPattern.MatchString(*value) {
		v.add("%s must be a local date-time without offset (YYYY-MM-DDTHH:MM:SS)", field)
		return
	}
	if _, err := time.Parse("2006-01-02", (*value)[:10]); err != nil {
		v.add("%s has an invalid date", field)
	}
}

// ---------- resources ----------

type RecurrenceRule struct {
	Frequency  string   `json:"frequency"`
	Interval   int      `json:"interval"`
	DaysOfWeek []string `json:"daysOfWeek"`
	Until      *string  `json:"until,omitempty"`
	Anchor     *string  `json:"anchor,omitempty"`
}

// Reminder is the shared reminder definition. Delivery state (deliveredFor, dismissed, snoozedUntilUtc) is per device:
// it is accepted on input, so clients can send their model unchanged, but never stored or synced.
type Reminder struct {
	ID string `json:"id"`
	At string `json:"at"`
}

type reminderInput struct {
	ID              string  `json:"id"`
	At              string  `json:"at"`
	DeliveredFor    *string `json:"deliveredFor,omitempty"`
	Dismissed       *bool   `json:"dismissed,omitempty"`
	SnoozedUntilUtc *string `json:"snoozedUntilUtc,omitempty"`
}

type Subtask struct {
	ID           string  `json:"id"`
	Title        string  `json:"title"`
	Completed    bool    `json:"completed"`
	CompletedUtc *string `json:"completedUtc,omitempty"`
}

type Attachment struct {
	ID           string `json:"id"`
	FileName     string `json:"fileName"`
	RelativePath string `json:"relativePath"`
	Size         int64  `json:"size"`
	AddedUtc     string `json:"addedUtc"`
}

type Task struct {
	ID           string          `json:"id"`
	ListID       string          `json:"listId"`
	Title        string          `json:"title"`
	Notes        string          `json:"notes"`
	Completed    bool            `json:"completed"`
	Important    bool            `json:"important"`
	Position     float64         `json:"position"`
	CreatedUtc   string          `json:"createdUtc"`
	ModifiedUtc  string          `json:"modifiedUtc"`
	CompletedUtc *string         `json:"completedUtc,omitempty"`
	StartDate    *string         `json:"startDate,omitempty"`
	DueDate      *string         `json:"dueDate,omitempty"`
	DueTime      *string         `json:"dueTime,omitempty"`
	MyDayDate    *string         `json:"myDayDate,omitempty"`
	Recurrence   *RecurrenceRule `json:"recurrence,omitempty"`
	Reminders    []Reminder      `json:"reminders"`
	Subtasks     []Subtask       `json:"subtasks"`
	Attachments  []Attachment    `json:"attachments"`
}

type taskInput struct {
	Task
	Reminders []reminderInput `json:"reminders"`
}

type List struct {
	ID             string  `json:"id"`
	Name           string  `json:"name"`
	GroupID        *string `json:"groupId,omitempty"`
	Color          *string `json:"color,omitempty"`
	TintBackground bool    `json:"tintBackground"`
	Position       float64 `json:"position"`
	IsDefault      bool    `json:"isDefault"`
	CreatedUtc     string  `json:"createdUtc"`
}

// Group: "collapsed" is a presentation preference of one device; it is accepted on input and dropped.
type Group struct {
	ID       string  `json:"id"`
	Name     string  `json:"name"`
	Position float64 `json:"position"`
}

type groupInput struct {
	Group
	Collapsed *bool `json:"collapsed,omitempty"`
}

type CompletionRecord struct {
	ID             string  `json:"id"`
	TaskID         string  `json:"taskId"`
	Title          string  `json:"title"`
	ListID         string  `json:"listId"`
	CompletedUtc   string  `json:"completedUtc"`
	OccurrenceDate *string `json:"occurrenceDate,omitempty"`
}

// ---------- decoding with validation ----------

func strictDecode(data []byte, target any) error {
	if len(data) > MaxResourceSize {
		return &ValidationError{Problems: []string{fmt.Sprintf("the resource is larger than %d bytes", MaxResourceSize)}}
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		return &ValidationError{Problems: []string{"not a valid todo-v1 object: " + err.Error()}}
	}
	if err := decoder.Decode(&struct{}{}); err != io.EOF {
		return &ValidationError{Problems: []string{"exactly one JSON object expected"}}
	}
	return nil
}

var weekdays = map[string]bool{"Sunday": true, "Monday": true, "Tuesday": true, "Wednesday": true, "Thursday": true, "Friday": true, "Saturday": true}

// ParseTask validates a todo-v1 TodoTask and returns it without device-local reminder state. Missing timestamps are
// filled with now (the server does not invent other values).
func ParseTask(data []byte, now time.Time) (Task, error) {
	var in taskInput
	if err := strictDecode(data, &in); err != nil {
		return Task{}, err
	}
	t := in.Task
	stamp := now.UTC().Format(time.RFC3339Nano)
	if t.CreatedUtc == "" {
		t.CreatedUtc = stamp
	}
	if t.ModifiedUtc == "" {
		t.ModifiedUtc = stamp
	}
	v := &validator{}
	v.id("id", t.ID)
	v.id("listId", t.ListID)
	v.text("title", t.Title, MaxTitleRunes, false)
	v.text("notes", t.Notes, MaxNotesRunes, false)
	v.utc("createdUtc", &t.CreatedUtc)
	v.utc("modifiedUtc", &t.ModifiedUtc)
	v.utc("completedUtc", t.CompletedUtc)
	v.date("startDate", t.StartDate)
	v.date("dueDate", t.DueDate)
	v.date("myDayDate", t.MyDayDate)
	if t.DueTime != nil && !localTimePattern.MatchString(*t.DueTime) {
		v.add("dueTime must be a local time (HH:MM[:SS])")
	}
	if r := t.Recurrence; r != nil {
		switch r.Frequency {
		case "Daily", "Weekly", "Monthly", "Yearly":
		default:
			v.add("recurrence.frequency must be Daily, Weekly, Monthly, or Yearly")
		}
		if r.Interval < 1 || r.Interval > 999 {
			v.add("recurrence.interval must be 1 to 999")
		}
		seen := map[string]bool{}
		for _, day := range r.DaysOfWeek {
			if !weekdays[day] || seen[day] {
				v.add("recurrence.daysOfWeek must be distinct day names")
				break
			}
			seen[day] = true
		}
		if r.DaysOfWeek == nil {
			r.DaysOfWeek = []string{}
		}
		v.date("recurrence.until", r.Until)
		v.date("recurrence.anchor", r.Anchor)
	}
	if len(in.Reminders) > MaxReminders || len(t.Subtasks) > MaxSteps || len(t.Attachments) > MaxAttachments {
		v.add("too many reminders (max %d), steps (max %d), or attachments (max %d)", MaxReminders, MaxSteps, MaxAttachments)
	}
	t.Reminders = make([]Reminder, 0, len(in.Reminders))
	ids := map[string]bool{}
	unique := func(kind, id string) {
		if ids[kind+id] {
			v.add("duplicate %s id %q", kind, id)
		}
		ids[kind+id] = true
	}
	for _, r := range in.Reminders {
		v.id("reminders[].id", r.ID)
		unique("reminder", r.ID)
		at := r.At
		v.localDateTime("reminders[].at", &at)
		v.localDateTime("reminders[].deliveredFor", r.DeliveredFor)
		v.utc("reminders[].snoozedUntilUtc", r.SnoozedUntilUtc)
		t.Reminders = append(t.Reminders, Reminder{ID: r.ID, At: r.At})
	}
	if t.Subtasks == nil {
		t.Subtasks = []Subtask{}
	}
	for _, s := range t.Subtasks {
		v.id("subtasks[].id", s.ID)
		unique("step", s.ID)
		v.text("subtasks[].title", s.Title, MaxTitleRunes, false)
		v.utc("subtasks[].completedUtc", s.CompletedUtc)
	}
	if t.Attachments == nil {
		t.Attachments = []Attachment{}
	}
	for _, a := range t.Attachments {
		v.id("attachments[].id", a.ID)
		unique("attachment", a.ID)
		v.text("attachments[].fileName", a.FileName, 260, true)
		if !SafeRelativePath(a.RelativePath) {
			v.add("attachments[].relativePath must be a relative path inside the task-file folder")
		}
		if a.Size < 0 {
			v.add("attachments[].size must not be negative")
		}
		added := a.AddedUtc
		v.utc("attachments[].addedUtc", &added)
	}
	return t, v.err()
}

// SafeRelativePath accepts only paths inside the task-file folder: not absolute, no drive, no "..", no empty segments.
func SafeRelativePath(p string) bool {
	if p == "" || len(p) > 520 || strings.ContainsAny(p, ":\x00") || strings.HasPrefix(p, "/") || strings.HasPrefix(p, `\`) {
		return false
	}
	for _, segment := range strings.FieldsFunc(p, func(r rune) bool { return r == '/' || r == '\\' }) {
		if segment == ".." || segment == "." {
			return false
		}
	}
	return !strings.Contains(p, "//") && !strings.Contains(p, `\\`)
}

func ParseList(data []byte, now time.Time) (List, error) {
	var l List
	if err := strictDecode(data, &l); err != nil {
		return List{}, err
	}
	if l.CreatedUtc == "" {
		l.CreatedUtc = now.UTC().Format(time.RFC3339Nano)
	}
	v := &validator{}
	v.id("id", l.ID)
	v.text("name", l.Name, MaxNameRunes, true)
	v.optionalID("groupId", l.GroupID)
	if l.Color != nil && !colorPattern.MatchString(*l.Color) {
		v.add("color must be #RRGGBB")
	}
	v.utc("createdUtc", &l.CreatedUtc)
	return l, v.err()
}

func ParseGroup(data []byte) (Group, error) {
	var in groupInput
	if err := strictDecode(data, &in); err != nil {
		return Group{}, err
	}
	v := &validator{}
	v.id("id", in.ID)
	v.text("name", in.Name, MaxNameRunes, true)
	return in.Group, v.err()
}

func ParseCompletion(data []byte) (CompletionRecord, error) {
	var c CompletionRecord
	if err := strictDecode(data, &c); err != nil {
		return CompletionRecord{}, err
	}
	v := &validator{}
	v.id("id", c.ID)
	v.id("taskId", c.TaskID)
	if c.ListID != "" {
		v.id("listId", c.ListID)
	}
	v.text("title", c.Title, MaxTitleRunes, false)
	v.utc("completedUtc", &c.CompletedUtc)
	v.date("occurrenceDate", c.OccurrenceDate)
	return c, v.err()
}

// MergePatch applies an RFC 7386 JSON merge patch to a JSON object.
func MergePatch(target json.RawMessage, patch json.RawMessage) (json.RawMessage, error) {
	var base, changes map[string]any
	if err := json.Unmarshal(target, &base); err != nil {
		return nil, err
	}
	if err := json.Unmarshal(patch, &changes); err != nil || changes == nil {
		return nil, &ValidationError{Problems: []string{"changes must be a JSON object"}}
	}
	return json.Marshal(merge(base, changes))
}

// ComposePatch combines two merge patches into one that has the effect of applying first, then second. Unlike
// MergePatch, a null in second is kept (it is an instruction to remove the field), so coalescing offline edits such as
// {"title": "x"} then {"myDayDate": null} still removes myDayDate on the server.
func ComposePatch(first, second json.RawMessage) (json.RawMessage, error) {
	var a, b map[string]any
	if err := json.Unmarshal(first, &a); err != nil {
		return nil, err
	}
	if err := json.Unmarshal(second, &b); err != nil || b == nil {
		return nil, &ValidationError{Problems: []string{"changes must be a JSON object"}}
	}
	return json.Marshal(compose(a, b))
}

func compose(a, b map[string]any) map[string]any {
	if a == nil {
		a = map[string]any{}
	}
	for key, value := range b {
		if next, ok := value.(map[string]any); ok {
			if existing, ok := a[key].(map[string]any); ok {
				a[key] = compose(existing, next)
				continue
			}
		}
		a[key] = value // including nil: "remove" survives composition
	}
	return a
}

func merge(base map[string]any, changes map[string]any) map[string]any {
	if base == nil {
		base = map[string]any{}
	}
	for key, value := range changes {
		switch typed := value.(type) {
		case nil:
			delete(base, key)
		case map[string]any:
			existing, _ := base[key].(map[string]any)
			base[key] = merge(existing, typed)
		default:
			base[key] = typed
		}
	}
	return base
}

