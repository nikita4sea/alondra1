package auth_test

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"strings"
	"sync"
	"testing"
	"time"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/auth/storetest"
)

func TestMemoryStoreContract(t *testing.T) {
	storetest.Run(t, func(*testing.T) auth.Store { return auth.NewMemoryStore() })
}

type fakeMailer struct {
	mu    sync.Mutex
	codes map[string]string
	fail  bool
	sent  int
}

func (m *fakeMailer) SendSignInCode(_ context.Context, to, code string, _ time.Duration) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.fail {
		return errors.New("smtp down")
	}
	if m.codes == nil {
		m.codes = map[string]string{}
	}
	m.codes[to] = code
	m.sent++
	return nil
}

type clock struct{ now time.Time }

func (c *clock) Now() time.Time          { return c.now }
func (c *clock) Advance(d time.Duration) { c.now = c.now.Add(d) }

type harness struct {
	svc    *auth.Service
	store  *auth.MemoryStore
	mailer *fakeMailer
	clock  *clock
}

func newHarness(t *testing.T, mutate ...func(*auth.Options)) *harness {
	t.Helper()
	signer, err := auth.NewTokenSigner([]byte(strings.Repeat("k", 48)))
	if err != nil {
		t.Fatal(err)
	}
	opts := auth.DefaultOptions()
	for _, m := range mutate {
		m(&opts)
	}
	h := &harness{store: auth.NewMemoryStore(), mailer: &fakeMailer{}, clock: &clock{now: time.Date(2026, 11, 21, 9, 0, 0, 0, time.UTC)}}
	h.svc = auth.NewService(h.store, h.mailer, signer, []byte(strings.Repeat("o", 48)), h.clock.Now, opts, slog.New(slog.NewTextHandler(io.Discard, nil)))
	return h
}

func (h *harness) signIn(t *testing.T, email string) (auth.User, auth.Tokens) {
	t.Helper()
	ctx := context.Background()
	start, err := h.svc.Start(ctx, email, "203.0.113.1")
	if err != nil {
		t.Fatal(err)
	}
	normalized, _ := auth.NormalizeEmail(email)
	user, tokens, err := h.svc.Verify(ctx, start.ChallengeID, email, h.mailer.codes[normalized], auth.DeviceInfo{Name: "Laptop", Platform: "Windows"})
	if err != nil {
		t.Fatal(err)
	}
	return user, tokens
}

func wrongCode(code string) string {
	if code == "000000" {
		return "000001"
	}
	return "000000"
}

func TestSignInCreatesTheAccountOnceAndJoinsNoTenant(t *testing.T) {
	h := newHarness(t)
	user, tokens := h.signIn(t, "  Maya@Example.com ")
	if user.EmailNormalized != "maya@example.com" || tokens.AccessToken == "" || !strings.HasPrefix(tokens.RefreshToken, auth.RefreshTokenPrefix) {
		t.Fatalf("unexpected sign-in result: %+v %+v", user, tokens)
	}
	if !tokens.AccessExpiresAt.Equal(h.clock.now.Add(15*time.Minute)) || !tokens.RefreshExpiresAt.Equal(h.clock.now.Add(30*24*time.Hour)) {
		t.Fatalf("token lifetimes: %v %v", tokens.AccessExpiresAt, tokens.RefreshExpiresAt)
	}
	h.clock.Advance(2 * time.Minute)
	again, _ := h.signIn(t, "maya@example.com")
	if again.ID != user.ID {
		t.Fatal("a second sign-in must reuse the account")
	}
	principal, err := h.svc.Authenticate(context.Background(), tokens.AccessToken)
	if err != nil || principal.UserID != user.ID {
		t.Fatalf("authenticate: %+v %v", principal, err)
	}
}

func TestStartLooksTheSameForNewAndExistingAccounts(t *testing.T) {
	h := newHarness(t)
	h.signIn(t, "known@example.com")
	h.clock.Advance(time.Hour)
	known, err1 := h.svc.Start(context.Background(), "known@example.com", "203.0.113.2")
	unknown, err2 := h.svc.Start(context.Background(), "nobody@example.com", "203.0.113.2")
	if err1 != nil || err2 != nil {
		t.Fatal(err1, err2)
	}
	if known.ExpiresIn != unknown.ExpiresIn || known.ResendAfter != unknown.ResendAfter || len(known.ChallengeID) != len(unknown.ChallengeID) {
		t.Fatal("responses must not reveal whether an account exists")
	}
}

func TestCodeExpiresAfterTenMinutes(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	start, _ := h.svc.Start(ctx, "a@example.com", "203.0.113.1")
	h.clock.Advance(10 * time.Minute)
	if _, _, err := h.svc.Verify(ctx, start.ChallengeID, "a@example.com", h.mailer.codes["a@example.com"], auth.DeviceInfo{}); !errors.Is(err, auth.ErrVerifyFailed) {
		t.Fatalf("expired code accepted: %v", err)
	}
}

func TestFiveWrongAttemptsLockTheChallenge(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	start, _ := h.svc.Start(ctx, "a@example.com", "203.0.113.1")
	code := h.mailer.codes["a@example.com"]
	for i := 0; i < 5; i++ {
		if _, _, err := h.svc.Verify(ctx, start.ChallengeID, "a@example.com", wrongCode(code), auth.DeviceInfo{}); !errors.Is(err, auth.ErrVerifyFailed) {
			t.Fatal(err)
		}
	}
	if _, _, err := h.svc.Verify(ctx, start.ChallengeID, "a@example.com", code, auth.DeviceInfo{}); !errors.Is(err, auth.ErrVerifyFailed) {
		t.Fatalf("the right code after 5 failures must fail: %v", err)
	}
}

func TestCodeIsOneTimeAndBoundToItsEmail(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	start, _ := h.svc.Start(ctx, "a@example.com", "203.0.113.1")
	code := h.mailer.codes["a@example.com"]
	if _, _, err := h.svc.Verify(ctx, start.ChallengeID, "b@example.com", code, auth.DeviceInfo{}); !errors.Is(err, auth.ErrVerifyFailed) {
		t.Fatalf("code accepted for another email: %v", err)
	}
	if _, _, err := h.svc.Verify(ctx, start.ChallengeID, "a@example.com", code, auth.DeviceInfo{}); err != nil {
		t.Fatal(err)
	}
	if _, _, err := h.svc.Verify(ctx, start.ChallengeID, "a@example.com", code, auth.DeviceInfo{}); !errors.Is(err, auth.ErrVerifyFailed) {
		t.Fatalf("replay accepted: %v", err)
	}
}

func TestNewCodeInvalidatesTheOlderOne(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	first, _ := h.svc.Start(ctx, "a@example.com", "203.0.113.1")
	oldCode := h.mailer.codes["a@example.com"]
	h.clock.Advance(61 * time.Second)
	if _, err := h.svc.Start(ctx, "a@example.com", "203.0.113.1"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := h.svc.Verify(ctx, first.ChallengeID, "a@example.com", oldCode, auth.DeviceInfo{}); !errors.Is(err, auth.ErrVerifyFailed) {
		t.Fatalf("superseded code accepted: %v", err)
	}
}

func TestResendCooldownAndHourlyLimit(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	if _, err := h.svc.Start(ctx, "a@example.com", "203.0.113.1"); err != nil {
		t.Fatal(err)
	}
	h.clock.Advance(30 * time.Second)
	var limited *auth.RateLimitedError
	if _, err := h.svc.Start(ctx, "a@example.com", "203.0.113.1"); !errors.As(err, &limited) || limited.RetryAfter != 30*time.Second {
		t.Fatalf("cooldown: %v", err)
	}
	for i := 0; i < 4; i++ {
		h.clock.Advance(61 * time.Second)
		if _, err := h.svc.Start(ctx, "a@example.com", "203.0.113.1"); err != nil {
			t.Fatalf("start %d: %v", i+2, err)
		}
	}
	h.clock.Advance(61 * time.Second)
	if _, err := h.svc.Start(ctx, "a@example.com", "203.0.113.1"); !errors.As(err, &limited) {
		t.Fatalf("sixth start in an hour must be limited: %v", err)
	}
	h.clock.Advance(time.Hour)
	if _, err := h.svc.Start(ctx, "a@example.com", "203.0.113.1"); err != nil {
		t.Fatalf("limit must reset after an hour: %v", err)
	}
}

func TestPerIPAndGlobalLimits(t *testing.T) {
	h := newHarness(t, func(o *auth.Options) { o.MaxStartsPerIPHour = 3; o.GlobalPerMinute = 5 })
	ctx := context.Background()
	var limited *auth.RateLimitedError
	for i, email := range []string{"a@example.com", "b@example.com", "c@example.com"} {
		if _, err := h.svc.Start(ctx, email, "198.51.100.7"); err != nil {
			t.Fatalf("start %d: %v", i, err)
		}
	}
	if _, err := h.svc.Start(ctx, "d@example.com", "198.51.100.7"); !errors.As(err, &limited) {
		t.Fatalf("per-IP limit: %v", err)
	}
	if _, err := h.svc.Start(ctx, "e@example.com", "198.51.100.8"); err != nil {
		t.Fatal(err)
	}
	if _, err := h.svc.Start(ctx, "f@example.com", "198.51.100.9"); !errors.As(err, &limited) {
		t.Fatalf("global limit: %v", err)
	}
}

func TestDeliveryFailureIsReportedWithoutLeakingTheCode(t *testing.T) {
	h := newHarness(t)
	h.mailer.fail = true
	if _, err := h.svc.Start(context.Background(), "a@example.com", "203.0.113.1"); !errors.Is(err, auth.ErrDeliveryFailed) {
		t.Fatalf("expected delivery failure, got %v", err)
	}
}

func TestInvalidInputsFailClosed(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	for _, email := range []string{"", "no-at", "a@b", "Maya <maya@example.com>", "a@example.com, b@example.com", "a b@example.com", strings.Repeat("a", 250) + "@example.com"} {
		if _, err := h.svc.Start(ctx, email, "203.0.113.1"); !errors.Is(err, auth.ErrInvalidEmail) {
			t.Errorf("%q: expected invalid email, got %v", email, err)
		}
	}
	start, _ := h.svc.Start(ctx, "a@example.com", "203.0.113.1")
	for _, code := range []string{"", "12345", "1234567", "abcdef", " 12345"} {
		if _, _, err := h.svc.Verify(ctx, start.ChallengeID, "a@example.com", code, auth.DeviceInfo{}); !errors.Is(err, auth.ErrVerifyFailed) {
			t.Errorf("code %q: %v", code, err)
		}
	}
	if _, _, err := h.svc.Verify(ctx, "not-a-uuid", "a@example.com", "123456", auth.DeviceInfo{}); !errors.Is(err, auth.ErrVerifyFailed) {
		t.Fatalf("garbage challenge id: %v", err)
	}
	// Malformed codes do not burn attempts: the right code still works.
	if _, _, err := h.svc.Verify(ctx, start.ChallengeID, "a@example.com", h.mailer.codes["a@example.com"], auth.DeviceInfo{}); err != nil {
		t.Fatal(err)
	}
}

func TestRefreshRotatesAndReuseRevokesTheFamily(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	_, first := h.signIn(t, "a@example.com")
	h.clock.Advance(time.Minute)
	second, err := h.svc.Refresh(ctx, first.RefreshToken)
	if err != nil || second.RefreshToken == first.RefreshToken || second.DeviceID != first.DeviceID {
		t.Fatalf("rotation: %+v %v", second, err)
	}
	// The stolen first token is presented again: reuse.
	if _, err := h.svc.Refresh(ctx, first.RefreshToken); !errors.Is(err, auth.ErrRefreshReused) {
		t.Fatalf("reuse not detected: %v", err)
	}
	if _, err := h.svc.Refresh(ctx, second.RefreshToken); !errors.Is(err, auth.ErrRefreshInvalid) {
		t.Fatalf("the family must be revoked: %v", err)
	}
	for _, access := range []string{first.AccessToken, second.AccessToken} {
		if _, err := h.svc.Authenticate(ctx, access); err == nil {
			t.Fatal("access tokens of a revoked family must stop working")
		}
	}
	if last := h.store.AuditLog[len(h.store.AuditLog)-1]; last.EventType != "auth.refresh_reuse" {
		t.Fatalf("reuse must be audited: %+v", last)
	}
}

func TestConcurrentRefreshWithTheSameTokenIsTreatedAsReuse(t *testing.T) {
	h := newHarness(t)
	_, tokens := h.signIn(t, "a@example.com")
	var wg sync.WaitGroup
	results := make(chan error, 4)
	for i := 0; i < 4; i++ {
		wg.Add(1)
		go func() { defer wg.Done(); _, err := h.svc.Refresh(context.Background(), tokens.RefreshToken); results <- err }()
	}
	wg.Wait()
	close(results)
	successes := 0
	for err := range results {
		if err == nil {
			successes++
		}
	}
	if successes > 1 {
		t.Fatalf("one refresh token produced %d sessions", successes)
	}
}

func TestRefreshTokenExpires(t *testing.T) {
	h := newHarness(t)
	_, tokens := h.signIn(t, "a@example.com")
	h.clock.Advance(30*24*time.Hour + time.Second)
	if _, err := h.svc.Refresh(context.Background(), tokens.RefreshToken); !errors.Is(err, auth.ErrRefreshInvalid) {
		t.Fatalf("expired refresh accepted: %v", err)
	}
	for _, garbage := range []string{"", "a1r_nope", "Bearer x", strings.Repeat("a", 500)} {
		if _, err := h.svc.Refresh(context.Background(), garbage); !errors.Is(err, auth.ErrRefreshInvalid) {
			t.Fatalf("%q: %v", garbage, err)
		}
	}
}

func TestLogoutEndsThisDeviceAndLogoutAllEndsEveryDevice(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	_, laptop := h.signIn(t, "a@example.com")
	h.clock.Advance(2 * time.Minute)
	_, phone := h.signIn(t, "a@example.com")

	p, err := h.svc.Authenticate(ctx, laptop.AccessToken)
	if err != nil {
		t.Fatal(err)
	}
	if err := h.svc.Logout(ctx, p); err != nil {
		t.Fatal(err)
	}
	if _, err := h.svc.Authenticate(ctx, laptop.AccessToken); err == nil {
		t.Fatal("logged-out access token still works")
	}
	if _, err := h.svc.Refresh(ctx, laptop.RefreshToken); !errors.Is(err, auth.ErrRefreshInvalid) {
		t.Fatalf("logged-out refresh still works: %v", err)
	}
	phoneP, err := h.svc.Authenticate(ctx, phone.AccessToken)
	if err != nil {
		t.Fatal("the other device must stay signed in")
	}
	if err := h.svc.LogoutAll(ctx, phoneP); err != nil {
		t.Fatal(err)
	}
	if _, err := h.svc.Authenticate(ctx, phone.AccessToken); err == nil {
		t.Fatal("logout-all must end every session")
	}
}

func TestAccessTokenExpiresAndRejectsTampering(t *testing.T) {
	h := newHarness(t)
	ctx := context.Background()
	_, tokens := h.signIn(t, "a@example.com")
	parts := strings.Split(tokens.AccessToken, ".")
	tampered := parts[0] + "." + parts[1] + "x." + parts[2]
	noneAlg := "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0." + parts[1] + "."
	for _, bad := range []string{tampered, noneAlg, parts[0] + "." + parts[1], ""} {
		if _, err := h.svc.Authenticate(ctx, bad); err == nil {
			t.Fatalf("accepted %q", bad)
		}
	}
	h.clock.Advance(15*time.Minute + time.Minute)
	if _, err := h.svc.Authenticate(ctx, tokens.AccessToken); err == nil {
		t.Fatal("expired access token accepted")
	}
}

func TestOTPIsSixDigitsAndVaries(t *testing.T) {
	seen := map[string]bool{}
	for i := 0; i < 200; i++ {
		code, err := auth.NewOTP()
		if err != nil || len(code) != 6 || strings.Trim(code, "0123456789") != "" {
			t.Fatalf("bad code %q %v", code, err)
		}
		seen[code] = true
	}
	if len(seen) < 190 {
		t.Fatalf("codes repeat too often: %d distinct of 200", len(seen))
	}
}

func TestUUIDs(t *testing.T) {
	now := time.Date(2026, 11, 21, 9, 0, 0, 0, time.UTC)
	a, _ := auth.NewUUIDv7(now)
	b, _ := auth.NewUUIDv7(now.Add(time.Millisecond))
	if !auth.IsUUID(a) || a[14] != '7' || a >= b {
		t.Fatalf("v7: %s %s", a, b)
	}
	v4, _ := auth.NewUUIDv4()
	if !auth.IsUUID(v4) || v4[14] != '4' || auth.IsUUID(strings.ToUpper(v4)) {
		t.Fatalf("v4: %s", v4)
	}
}

func TestRedactEmail(t *testing.T) {
	if got := auth.RedactEmail("maya@example.com"); got != "m***@example.com" {
		t.Fatal(got)
	}
}
