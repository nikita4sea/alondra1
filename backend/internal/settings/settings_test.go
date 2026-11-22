package settings

import (
	"strings"
	"testing"
)

func validate(t *testing.T, name string, level Level, body string) error {
	t.Helper()
	ns, ok := Lookup(name, level)
	if !ok {
		t.Fatalf("namespace %s missing", name)
	}
	_, err := ns.Validate([]byte(body))
	return err
}

func TestOnlyAllowlistedKeysAreAccepted(t *testing.T) {
	// Secrets and device-only state are not representable: there is no key for them.
	for _, body := range []string{
		`{"theme":"Dark","password":"hunter2"}`,
		`{"microsoftClientId":"abc"}`,
		`{"oauthToken":"x"}`,
		`{"windowLeft":100}`,
		`{"background":{"customPath":"C:\\Users\\me\\pic.jpg"}}`,
	} {
		if validate(t, "appearance", User, body) == nil {
			t.Errorf("accepted %s", body)
		}
	}
	for _, body := range []string{`{"selectedView":"smart:MyDay"}`, `{"detailsOpen":true}`} {
		if validate(t, "todo.preferences", User, body) == nil {
			t.Errorf("device-local preference accepted: %s", body)
		}
	}
}

func TestValidDocuments(t *testing.T) {
	if err := validate(t, "appearance", User, `{"theme":"Dark","darkColors":{"ChromeBrush":"#16151F"},"lightColors":{}}`); err != nil {
		t.Fatal(err)
	}
	if err := validate(t, "todo.preferences", User, `{"sortByView":{"smart:MyDay":"DueDate"},"sortDescendingByView":{"list:x":true},"statusFilter":"All","calendarMode":"Week","snoozeMinutes":15,"showCounts":false}`); err != nil {
		t.Fatal(err)
	}
	if err := validate(t, "space.profile", Space, `{"accentColor":"#6C6DB0","description":"The Parks family"}`); err != nil {
		t.Fatal(err)
	}
}

func TestValuesAreChecked(t *testing.T) {
	for name, c := range map[string]struct {
		ns   string
		lvl  Level
		body string
	}{
		"theme":        {"appearance", User, `{"theme":"Neon"}`},
		"color":        {"appearance", User, `{"darkColors":{"ChromeBrush":"red"}}`},
		"token":        {"appearance", User, `{"darkColors":{"<script>":"#000000"}}`},
		"sort":         {"todo.preferences", User, `{"sortByView":{"x":"Random"}}`},
		"snooze":       {"todo.preferences", User, `{"snoozeMinutes":0}`},
		"description":  {"space.profile", Space, `{"description":"` + strings.Repeat("x", 501) + `"}`},
		"two objects":  {"space.profile", Space, `{}{}`},
	} {
		if validate(t, c.ns, c.lvl, c.body) == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

func TestLevelsAreSeparate(t *testing.T) {
	if _, ok := Lookup("appearance", Space); ok {
		t.Fatal("a user namespace must not be writable as a space namespace")
	}
	if _, ok := Lookup("space.profile", User); ok {
		t.Fatal("a space namespace must not be a user namespace")
	}
	if _, ok := Lookup("secrets", User); ok {
		t.Fatal("unknown namespace")
	}
}
