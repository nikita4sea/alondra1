package cloudsync

import (
	"strings"
	"testing"

	"alondra1/backend/internal/tasks"
)

func TestCursorsSurviveATokenKeyRotation(t *testing.T) {
	scope := tasks.Scope{TenantID: "t", UserID: "u"}
	oldKey, newKey := []byte(strings.Repeat("a", 48)), []byte(strings.Repeat("b", 48))
	before := NewService(nil, oldKey)
	cursor := before.sign(token{Tenant: "t", User: "u", Sequence: 42})

	after := NewService(nil, newKey, oldKey)
	if got, err := after.verify(cursor, scope, false); err != nil || got.Sequence != 42 {
		t.Fatalf("an old cursor must stay valid during the rotation: %v %v", got, err)
	}
	if _, err := NewService(nil, newKey).verify(cursor, scope, false); err != ErrInvalidCursor {
		t.Fatal("once the previous key is removed, old cursors are invalid (clients bootstrap again)")
	}
	if _, err := after.verify(cursor, tasks.Scope{TenantID: "t", UserID: "other"}, false); err != ErrInvalidCursor {
		t.Fatal("cursors stay bound to their user")
	}
}
