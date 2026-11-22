//go:build integration

package httpapi_test

import (
	"context"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/httpapi"
	"alondra1/backend/internal/postgres"
	"alondra1/backend/internal/settings"
	"alondra1/backend/internal/tasks"
	"alondra1/backend/internal/tenancy"
	"alondra1/backend/internal/testdb"
)

type postbox struct {
	mu      sync.Mutex
	codes   map[string]string
	invites map[string]string
}

func (p *postbox) SendSignInCode(_ context.Context, to, code string, _ time.Duration) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.codes[to] = code
	return nil
}

func (p *postbox) SendInvite(_ context.Context, to, _, _, token string, _ time.Duration) error {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.invites[to] = token
	return nil
}

type world struct {
	*fixture
	box   *postbox
	clock *syncClock
}

type syncClock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *syncClock) Now() time.Time { c.mu.Lock(); defer c.mu.Unlock(); return c.now }
func (c *syncClock) Advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(d)
}

func newWorld(t *testing.T) *world {
	pool := testdb.Start(t, true)
	box := &postbox{codes: map[string]string{}, invites: map[string]string{}}
	clock := &syncClock{now: time.Now().UTC()}
	signer, _ := auth.NewTokenSigner([]byte(strings.Repeat("k", 48)))
	opts := auth.DefaultOptions()
	opts.MaxStartsPerIPHour = 1000
	authSvc := auth.NewService(postgres.NewAuthStore(pool), box, signer, []byte(strings.Repeat("o", 48)), clock.Now, opts, testdb.Quiet)
	spaces := tenancy.NewService(postgres.NewTenancyStore(pool), box, clock.Now, testdb.Quiet)
	api := &httpapi.Server{Auth: authSvc, Tenancy: spaces, Log: testdb.Quiet,
		Tasks: tasks.NewService(postgres.NewTaskStore(pool), clock.Now), Settings: settings.NewService(postgres.NewSettingsStore(pool), clock.Now)}
	f := &fixture{server: httptest.NewServer(api.Handler())}
	t.Cleanup(f.server.Close)
	return &world{fixture: f, box: box, clock: clock}
}

// signIn returns an access token for the address (a new device each time).
func (w *world) signIn(t *testing.T, email string) string {
	t.Helper()
	w.clock.Advance(61 * time.Second) // stay clear of the resend cooldown
	start := w.do(t, "POST", "/v1/auth/email/start", "", map[string]string{"email": email})
	if start.status != 202 {
		t.Fatalf("start %s: %d %v", email, start.status, start.body)
	}
	w.box.mu.Lock()
	code := w.box.codes[email]
	w.box.mu.Unlock()
	verify := w.do(t, "POST", "/v1/auth/email/verify", "", map[string]any{"email": email, "code": code, "challenge_id": start.body["challenge_id"], "device": map[string]string{"name": email, "platform": "windows"}})
	if verify.status != 200 {
		t.Fatalf("verify %s: %d %v", email, verify.status, verify.body)
	}
	return verify.body["access_token"].(string)
}

func (w *world) expect(t *testing.T, r response, status int, code string) response {
	t.Helper()
	if r.status != status || (code != "" && errorCode(r) != code) {
		t.Fatalf("expected %d %s, got %d %v", status, code, r.status, r.body)
	}
	return r
}

func (w *world) invite(t *testing.T, token, tenantID, email, role string) string {
	t.Helper()
	r := w.expect(t, w.do(t, "POST", "/v1/tenants/"+tenantID+"/invites", token, map[string]string{"email": email, "role": role}), 201, "")
	if _, leaked := r.body["token"]; leaked {
		t.Fatal("the invitation token must not be in the response")
	}
	w.box.mu.Lock()
	defer w.box.mu.Unlock()
	invitation := w.box.invites[strings.ToLower(email)] // the server sends to the normalized address
	if invitation == "" {
		t.Fatalf("no invitation email for %s", email)
	}
	return invitation
}

func TestSpacesEndToEnd(t *testing.T) {
	w := newWorld(t)
	maya, sam, eve, ali := w.signIn(t, "maya@example.com"), w.signIn(t, "sam@example.com"), w.signIn(t, "eve@example.com"), w.signIn(t, "ali@example.com")

	// Create: explicit type; Personal gets a default name; family needs one.
	family := w.expect(t, w.do(t, "POST", "/v1/tenants", maya, map[string]string{"type": "family", "name": "The Parks"}), 201, "").body
	if family["role"] != "owner" || family["type"] != "family" {
		t.Fatalf("family: %v", family)
	}
	familyID := family["id"].(string)
	personal := w.expect(t, w.do(t, "POST", "/v1/tenants", maya, map[string]string{"type": "personal"}), 201, "").body
	if personal["name"] != "My space" {
		t.Fatalf("personal default name: %v", personal)
	}
	w.expect(t, w.do(t, "POST", "/v1/tenants", maya, map[string]string{"type": "family"}), 422, "INVALID_NAME")
	w.expect(t, w.do(t, "POST", "/v1/tenants", maya, map[string]string{"type": "team", "name": "x"}), 422, "INVALID_TYPE")
	if me := w.do(t, "GET", "/v1/me", maya, nil); len(me.body["memberships"].([]any)) != 2 {
		t.Fatalf("me memberships: %v", me.body)
	}

	t.Run("outsiders cannot see or touch a space, even with its id", func(t *testing.T) {
		for _, probe := range []struct{ method, path string; body any }{
			{"GET", "/v1/tenants/" + familyID, nil},
			{"GET", "/v1/tenants/" + familyID + "/members", nil},
			{"GET", "/v1/tenants/" + familyID + "/invites", nil},
			{"PATCH", "/v1/tenants/" + familyID, map[string]string{"name": "Mine now"}},
			{"DELETE", "/v1/tenants/" + familyID, nil},
			{"POST", "/v1/tenants/" + familyID + "/invites", map[string]string{"email": "eve2@example.com", "role": "member"}},
			{"GET", "/v1/tenants/00000000-0000-7000-8000-000000000000", nil},
			{"GET", "/v1/tenants/not-a-uuid", nil},
		} {
			w.expect(t, w.do(t, probe.method, probe.path, eve, probe.body), 404, "NOT_FOUND")
		}
		if list := w.do(t, "GET", "/v1/tenants", eve, nil); len(list.body["tenants"].([]any)) != 0 {
			t.Fatalf("eve sees spaces: %v", list.body)
		}
	})

	t.Run("an invitation works only for its address, once", func(t *testing.T) {
		w.expect(t, w.do(t, "POST", "/v1/tenants/"+personal["id"].(string)+"/invites", maya, map[string]string{"email": "sam@example.com", "role": "member"}), 409, "PERSONAL_SPACE")
		token := w.invite(t, maya, familyID, "SAM@example.com", "member")
		w.expect(t, w.do(t, "POST", "/v1/invites/"+token+"/accept", eve, nil), 403, "INVITE_EMAIL_MISMATCH")
		joined := w.expect(t, w.do(t, "POST", "/v1/invites/"+token+"/accept", sam, nil), 200, "").body
		if joined["id"] != familyID || joined["role"] != "member" {
			t.Fatalf("joined: %v", joined)
		}
		w.expect(t, w.do(t, "POST", "/v1/invites/"+token+"/accept", sam, nil), 404, "INVITE_INVALID")
		w.expect(t, w.do(t, "POST", "/v1/invites/a1i_forged/accept", sam, nil), 404, "INVITE_INVALID")
		w.expect(t, w.do(t, "POST", "/v1/tenants/"+familyID+"/invites", maya, map[string]string{"email": "sam@example.com", "role": "member"}), 409, "ALREADY_MEMBER")
	})

	t.Run("members cannot manage; nobody invites above their role", func(t *testing.T) {
		w.expect(t, w.do(t, "POST", "/v1/tenants/"+familyID+"/invites", sam, map[string]string{"email": "x@example.com", "role": "member"}), 403, "FORBIDDEN")
		w.expect(t, w.do(t, "PATCH", "/v1/tenants/"+familyID, sam, map[string]string{"name": "Sam's"}), 403, "FORBIDDEN")
		w.expect(t, w.do(t, "DELETE", "/v1/tenants/"+familyID, sam, nil), 403, "FORBIDDEN")
		w.expect(t, w.do(t, "GET", "/v1/tenants/"+familyID+"/invites", sam, nil), 403, "FORBIDDEN")
		w.expect(t, w.do(t, "POST", "/v1/tenants/"+familyID+"/invites", maya, map[string]string{"email": "x@example.com", "role": "owner"}), 422, "INVALID_ROLE")
		members := w.do(t, "GET", "/v1/tenants/"+familyID+"/members", sam, nil).body["members"].([]any)
		if len(members) != 2 || members[0].(map[string]any)["role"] != "owner" {
			t.Fatalf("members (owners first): %v", members)
		}
	})

	var aliID, samID, mayaID string
	t.Run("admins invite and remove members but not owners; only owners change roles", func(t *testing.T) {
		token := w.invite(t, maya, familyID, "ali@example.com", "admin")
		w.expect(t, w.do(t, "POST", "/v1/invites/"+token+"/accept", ali, nil), 200, "")
		for _, m := range w.do(t, "GET", "/v1/tenants/"+familyID+"/members", maya, nil).body["members"].([]any) {
			member := m.(map[string]any)
			switch member["email"] {
			case "ali@example.com":
				aliID = member["user_id"].(string)
			case "sam@example.com":
				samID = member["user_id"].(string)
			case "maya@example.com":
				mayaID = member["user_id"].(string)
			}
		}
		w.invite(t, ali, familyID, "dan@example.com", "admin") // equal role is allowed
		w.expect(t, w.do(t, "PATCH", "/v1/tenants/"+familyID+"/members/"+samID, ali, map[string]string{"role": "admin"}), 403, "FORBIDDEN")
		w.expect(t, w.do(t, "DELETE", "/v1/tenants/"+familyID+"/members/"+mayaID, ali, nil), 403, "FORBIDDEN")
		w.expect(t, w.do(t, "DELETE", "/v1/tenants/"+familyID+"/members/"+samID, ali, nil), 204, "")
		w.expect(t, w.do(t, "GET", "/v1/tenants/"+familyID, sam, nil), 404, "NOT_FOUND")
	})

	t.Run("pending invitations can be listed, revoked, and expire", func(t *testing.T) {
		pending := w.do(t, "GET", "/v1/tenants/"+familyID+"/invites", maya, nil).body["invites"].([]any)
		if len(pending) != 1 || pending[0].(map[string]any)["email"] != "dan@example.com" {
			t.Fatalf("pending: %v", pending)
		}
		revoked := w.invite(t, maya, familyID, "fay@example.com", "member")
		var inviteID string
		for _, i := range w.do(t, "GET", "/v1/tenants/"+familyID+"/invites", maya, nil).body["invites"].([]any) {
			if i.(map[string]any)["email"] == "fay@example.com" {
				inviteID = i.(map[string]any)["id"].(string)
			}
		}
		w.expect(t, w.do(t, "DELETE", "/v1/tenants/"+familyID+"/invites/"+inviteID, maya, nil), 204, "")
		fay := w.signIn(t, "fay@example.com")
		w.expect(t, w.do(t, "POST", "/v1/invites/"+revoked+"/accept", fay, nil), 404, "INVITE_INVALID")

		late := w.invite(t, maya, familyID, "gus@example.com", "member")
		w.clock.Advance(tenancy.InviteTTL + time.Minute)
		gus := w.signIn(t, "gus@example.com")
		w.expect(t, w.do(t, "POST", "/v1/invites/"+late+"/accept", gus, nil), 404, "INVITE_INVALID")
		// Access tokens expired with the clock jump.
		maya, ali, eve = w.signIn(t, "maya@example.com"), w.signIn(t, "ali@example.com"), w.signIn(t, "eve@example.com")
	})

	t.Run("a space keeps an owner; ownership transfers by role change", func(t *testing.T) {
		w.expect(t, w.do(t, "DELETE", "/v1/tenants/"+familyID+"/members/"+mayaID, maya, nil), 409, "LAST_OWNER")
		w.expect(t, w.do(t, "PATCH", "/v1/tenants/"+familyID+"/members/"+mayaID, maya, map[string]string{"role": "member"}), 409, "LAST_OWNER")
		w.expect(t, w.do(t, "PATCH", "/v1/tenants/"+familyID+"/members/"+aliID, maya, map[string]string{"role": "owner"}), 200, "")
		w.expect(t, w.do(t, "DELETE", "/v1/tenants/"+familyID+"/members/"+mayaID, maya, nil), 204, "")
		if v := w.do(t, "GET", "/v1/tenants/"+familyID, ali, nil).body; v["role"] != "owner" {
			t.Fatalf("ali should own the space: %v", v)
		}
	})

	t.Run("concurrent demotions never leave a space without an owner", func(t *testing.T) {
		space := w.expect(t, w.do(t, "POST", "/v1/tenants", ali, map[string]string{"type": "organization", "name": "Race Inc"}), 201, "").body["id"].(string)
		bob := w.signIn(t, "bob@example.com")
		w.expect(t, w.do(t, "POST", "/v1/invites/"+w.invite(t, ali, space, "bob@example.com", "admin")+"/accept", bob, nil), 200, "")
		var bobID string
		for _, m := range w.do(t, "GET", "/v1/tenants/"+space+"/members", ali, nil).body["members"].([]any) {
			if m.(map[string]any)["email"] == "bob@example.com" {
				bobID = m.(map[string]any)["user_id"].(string)
			}
		}
		w.expect(t, w.do(t, "PATCH", "/v1/tenants/"+space+"/members/"+bobID, ali, map[string]string{"role": "owner"}), 200, "")
		var wg sync.WaitGroup
		wg.Add(2)
		go func() { defer wg.Done(); w.do(t, "PATCH", "/v1/tenants/"+space+"/members/"+bobID, ali, map[string]string{"role": "member"}) }()
		go func() { defer wg.Done(); w.do(t, "PATCH", "/v1/tenants/"+space+"/members/"+aliID, bob, map[string]string{"role": "member"}) }()
		wg.Wait()
		owners := 0
		for _, m := range w.do(t, "GET", "/v1/tenants/"+space+"/members", ali, nil).body["members"].([]any) {
			if m.(map[string]any)["role"] == "owner" {
				owners++
			}
		}
		if owners != 1 {
			t.Fatalf("expected exactly one owner after the race, got %d", owners)
		}
	})

	t.Run("devices: list with current flag and revoke another device", func(t *testing.T) {
		phone := w.signIn(t, "ali@example.com")
		devices := w.do(t, "GET", "/v1/devices", phone, nil).body["devices"].([]any)
		if len(devices) < 2 || devices[0].(map[string]any)["current"] != true {
			t.Fatalf("devices: %v", devices)
		}
		other := devices[1].(map[string]any)["id"].(string)
		w.expect(t, w.do(t, "DELETE", "/v1/devices/"+other, eve, nil), 404, "NOT_FOUND")
		w.expect(t, w.do(t, "DELETE", "/v1/devices/"+other, phone, nil), 204, "")
		w.expect(t, w.do(t, "GET", "/v1/me", ali, nil), 401, "UNAUTHENTICATED") // ali's laptop was that device
		ali = phone
	})

	t.Run("account deletion: blocked while sole owner with members, then releases the address", func(t *testing.T) {
		held := w.expect(t, w.do(t, "POST", "/v1/tenants", ali, map[string]string{"type": "organization", "name": "Held"}), 201, "").body["id"].(string)
		w.expect(t, w.do(t, "POST", "/v1/invites/"+w.invite(t, ali, held, "eve@example.com", "member")+"/accept", eve, nil), 200, "")
		blocked := w.expect(t, w.do(t, "DELETE", "/v1/me", ali, nil), 409, "OWNERSHIP_TRANSFER_REQUIRED")
		ids := blocked.body["error"].(map[string]any)["tenant_ids"].([]any)
		found := false
		for _, id := range ids {
			found = found || id == held
		}
		if !found {
			t.Fatalf("tenant_ids must list the blocking space: %v", blocked.body)
		}
		if v := w.do(t, "GET", "/v1/tenants/"+familyID, ali, nil); v.status != 200 {
			t.Fatal("a refused deletion must change nothing")
		}
		if v := w.do(t, "GET", "/v1/me", ali, nil); v.status != 200 {
			t.Fatal("a refused deletion must keep the account signed in")
		}

		w.expect(t, w.do(t, "DELETE", "/v1/me", maya, nil), 204, "")
		w.expect(t, w.do(t, "GET", "/v1/me", maya, nil), 401, "UNAUTHENTICATED")
		again := w.signIn(t, "maya@example.com")
		me := w.expect(t, w.do(t, "GET", "/v1/me", again, nil), 200, "").body
		if me["id"] == mayaID || len(me["memberships"].([]any)) != 0 {
			t.Fatalf("a new sign-up must be a fresh account: %v", me)
		}
	})
}
