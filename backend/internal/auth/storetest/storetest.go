// Package storetest is the behavioral contract every auth.Store must meet. The in-memory store runs it in unit tests;
// the PostgreSQL store runs it in integration tests against a real database.
package storetest

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"alondra1/backend/internal/auth"
)

var base = time.Date(2026, 11, 21, 9, 0, 0, 0, time.UTC)

func id(t *testing.T) string {
	t.Helper()
	value, err := auth.NewUUIDv4()
	if err != nil {
		t.Fatal(err)
	}
	return value
}

// Run executes the suite. newStore must return an empty store for each call.
func Run(t *testing.T, newStore func(t *testing.T) auth.Store) {
	ctx := context.Background()

	t.Run("new challenge supersedes older ones for the same email only", func(t *testing.T) {
		s := newStore(t)
		first := auth.Challenge{ID: id(t), EmailNormalized: "a@example.com", OTPHMAC: mac(1), ExpiresAt: base.Add(10 * time.Minute), CreatedAt: base, RequestIPHash: []byte{9}}
		other := auth.Challenge{ID: id(t), EmailNormalized: "b@example.com", OTPHMAC: mac(2), ExpiresAt: base.Add(10 * time.Minute), CreatedAt: base, RequestIPHash: []byte{9}}
		second := auth.Challenge{ID: id(t), EmailNormalized: "a@example.com", OTPHMAC: mac(3), ExpiresAt: base.Add(11 * time.Minute), CreatedAt: base.Add(time.Minute), RequestIPHash: []byte{8}}
		for _, c := range []auth.Challenge{first, other, second} {
			if err := s.CreateChallenge(ctx, c); err != nil {
				t.Fatal(err)
			}
		}
		got, err := s.GetChallenge(ctx, first.ID)
		if err != nil || got.ConsumedAt == nil {
			t.Fatalf("first challenge should be superseded: %+v %v", got, err)
		}
		if got, _ := s.GetChallenge(ctx, other.ID); got.ConsumedAt != nil {
			t.Fatal("another email's challenge must not be touched")
		}
		if got, _ := s.GetChallenge(ctx, second.ID); got.ConsumedAt != nil || string(got.OTPHMAC) != string(mac(3)) || got.EmailNormalized != "a@example.com" {
			t.Fatalf("second challenge stored wrong: %+v", got)
		}
		count, latest, err := s.RecentChallenges(ctx, "a@example.com", base.Add(-time.Hour))
		if err != nil || count != 2 || !latest.Equal(second.CreatedAt) {
			t.Fatalf("recent = %d %v %v", count, latest, err)
		}
		if count, _, _ := s.RecentChallenges(ctx, "a@example.com", base.Add(30*time.Second)); count != 1 {
			t.Fatalf("since filter: %d", count)
		}
		if n, err := s.RecentChallengesByIP(ctx, []byte{9}, base.Add(-time.Hour)); err != nil || n != 2 {
			t.Fatalf("by ip = %d %v", n, err)
		}
		if _, err := s.GetChallenge(ctx, id(t)); !errors.Is(err, auth.ErrNotFound) {
			t.Fatalf("missing challenge: %v", err)
		}
	})

	t.Run("consume is one-time, respects expiry and attempts, and creates the user once", func(t *testing.T) {
		s := newStore(t)
		c := auth.Challenge{ID: id(t), EmailNormalized: "c@example.com", OTPHMAC: mac(1), ExpiresAt: base.Add(10 * time.Minute), CreatedAt: base, RequestIPHash: []byte{1}}
		if err := s.CreateChallenge(ctx, c); err != nil {
			t.Fatal(err)
		}
		if n, err := s.RecordFailedAttempt(ctx, c.ID); err != nil || n != 1 {
			t.Fatalf("attempt = %d %v", n, err)
		}
		userID := id(t)
		user, err := s.ConsumeChallenge(ctx, c.ID, base.Add(time.Minute), 5, userID)
		if err != nil || user.ID != userID || user.EmailNormalized != "c@example.com" {
			t.Fatalf("consume = %+v %v", user, err)
		}
		if _, err := s.ConsumeChallenge(ctx, c.ID, base.Add(time.Minute), 5, id(t)); !errors.Is(err, auth.ErrChallengeInvalid) {
			t.Fatalf("replay must fail: %v", err)
		}
		if _, err := s.RecordFailedAttempt(ctx, c.ID); !errors.Is(err, auth.ErrChallengeInvalid) {
			t.Fatalf("attempt on consumed challenge: %v", err)
		}
		// A later challenge for the same email signs into the same user.
		again := auth.Challenge{ID: id(t), EmailNormalized: "c@example.com", OTPHMAC: mac(1), ExpiresAt: base.Add(20 * time.Minute), CreatedAt: base.Add(5 * time.Minute), RequestIPHash: []byte{1}}
		if err := s.CreateChallenge(ctx, again); err != nil {
			t.Fatal(err)
		}
		same, err := s.ConsumeChallenge(ctx, again.ID, base.Add(6*time.Minute), 5, id(t))
		if err != nil || same.ID != userID {
			t.Fatalf("existing user expected: %+v %v", same, err)
		}
		if got, err := s.GetUser(ctx, userID); err != nil || got.EmailNormalized != "c@example.com" {
			t.Fatalf("get user: %+v %v", got, err)
		}

		expired := auth.Challenge{ID: id(t), EmailNormalized: "d@example.com", OTPHMAC: mac(1), ExpiresAt: base.Add(time.Minute), CreatedAt: base, RequestIPHash: []byte{1}}
		locked := auth.Challenge{ID: id(t), EmailNormalized: "e@example.com", OTPHMAC: mac(1), ExpiresAt: base.Add(time.Hour), CreatedAt: base, RequestIPHash: []byte{1}}
		for _, ch := range []auth.Challenge{expired, locked} {
			if err := s.CreateChallenge(ctx, ch); err != nil {
				t.Fatal(err)
			}
		}
		if _, err := s.ConsumeChallenge(ctx, expired.ID, base.Add(time.Minute), 5, id(t)); !errors.Is(err, auth.ErrChallengeInvalid) {
			t.Fatalf("expired must fail: %v", err)
		}
		for i := 0; i < 5; i++ {
			if _, err := s.RecordFailedAttempt(ctx, locked.ID); err != nil {
				t.Fatal(err)
			}
		}
		if _, err := s.ConsumeChallenge(ctx, locked.ID, base.Add(time.Minute), 5, id(t)); !errors.Is(err, auth.ErrChallengeInvalid) {
			t.Fatalf("locked must fail: %v", err)
		}
	})

	t.Run("concurrent consumes succeed exactly once", func(t *testing.T) {
		s := newStore(t)
		c := auth.Challenge{ID: id(t), EmailNormalized: "race@example.com", OTPHMAC: mac(1), ExpiresAt: base.Add(time.Hour), CreatedAt: base, RequestIPHash: []byte{1}}
		if err := s.CreateChallenge(ctx, c); err != nil {
			t.Fatal(err)
		}
		var wg sync.WaitGroup
		var mu sync.Mutex
		wins := 0
		for i := 0; i < 8; i++ {
			wg.Add(1)
			go func() {
				defer wg.Done()
				newID, _ := auth.NewUUIDv4()
				if _, err := s.ConsumeChallenge(ctx, c.ID, base.Add(time.Minute), 5, newID); err == nil {
					mu.Lock()
					wins++
					mu.Unlock()
				}
			}()
		}
		wg.Wait()
		if wins != 1 {
			t.Fatalf("wins = %d", wins)
		}
	})

	t.Run("sessions rotate once and revocation covers rotated rows", func(t *testing.T) {
		s := newStore(t)
		userID, deviceID, family := id(t), id(t), id(t)
		mustUser(t, s, userID, "f@example.com")
		if err := s.CreateDevice(ctx, auth.Device{ID: deviceID, UserID: userID, Name: "Laptop", Platform: "windows", SeenAt: base}); err != nil {
			t.Fatal(err)
		}
		first := auth.Session{ID: id(t), UserID: userID, DeviceID: deviceID, RefreshHash: []byte("h1"), TokenFamilyID: family, ExpiresAt: base.Add(time.Hour), CreatedAt: base, LastUsedAt: base}
		if err := s.CreateSession(ctx, first); err != nil {
			t.Fatal(err)
		}
		got, err := s.GetSessionByRefreshHash(ctx, []byte("h1"))
		if err != nil || got.ID != first.ID || got.RevokedAt != nil || got.TokenFamilyID != family {
			t.Fatalf("by hash: %+v %v", got, err)
		}
		second := first
		second.ID, second.RefreshHash = id(t), []byte("h2")
		if err := s.RotateSession(ctx, first.ID, second, base.Add(time.Minute)); err != nil {
			t.Fatal(err)
		}
		third := first
		third.ID, third.RefreshHash = id(t), []byte("h3")
		if err := s.RotateSession(ctx, first.ID, third, base.Add(time.Minute)); !errors.Is(err, auth.ErrSessionRotated) {
			t.Fatalf("second rotation of the same session must fail: %v", err)
		}
		if _, err := s.GetSession(ctx, third.ID); !errors.Is(err, auth.ErrNotFound) {
			t.Fatalf("losing rotation must not insert: %v", err)
		}
		old, _ := s.GetSession(ctx, first.ID)
		if old.RevokedAt == nil || old.RevokeReason != auth.RevokeRotated {
			t.Fatalf("old session: %+v", old)
		}
		if err := s.RevokeFamily(ctx, family, auth.RevokeReuse, base.Add(2*time.Minute)); err != nil {
			t.Fatal(err)
		}
		for _, sid := range []string{first.ID, second.ID} {
			got, _ := s.GetSession(ctx, sid)
			if got.RevokedAt == nil || got.RevokeReason != auth.RevokeReuse {
				t.Fatalf("family revocation must cover %s: %+v", sid, got)
			}
		}
		old, _ = s.GetSession(ctx, first.ID)
		if !old.RevokedAt.Equal(base.Add(time.Minute)) {
			t.Fatalf("revoked_at must keep its first value: %v", old.RevokedAt)
		}
		if err := s.TouchDevice(ctx, deviceID, base.Add(3*time.Minute)); err != nil {
			t.Fatal(err)
		}
	})

	t.Run("revoke all for user leaves other users alone", func(t *testing.T) {
		s := newStore(t)
		a, b := id(t), id(t)
		mustUser(t, s, a, "g@example.com")
		mustUser(t, s, b, "h@example.com")
		sessionsOf := map[string][]string{}
		for _, owner := range []string{a, a, b} {
			deviceID, sessionID := id(t), id(t)
			if err := s.CreateDevice(ctx, auth.Device{ID: deviceID, UserID: owner, Name: "x", Platform: "other", SeenAt: base}); err != nil {
				t.Fatal(err)
			}
			if err := s.CreateSession(ctx, auth.Session{ID: sessionID, UserID: owner, DeviceID: deviceID, RefreshHash: []byte(id(t)), TokenFamilyID: id(t), ExpiresAt: base.Add(time.Hour), CreatedAt: base, LastUsedAt: base}); err != nil {
				t.Fatal(err)
			}
			sessionsOf[owner] = append(sessionsOf[owner], sessionID)
		}
		if err := s.RevokeAllForUser(ctx, a, auth.RevokeLogoutAll, base); err != nil {
			t.Fatal(err)
		}
		for _, sid := range sessionsOf[a] {
			if got, _ := s.GetSession(ctx, sid); got.RevokedAt == nil || got.RevokeReason != auth.RevokeLogoutAll {
				t.Fatalf("user a session %s should be revoked: %+v", sid, got)
			}
		}
		if got, _ := s.GetSession(ctx, sessionsOf[b][0]); got.RevokedAt != nil {
			t.Fatalf("user b must stay signed in: %+v", got)
		}
	})

	t.Run("revoking a device revokes its sessions and only the owner can", func(t *testing.T) {
		s := newStore(t)
		owner, stranger := id(t), id(t)
		mustUser(t, s, owner, "j@example.com")
		mustUser(t, s, stranger, "k@example.com")
		laptop, phone := id(t), id(t)
		for i, deviceID := range []string{laptop, phone} {
			if err := s.CreateDevice(ctx, auth.Device{ID: deviceID, UserID: owner, Name: "d", Platform: "windows", SeenAt: base.Add(time.Duration(i) * time.Minute)}); err != nil {
				t.Fatal(err)
			}
		}
		laptopSession, phoneSession := id(t), id(t)
		for deviceID, sessionID := range map[string]string{laptop: laptopSession, phone: phoneSession} {
			if err := s.CreateSession(ctx, auth.Session{ID: sessionID, UserID: owner, DeviceID: deviceID, RefreshHash: []byte(id(t)), TokenFamilyID: id(t), ExpiresAt: base.Add(time.Hour), CreatedAt: base, LastUsedAt: base}); err != nil {
				t.Fatal(err)
			}
		}
		devices, err := s.ListDevices(ctx, owner)
		if err != nil || len(devices) != 2 || devices[0].ID != phone {
			t.Fatalf("list (most recent first): %+v %v", devices, err)
		}
		if err := s.RevokeDevice(ctx, stranger, laptop, base); !errors.Is(err, auth.ErrNotFound) {
			t.Fatalf("another user revoked the device: %v", err)
		}
		if err := s.RevokeDevice(ctx, owner, laptop, base); err != nil {
			t.Fatal(err)
		}
		if err := s.RevokeDevice(ctx, owner, laptop, base); !errors.Is(err, auth.ErrNotFound) {
			t.Fatalf("second revocation: %v", err)
		}
		if got, _ := s.GetSession(ctx, laptopSession); got.RevokedAt == nil || got.RevokeReason != auth.RevokeDevice {
			t.Fatalf("laptop session must be revoked: %+v", got)
		}
		if got, _ := s.GetSession(ctx, phoneSession); got.RevokedAt != nil {
			t.Fatal("the other device must stay signed in")
		}
		if devices, _ := s.ListDevices(ctx, owner); len(devices) != 1 || devices[0].ID != phone {
			t.Fatalf("revoked devices are not listed: %+v", devices)
		}
	})

	t.Run("audit accepts events with and without actors", func(t *testing.T) {
		s := newStore(t)
		userID := id(t)
		mustUser(t, s, userID, "i@example.com")
		for _, e := range []auth.AuditEvent{
			{ActorUserID: userID, EventType: "auth.sign_in", TargetID: id(t), Metadata: map[string]string{"device_id": "x"}, CreatedAt: base},
			{EventType: "auth.anonymous", CreatedAt: base},
		} {
			if err := s.Audit(ctx, e); err != nil {
				t.Fatal(err)
			}
		}
	})
}

// mustUser creates a user through the public contract: a challenge consumed for that email.
func mustUser(t *testing.T, s auth.Store, userID, email string) {
	t.Helper()
	ctx := context.Background()
	c := auth.Challenge{ID: id(t), EmailNormalized: email, OTPHMAC: mac(1), ExpiresAt: base.Add(time.Hour), CreatedAt: base, RequestIPHash: []byte{1}}
	if err := s.CreateChallenge(ctx, c); err != nil {
		t.Fatal(err)
	}
	if _, err := s.ConsumeChallenge(ctx, c.ID, base, 5, userID); err != nil {
		t.Fatal(err)
	}
}

// mac returns a 32-byte stand-in for an OTP HMAC.
func mac(seed byte) []byte {
	value := make([]byte, 32)
	for i := range value {
		value[i] = seed
	}
	return value
}
