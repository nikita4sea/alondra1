package httpapi_test

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/httpapi"
)

type mailbox struct {
	mu   sync.Mutex
	last map[string]string
}

func (m *mailbox) SendSignInCode(_ context.Context, to, code string, _ time.Duration) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.last == nil {
		m.last = map[string]string{}
	}
	m.last[to] = code
	return nil
}

type fixture struct {
	server *httptest.Server
	mail   *mailbox
	logs   *bytes.Buffer
	now    time.Time
	ready  error
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	f := &fixture{mail: &mailbox{}, logs: &bytes.Buffer{}, now: time.Date(2026, 11, 21, 9, 0, 0, 0, time.UTC)}
	logger := slog.New(slog.NewJSONHandler(f.logs, &slog.HandlerOptions{Level: slog.LevelDebug}))
	signer, _ := auth.NewTokenSigner([]byte(strings.Repeat("k", 48)))
	svc := auth.NewService(auth.NewMemoryStore(), f.mail, signer, []byte(strings.Repeat("o", 48)), func() time.Time { return f.now }, auth.DefaultOptions(), logger)
	api := &httpapi.Server{Auth: svc, Log: logger, Ready: func(context.Context) error { return f.ready }}
	f.server = httptest.NewServer(api.Handler())
	t.Cleanup(f.server.Close)
	return f
}

type response struct {
	status int
	header http.Header
	body   map[string]any
}

func (f *fixture) do(t *testing.T, method, path, token string, body any, headers ...string) response {
	t.Helper()
	var reader *bytes.Reader
	switch value := body.(type) {
	case nil:
		reader = bytes.NewReader(nil)
	case string:
		reader = bytes.NewReader([]byte(value))
	default:
		raw, _ := json.Marshal(value)
		reader = bytes.NewReader(raw)
	}
	req, _ := http.NewRequest(method, f.server.URL+path, reader)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	for i := 0; i+1 < len(headers); i += 2 {
		req.Header.Set(headers[i], headers[i+1])
	}
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	out := response{status: res.StatusCode, header: res.Header}
	_ = json.NewDecoder(res.Body).Decode(&out.body)
	return out
}

func errorCode(r response) string {
	if e, ok := r.body["error"].(map[string]any); ok {
		return e["code"].(string)
	}
	return ""
}

func TestSignInFlowOverHTTP(t *testing.T) {
	f := newFixture(t)
	start := f.do(t, "POST", "/v1/auth/email/start", "", map[string]string{"email": "Maya@Example.com"})
	if start.status != http.StatusAccepted || start.body["expires_in_seconds"].(float64) != 600 || start.body["resend_after_seconds"].(float64) != 60 {
		t.Fatalf("start: %d %v", start.status, start.body)
	}
	code := f.mail.last["maya@example.com"]
	verify := f.do(t, "POST", "/v1/auth/email/verify", "", map[string]any{
		"email": "maya@example.com", "code": code, "challenge_id": start.body["challenge_id"],
		"device": map[string]string{"name": "Maya's laptop", "platform": "windows"},
	})
	if verify.status != http.StatusOK || verify.body["token_type"] != "Bearer" {
		t.Fatalf("verify: %d %v", verify.status, verify.body)
	}
	access, refresh := verify.body["access_token"].(string), verify.body["refresh_token"].(string)

	me := f.do(t, "GET", "/v1/me", access, nil)
	if me.status != http.StatusOK || me.body["email"] != "maya@example.com" || len(me.body["memberships"].([]any)) != 0 {
		t.Fatalf("me: %d %v", me.status, me.body)
	}

	rotated := f.do(t, "POST", "/v1/auth/refresh", "", map[string]string{"refresh_token": refresh})
	if rotated.status != http.StatusOK || rotated.body["refresh_token"] == refresh {
		t.Fatalf("refresh: %d %v", rotated.status, rotated.body)
	}
	reuse := f.do(t, "POST", "/v1/auth/refresh", "", map[string]string{"refresh_token": refresh})
	if reuse.status != http.StatusUnauthorized || errorCode(reuse) != "REFRESH_REUSED" {
		t.Fatalf("reuse: %d %v", reuse.status, reuse.body)
	}
	if me := f.do(t, "GET", "/v1/me", rotated.body["access_token"].(string), nil); me.status != http.StatusUnauthorized {
		t.Fatalf("after reuse the family must be signed out: %d", me.status)
	}

	// Nothing secret reaches the logs.
	logs := f.logs.String()
	for _, secret := range []string{code, access, refresh, "maya@example.com"} {
		if strings.Contains(logs, secret) {
			t.Fatalf("logs contain a secret or address: %q", secret)
		}
	}
}

func TestLogoutOverHTTP(t *testing.T) {
	f := newFixture(t)
	start := f.do(t, "POST", "/v1/auth/email/start", "", map[string]string{"email": "a@example.com"})
	verify := f.do(t, "POST", "/v1/auth/email/verify", "", map[string]any{"email": "a@example.com", "code": f.mail.last["a@example.com"], "challenge_id": start.body["challenge_id"]})
	access := verify.body["access_token"].(string)
	if r := f.do(t, "POST", "/v1/auth/logout", access, nil); r.status != http.StatusNoContent {
		t.Fatalf("logout: %d %v", r.status, r.body)
	}
	if r := f.do(t, "GET", "/v1/me", access, nil); r.status != http.StatusUnauthorized || errorCode(r) != "UNAUTHENTICATED" || r.header.Get("WWW-Authenticate") == "" {
		t.Fatalf("me after logout: %d %v", r.status, r.body)
	}
}

func TestErrorContract(t *testing.T) {
	f := newFixture(t)
	cases := []struct {
		name, method, path string
		body               any
		headers            []string
		status             int
		code               string
	}{
		{"invalid email", "POST", "/v1/auth/email/start", map[string]string{"email": "nope"}, nil, 422, "INVALID_EMAIL"},
		{"unknown field", "POST", "/v1/auth/email/start", map[string]string{"email": "a@example.com", "password": "x"}, nil, 400, "MALFORMED_JSON"},
		{"two objects", "POST", "/v1/auth/email/start", `{"email":"a@example.com"}{"email":"b@example.com"}`, nil, 400, "MALFORMED_JSON"},
		{"not json", "POST", "/v1/auth/email/start", "email=a@example.com", []string{"Content-Type", "application/x-www-form-urlencoded"}, 415, "UNSUPPORTED_MEDIA_TYPE"},
		{"too large", "POST", "/v1/auth/email/start", `{"email":"` + strings.Repeat("a", 20000) + `"}`, nil, 413, "BODY_TOO_LARGE"},
		{"wrong code", "POST", "/v1/auth/email/verify", map[string]string{"email": "a@example.com", "code": "123456", "challenge_id": "00000000-0000-4000-8000-000000000000"}, nil, 401, "INVALID_CODE"},
		{"bad refresh", "POST", "/v1/auth/refresh", map[string]string{"refresh_token": "a1r_nope"}, nil, 401, "REFRESH_INVALID"},
		{"no token", "GET", "/v1/me", nil, nil, 401, "UNAUTHENTICATED"},
		{"garbage token", "GET", "/v1/me", nil, []string{"Authorization", "Bearer abc.def.ghi"}, 401, "UNAUTHENTICATED"},
		{"unknown path", "GET", "/v1/nothing", nil, nil, 404, "NOT_FOUND"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := f.do(t, c.method, c.path, "", c.body, c.headers...)
			if r.status != c.status || errorCode(r) != c.code {
				t.Fatalf("got %d %v", r.status, r.body)
			}
			e := r.body["error"].(map[string]any)
			if e["request_id"] == "" || e["request_id"] != r.header.Get("X-Request-ID") || e["message"] == "" {
				t.Fatalf("error body must carry the request id and a message: %v", e)
			}
			if r.header.Get("Cache-Control") != "no-store" {
				t.Fatal("responses must not be cached")
			}
		})
	}
}

func TestRateLimitSetsRetryAfter(t *testing.T) {
	f := newFixture(t)
	f.do(t, "POST", "/v1/auth/email/start", "", map[string]string{"email": "a@example.com"})
	r := f.do(t, "POST", "/v1/auth/email/start", "", map[string]string{"email": "a@example.com"})
	if r.status != http.StatusTooManyRequests || errorCode(r) != "RATE_LIMITED" || r.header.Get("Retry-After") != "60" {
		t.Fatalf("got %d %v Retry-After=%q", r.status, r.body, r.header.Get("Retry-After"))
	}
}

func TestRequestIDIsEchoedOnlyWhenSafe(t *testing.T) {
	f := newFixture(t)
	if r := f.do(t, "GET", "/v1/health/live", "", nil, "X-Request-ID", "client-123"); r.header.Get("X-Request-ID") != "client-123" {
		t.Fatalf("safe id not echoed: %q", r.header.Get("X-Request-ID"))
	}
	if r := f.do(t, "GET", "/v1/health/live", "", nil, "X-Request-ID", "bad id\twith<html>"); r.header.Get("X-Request-ID") == "bad id\twith<html>" {
		t.Fatal("unsafe id echoed")
	}
}

func TestHealth(t *testing.T) {
	f := newFixture(t)
	if r := f.do(t, "GET", "/v1/health/live", "", nil); r.status != 200 || r.body["status"] != "ok" {
		t.Fatalf("live: %d %v", r.status, r.body)
	}
	if r := f.do(t, "GET", "/v1/health/ready", "", nil); r.status != 200 {
		t.Fatalf("ready: %d %v", r.status, r.body)
	}
	f.ready = errors.New("db down")
	if r := f.do(t, "GET", "/v1/health/ready", "", nil); r.status != 503 || errorCode(r) != "NOT_READY" {
		t.Fatalf("not ready: %d %v", r.status, r.body)
	}
}
