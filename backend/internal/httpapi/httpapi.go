// Package httpapi exposes the /v1 HTTP API (packages/api-contract/openapi.yaml). Errors are JSON with a stable code and
// the request id; request bodies, codes, and tokens are never logged.
package httpapi

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"mime"
	"net"
	"net/http"
	"regexp"
	"strconv"
	"strings"
	"time"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/tenancy"
)

const maxBodyBytes = 16 << 10

type Server struct {
	Auth              *auth.Service
	Tenancy           *tenancy.Service
	Ready             func(context.Context) error
	Log               *slog.Logger
	TrustProxyHeaders bool
}

func (s *Server) Handler() http.Handler {
	r := newRouter()
	r.handle("GET", "/v1/health/live", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
	}))
	r.handle("GET", "/v1/health/ready", http.HandlerFunc(s.ready))
	r.handle("POST", "/v1/auth/email/start", http.HandlerFunc(s.startEmail))
	r.handle("POST", "/v1/auth/email/verify", http.HandlerFunc(s.verifyEmail))
	r.handle("POST", "/v1/auth/refresh", http.HandlerFunc(s.refresh))
	r.handle("POST", "/v1/auth/logout", s.authenticated(s.logout))
	r.handle("POST", "/v1/auth/logout-all", s.authenticated(s.logoutAll))
	r.handle("GET", "/v1/me", s.authenticated(s.me))
	s.tenancyRoutes(r)
	r.mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		writeError(w, r, http.StatusNotFound, "NOT_FOUND", "No such endpoint.")
	})
	return s.middleware(r.mux)
}

// router registers "METHOD /path" routes and, for every path, a method-less fallback that answers other methods with
// a JSON 405 (the standard mux would answer in plain text).
type router struct {
	mux     *http.ServeMux
	methods map[string][]string
}

func newRouter() *router { return &router{mux: http.NewServeMux(), methods: map[string][]string{}} }

func (r *router) handle(method, path string, handler http.Handler) {
	r.mux.Handle(method+" "+path, handler)
	if _, seen := r.methods[path]; !seen {
		r.mux.HandleFunc(path, func(w http.ResponseWriter, req *http.Request) {
			w.Header().Set("Allow", strings.Join(r.methods[path], ", "))
			writeError(w, req, http.StatusMethodNotAllowed, "METHOD_NOT_ALLOWED", "This endpoint does not support "+req.Method+".")
		})
	}
	r.methods[path] = append(r.methods[path], method)
}

// ---------- middleware ----------

type ctxKey int

const (
	requestIDKey ctxKey = iota
	principalKey
)

var safeRequestID = regexp.MustCompile(`^[A-Za-z0-9._-]{1,64}$`)

type statusRecorder struct {
	http.ResponseWriter
	status int
}

func (r *statusRecorder) WriteHeader(status int) {
	r.status = status
	r.ResponseWriter.WriteHeader(status)
}

func (s *Server) middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		started := time.Now()
		requestID := r.Header.Get("X-Request-ID")
		if !safeRequestID.MatchString(requestID) {
			requestID = newRequestID()
		}
		w.Header().Set("X-Request-ID", requestID)
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Referrer-Policy", "no-referrer")
		r = r.WithContext(context.WithValue(r.Context(), requestIDKey, requestID))
		recorder := &statusRecorder{ResponseWriter: w, status: http.StatusOK}
		defer func() {
			if failure := recover(); failure != nil {
				s.log().Error("panic", "request_id", requestID, "panic", fmt.Sprint(failure))
				writeError(recorder, r, http.StatusInternalServerError, "INTERNAL", "Something went wrong.")
			}
			// Log the route pattern, never the raw path: paths can carry tokens (invite acceptance) and ids.
			route := r.Pattern
			if route == "" || route == "/" {
				route = "unmatched"
			}
			s.log().Info("request", "request_id", requestID, "method", r.Method, "route", route, "status", recorder.status,
				"duration_ms", time.Since(started).Milliseconds())
		}()
		next.ServeHTTP(recorder, r)
	})
}

func (s *Server) authenticated(next func(http.ResponseWriter, *http.Request, auth.Principal)) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		header := r.Header.Get("Authorization")
		token, ok := strings.CutPrefix(header, "Bearer ")
		if !ok || token == "" {
			w.Header().Set("WWW-Authenticate", `Bearer realm="alondra1"`)
			writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED", "Sign in again.")
			return
		}
		principal, err := s.Auth.Authenticate(r.Context(), token)
		if err != nil {
			if !errors.Is(err, auth.ErrInvalidToken) && !errors.Is(err, auth.ErrSessionInactive) {
				s.internal(w, r, err)
				return
			}
			w.Header().Set("WWW-Authenticate", `Bearer realm="alondra1", error="invalid_token"`)
			writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED", "Sign in again.")
			return
		}
		next(w, r.WithContext(context.WithValue(r.Context(), principalKey, principal)), principal)
	})
}

// ---------- handlers ----------

func (s *Server) ready(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
	defer cancel()
	if s.Ready != nil {
		if err := s.Ready(ctx); err != nil {
			s.log().Warn("not ready", "error", err.Error())
			writeError(w, r, http.StatusServiceUnavailable, "NOT_READY", "The database is not reachable.")
			return
		}
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "ready"})
}

type startRequest struct {
	Email string `json:"email"`
}

func (s *Server) startEmail(w http.ResponseWriter, r *http.Request) {
	var body startRequest
	if !decode(w, r, &body) {
		return
	}
	result, err := s.Auth.Start(r.Context(), body.Email, s.clientIP(r))
	if err != nil {
		s.authError(w, r, err)
		return
	}
	writeJSON(w, http.StatusAccepted, map[string]any{
		"challenge_id":         result.ChallengeID,
		"expires_in_seconds":   int(result.ExpiresIn / time.Second),
		"resend_after_seconds": int(result.ResendAfter / time.Second),
	})
}

type verifyRequest struct {
	Email       string `json:"email"`
	Code        string `json:"code"`
	ChallengeID string `json:"challenge_id"`
	Device      struct {
		Name     string `json:"name"`
		Platform string `json:"platform"`
	} `json:"device"`
}

func (s *Server) verifyEmail(w http.ResponseWriter, r *http.Request) {
	var body verifyRequest
	if !decode(w, r, &body) {
		return
	}
	user, tokens, err := s.Auth.Verify(r.Context(), body.ChallengeID, body.Email, body.Code, auth.DeviceInfo{Name: body.Device.Name, Platform: body.Device.Platform})
	if err != nil {
		s.authError(w, r, err)
		return
	}
	response := tokenResponse(tokens)
	response["user"] = map[string]any{"id": user.ID, "email": user.EmailNormalized}
	writeJSON(w, http.StatusOK, response)
}

type refreshRequest struct {
	RefreshToken string `json:"refresh_token"`
}

func (s *Server) refresh(w http.ResponseWriter, r *http.Request) {
	var body refreshRequest
	if !decode(w, r, &body) {
		return
	}
	tokens, err := s.Auth.Refresh(r.Context(), body.RefreshToken)
	if err != nil {
		s.authError(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, tokenResponse(tokens))
}

func (s *Server) logout(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	if err := s.Auth.Logout(r.Context(), p); err != nil {
		s.internal(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) logoutAll(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	if err := s.Auth.LogoutAll(r.Context(), p); err != nil {
		s.internal(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) me(w http.ResponseWriter, r *http.Request, p auth.Principal) {
	user, err := s.Auth.User(r.Context(), p.UserID)
	if errors.Is(err, auth.ErrNotFound) {
		writeError(w, r, http.StatusUnauthorized, "UNAUTHENTICATED", "Sign in again.")
		return
	}
	if err != nil {
		s.internal(w, r, err)
		return
	}
	// A new account belongs to no space until the user creates one or accepts an invitation.
	memberships := []map[string]any{}
	if s.Tenancy != nil {
		views, err := s.Tenancy.List(r.Context(), p.UserID)
		if err != nil {
			s.internal(w, r, err)
			return
		}
		for _, v := range views {
			memberships = append(memberships, tenantJSON(v))
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"id": user.ID, "email": user.EmailNormalized, "created_at": user.CreatedAt.UTC().Format(time.RFC3339),
		"memberships": memberships,
	})
}

func tokenResponse(t auth.Tokens) map[string]any {
	return map[string]any{
		"token_type":               "Bearer",
		"access_token":             t.AccessToken,
		"access_token_expires_at":  t.AccessExpiresAt.UTC().Format(time.RFC3339),
		"refresh_token":            t.RefreshToken,
		"refresh_token_expires_at": t.RefreshExpiresAt.UTC().Format(time.RFC3339),
		"session":                  map[string]any{"id": t.SessionID, "device_id": t.DeviceID},
	}
}

func (s *Server) authError(w http.ResponseWriter, r *http.Request, err error) {
	var limited *auth.RateLimitedError
	switch {
	case errors.As(err, &limited):
		seconds := int(limited.RetryAfter.Round(time.Second) / time.Second)
		if seconds < 1 {
			seconds = 1
		}
		w.Header().Set("Retry-After", strconv.Itoa(seconds))
		writeError(w, r, http.StatusTooManyRequests, "RATE_LIMITED", "Too many requests. Try again later.")
	case errors.Is(err, auth.ErrInvalidEmail):
		writeError(w, r, http.StatusUnprocessableEntity, "INVALID_EMAIL", "Enter a valid email address.")
	case errors.Is(err, auth.ErrVerifyFailed):
		writeError(w, r, http.StatusUnauthorized, "INVALID_CODE", "The code is wrong, expired, or already used. Request a new code.")
	case errors.Is(err, auth.ErrRefreshReused):
		writeError(w, r, http.StatusUnauthorized, "REFRESH_REUSED", "This sign-in was ended for safety. Sign in again.")
	case errors.Is(err, auth.ErrRefreshInvalid):
		writeError(w, r, http.StatusUnauthorized, "REFRESH_INVALID", "Sign in again.")
	case errors.Is(err, auth.ErrDeliveryFailed):
		writeError(w, r, http.StatusServiceUnavailable, "EMAIL_DELIVERY_FAILED", "The sign-in email could not be sent. Try again in a few minutes.")
	default:
		s.internal(w, r, err)
	}
}

func (s *Server) internal(w http.ResponseWriter, r *http.Request, err error) {
	s.log().Error("internal error", "request_id", requestID(r), "error", err.Error())
	writeError(w, r, http.StatusInternalServerError, "INTERNAL", "Something went wrong.")
}

func (s *Server) log() *slog.Logger {
	if s.Log != nil {
		return s.Log
	}
	return slog.Default()
}

// clientIP is the peer address; X-Forwarded-For is honored only behind a trusted reverse proxy.
func (s *Server) clientIP(r *http.Request) string {
	if s.TrustProxyHeaders {
		if forwarded := r.Header.Get("X-Forwarded-For"); forwarded != "" {
			first := strings.TrimSpace(strings.Split(forwarded, ",")[0])
			if net.ParseIP(first) != nil {
				return first
			}
		}
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// ---------- JSON ----------

func decode(w http.ResponseWriter, r *http.Request, target any) bool {
	if mediaType, _, err := mime.ParseMediaType(r.Header.Get("Content-Type")); err != nil || mediaType != "application/json" {
		writeError(w, r, http.StatusUnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", "Send application/json.")
		return false
	}
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxBodyBytes))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		var tooLarge *http.MaxBytesError
		if errors.As(err, &tooLarge) {
			writeError(w, r, http.StatusRequestEntityTooLarge, "BODY_TOO_LARGE", "The request body is too large.")
			return false
		}
		writeError(w, r, http.StatusBadRequest, "MALFORMED_JSON", "The request body is not valid JSON for this endpoint.")
		return false
	}
	if err := decoder.Decode(&struct{}{}); err != io.EOF {
		writeError(w, r, http.StatusBadRequest, "MALFORMED_JSON", "Send exactly one JSON object.")
		return false
	}
	return true
}

func writeJSON(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}

func writeError(w http.ResponseWriter, r *http.Request, status int, code, message string) {
	writeJSON(w, status, map[string]any{"error": map[string]string{"code": code, "message": message, "request_id": requestID(r)}})
}

func requestID(r *http.Request) string {
	if id, ok := r.Context().Value(requestIDKey).(string); ok {
		return id
	}
	return ""
}

func newRequestID() string {
	b := make([]byte, 12)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}
