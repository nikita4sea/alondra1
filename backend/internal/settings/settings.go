// Package settings stores portable, non-secret settings in explicit, versioned namespaces (docs/BACKEND_SPEC.md §8).
//
// Only the namespaces below exist. Each has a strict schema: unknown keys are rejected, so nothing outside the allowlist
// (passwords, tokens, client ids, local paths, window geometry, notification markers) can be stored by accident. User
// namespaces are private to one user in one space; space namespaces are read by every member and written by owners and
// admins.
package settings

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"regexp"
	"time"
	"unicode/utf8"

	"alondra1/backend/internal/tasks"
)

const MaxBytes = 256 << 10

type Level int

const (
	User Level = iota
	Space
)

// Namespace validates one settings document.
type Namespace struct {
	Name     string
	Level    Level
	Validate func(data []byte) (json.RawMessage, error)
}

var colorPattern = regexp.MustCompile(`^#[0-9A-Fa-f]{6}$`)
var tokenPattern = regexp.MustCompile(`^[A-Z][A-Za-z]{2,40}Brush$`)

// appearance: theme choice and palette overrides. Background pictures are local files and are not part of it.
type appearance struct {
	Theme       *string           `json:"theme,omitempty"`
	LightColors map[string]string `json:"lightColors,omitempty"`
	DarkColors  map[string]string `json:"darkColors,omitempty"`
}

// todoPreferences: the cross-device part of TaskPreferences. selectedView and detailsOpen stay on the device.
type todoPreferences struct {
	SortByView           map[string]string `json:"sortByView,omitempty"`
	SortDescendingByView map[string]bool   `json:"sortDescendingByView,omitempty"`
	StatusFilter         *string           `json:"statusFilter,omitempty"`
	ImportantOnlyFilter  *bool             `json:"importantOnlyFilter,omitempty"`
	ShowCounts           *bool             `json:"showCounts,omitempty"`
	CalendarMode         *string           `json:"calendarMode,omitempty"`
	NotificationsEnabled *bool             `json:"notificationsEnabled,omitempty"`
	SnoozeMinutes        *int              `json:"snoozeMinutes,omitempty"`
}

// spaceProfile: shared presentation of a space.
type spaceProfile struct {
	AccentColor *string `json:"accentColor,omitempty"`
	Description *string `json:"description,omitempty"`
}

var namespaces = map[string]Namespace{
	"appearance": {Name: "appearance", Level: User, Validate: func(data []byte) (json.RawMessage, error) {
		var a appearance
		if err := strict(data, &a); err != nil {
			return nil, err
		}
		var problems []string
		if a.Theme != nil && *a.Theme != "System" && *a.Theme != "Light" && *a.Theme != "Dark" {
			problems = append(problems, "theme must be System, Light, or Dark")
		}
		for _, palette := range []map[string]string{a.LightColors, a.DarkColors} {
			if len(palette) > 64 {
				problems = append(problems, "at most 64 color overrides per theme")
			}
			for token, color := range palette {
				if !tokenPattern.MatchString(token) || !colorPattern.MatchString(color) {
					problems = append(problems, fmt.Sprintf("color override %q must map a ...Brush token to #RRGGBB", token))
				}
			}
		}
		return finish(a, problems)
	}},
	"todo.preferences": {Name: "todo.preferences", Level: User, Validate: func(data []byte) (json.RawMessage, error) {
		var p todoPreferences
		if err := strict(data, &p); err != nil {
			return nil, err
		}
		var problems []string
		sorts := map[string]bool{"Manual": true, "DueDate": true, "Importance": true, "Created": true, "Alphabetical": true}
		if len(p.SortByView) > 500 || len(p.SortDescendingByView) > 500 {
			problems = append(problems, "at most 500 views")
		}
		for view, mode := range p.SortByView {
			if !sorts[mode] || utf8.RuneCountInString(view) > 200 {
				problems = append(problems, fmt.Sprintf("sortByView[%q] must be Manual, DueDate, Importance, Created, or Alphabetical", view))
			}
		}
		for view := range p.SortDescendingByView {
			if utf8.RuneCountInString(view) > 200 {
				problems = append(problems, "view keys must be at most 200 characters")
			}
		}
		if p.StatusFilter != nil && *p.StatusFilter != "Open" && *p.StatusFilter != "All" && *p.StatusFilter != "Completed" {
			problems = append(problems, "statusFilter must be Open, All, or Completed")
		}
		if p.CalendarMode != nil && *p.CalendarMode != "Day" && *p.CalendarMode != "Week" && *p.CalendarMode != "Month" {
			problems = append(problems, "calendarMode must be Day, Week, or Month")
		}
		if p.SnoozeMinutes != nil && (*p.SnoozeMinutes < 1 || *p.SnoozeMinutes > 1440) {
			problems = append(problems, "snoozeMinutes must be 1 to 1440")
		}
		return finish(p, problems)
	}},
	"space.profile": {Name: "space.profile", Level: Space, Validate: func(data []byte) (json.RawMessage, error) {
		var p spaceProfile
		if err := strict(data, &p); err != nil {
			return nil, err
		}
		var problems []string
		if p.AccentColor != nil && !colorPattern.MatchString(*p.AccentColor) {
			problems = append(problems, "accentColor must be #RRGGBB")
		}
		if p.Description != nil && utf8.RuneCountInString(*p.Description) > 500 {
			problems = append(problems, "description must be at most 500 characters")
		}
		return finish(p, problems)
	}},
}

// Lookup returns the namespace with this name at this level.
func Lookup(name string, level Level) (Namespace, bool) {
	ns, ok := namespaces[name]
	return ns, ok && ns.Level == level
}

func strict(data []byte, target any) error {
	if len(data) > MaxBytes {
		return &tasks.ValidationError{Problems: []string{fmt.Sprintf("settings are limited to %d bytes", MaxBytes)}}
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		return &tasks.ValidationError{Problems: []string{"not allowed in this namespace: " + err.Error()}}
	}
	if err := decoder.Decode(&struct{}{}); err != io.EOF {
		return &tasks.ValidationError{Problems: []string{"exactly one JSON object expected"}}
	}
	return nil
}

func finish(value any, problems []string) (json.RawMessage, error) {
	if len(problems) > 0 {
		return nil, &tasks.ValidationError{Problems: problems}
	}
	return json.Marshal(value)
}

// ---------- service ----------

type Doc struct {
	Namespace string
	Data      json.RawMessage
	Version   int64 // 0: never written; Data is {}
	UpdatedAt time.Time
}

var (
	ErrUnknownNamespace = errors.New("unknown settings namespace")
	ErrForbidden        = errors.New("only owners and admins change space settings")
)

type Tx interface {
	Get(ctx context.Context, level Level, namespace string, lock bool) (Doc, error) // Version 0 if missing
	Put(ctx context.Context, level Level, doc Doc) error                              // insert or update
}

type Store interface {
	// InScope checks membership like tasks.Store and passes the caller's role.
	InScope(ctx context.Context, scope tasks.Scope, fn func(tx Tx, role string) error) error
}

type Service struct {
	store Store
	now   func() time.Time
}

func NewService(store Store, now func() time.Time) *Service { return &Service{store: store, now: now} }

func (s *Service) Get(ctx context.Context, scope tasks.Scope, level Level, name string) (Doc, error) {
	if _, ok := Lookup(name, level); !ok {
		return Doc{}, ErrUnknownNamespace
	}
	var doc Doc
	err := s.store.InScope(ctx, scope, func(tx Tx, _ string) (err error) {
		doc, err = tx.Get(ctx, level, name, false)
		return err
	})
	return doc, err
}

// Write replaces (patch=false) or merge-patches (patch=true) the document if expected is its current version
// (0 for a namespace never written).
func (s *Service) Write(ctx context.Context, scope tasks.Scope, level Level, name string, expected int64, body json.RawMessage, patch bool) (Doc, error) {
	ns, ok := Lookup(name, level)
	if !ok {
		return Doc{}, ErrUnknownNamespace
	}
	if expected < 0 {
		return Doc{}, tasks.ErrPrecondition
	}
	var out Doc
	err := s.store.InScope(ctx, scope, func(tx Tx, role string) error {
		if level == Space && role != "owner" && role != "admin" {
			return ErrForbidden
		}
		current, err := tx.Get(ctx, level, name, true)
		if err != nil {
			return err
		}
		if current.Version != expected {
			return &tasks.ConflictError{Kind: "version", Current: &tasks.Record{ID: name, Data: current.Data, Version: current.Version, UpdatedAt: current.UpdatedAt}}
		}
		next := body
		if patch {
			if next, err = tasks.MergePatch(current.Data, body); err != nil {
				return err
			}
		}
		clean, err := ns.Validate(next)
		if err != nil {
			return err
		}
		out = Doc{Namespace: name, Data: clean, Version: current.Version + 1, UpdatedAt: s.now().UTC()}
		return tx.Put(ctx, level, out)
	})
	return out, err
}

// UserNamespaces lists the user-level namespaces, for export.
func UserNamespaces() []string {
	var names []string
	for name, ns := range namespaces {
		if ns.Level == User {
			names = append(names, name)
		}
	}
	return names
}

