// Package auth implements Alondra1 cloud sign-in: emailed one-time codes, short-lived access tokens, and rotating
// refresh tokens bound to a device session (docs/BACKEND_SPEC.md §4). There are no passwords.
package auth

import (
	"context"
	"crypto/hmac"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"time"
	"unicode/utf8"
)

// Errors returned to the HTTP layer. Verification failures are deliberately indistinguishable.
var (
	ErrVerifyFailed    = errors.New("the code is wrong, expired, or already used")
	ErrRefreshInvalid  = errors.New("refresh token is invalid or expired")
	ErrRefreshReused   = errors.New("refresh token was reused; the session family is revoked")
	ErrDeliveryFailed  = errors.New("the sign-in email could not be sent")
	ErrSessionInactive = errors.New("session is no longer active")
)

// RateLimitedError tells the client when to try again.
type RateLimitedError struct{ RetryAfter time.Duration }

func (e *RateLimitedError) Error() string {
	return fmt.Sprintf("too many requests; retry after %s", e.RetryAfter.Round(time.Second))
}

// Mailer delivers sign-in codes. Implementations must never log the code.
type Mailer interface {
	SendSignInCode(ctx context.Context, to, code string, validFor time.Duration) error
}

type Options struct {
	AccessTTL          time.Duration
	RefreshTTL         time.Duration
	OTPTTL             time.Duration
	MaxAttempts        int
	ResendCooldown     time.Duration
	MaxStartsPerHour   int
	MaxStartsPerIPHour int
	GlobalPerMinute    int
}

// DefaultOptions are the values required by the spec (§4).
func DefaultOptions() Options {
	return Options{
		AccessTTL:          15 * time.Minute,
		RefreshTTL:         30 * 24 * time.Hour,
		OTPTTL:             10 * time.Minute,
		MaxAttempts:        5,
		ResendCooldown:     60 * time.Second,
		MaxStartsPerHour:   5,
		MaxStartsPerIPHour: 30,
		GlobalPerMinute:    300,
	}
}

type Service struct {
	store   Store
	mailer  Mailer
	signer  *TokenSigner
	otpKey  []byte
	now     func() time.Time
	opts    Options
	log     *slog.Logger
	limiter *windowLimiter
}

func NewService(store Store, mailer Mailer, signer *TokenSigner, otpKey []byte, now func() time.Time, opts Options, log *slog.Logger) *Service {
	if log == nil {
		log = slog.Default()
	}
	return &Service{
		store: store, mailer: mailer, signer: signer, otpKey: append([]byte(nil), otpKey...), now: now, opts: opts, log: log,
		limiter: &windowLimiter{window: time.Minute, limit: opts.GlobalPerMinute},
	}
}

type StartResult struct {
	ChallengeID string
	ExpiresIn   time.Duration
	ResendAfter time.Duration
}

// Start sends a sign-in code to the address. The result is the same whether or not an account exists.
func (s *Service) Start(ctx context.Context, rawEmail, clientIP string) (StartResult, error) {
	email, err := NormalizeEmail(rawEmail)
	if err != nil {
		return StartResult{}, err
	}
	now := s.now().UTC()
	if !s.limiter.allow(now) {
		return StartResult{}, &RateLimitedError{RetryAfter: time.Minute}
	}
	ipHash := IPHash(s.otpKey, clientIP)
	byIP, err := s.store.RecentChallengesByIP(ctx, ipHash, now.Add(-time.Hour))
	if err != nil {
		return StartResult{}, err
	}
	if byIP >= s.opts.MaxStartsPerIPHour {
		return StartResult{}, &RateLimitedError{RetryAfter: 15 * time.Minute}
	}
	count, latest, err := s.store.RecentChallenges(ctx, email, now.Add(-time.Hour))
	if err != nil {
		return StartResult{}, err
	}
	if count > 0 && now.Sub(latest) < s.opts.ResendCooldown {
		return StartResult{}, &RateLimitedError{RetryAfter: s.opts.ResendCooldown - now.Sub(latest)}
	}
	if count >= s.opts.MaxStartsPerHour {
		return StartResult{}, &RateLimitedError{RetryAfter: 15 * time.Minute}
	}

	id, err := NewUUIDv4()
	if err != nil {
		return StartResult{}, err
	}
	code, err := NewOTP()
	if err != nil {
		return StartResult{}, err
	}
	challenge := Challenge{
		ID: id, EmailNormalized: email, OTPHMAC: OTPMAC(s.otpKey, id, email, code),
		ExpiresAt: now.Add(s.opts.OTPTTL), CreatedAt: now, RequestIPHash: ipHash,
	}
	if err := s.store.CreateChallenge(ctx, challenge); err != nil {
		return StartResult{}, err
	}
	if err := s.mailer.SendSignInCode(ctx, email, code, s.opts.OTPTTL); err != nil {
		s.log.Warn("sign-in email not sent", "to", RedactEmail(email), "error", err.Error())
		return StartResult{}, ErrDeliveryFailed
	}
	return StartResult{ChallengeID: id, ExpiresIn: s.opts.OTPTTL, ResendAfter: s.opts.ResendCooldown}, nil
}

// DeviceInfo describes the signing-in device; it is display metadata only.
type DeviceInfo struct {
	Name     string
	Platform string
}

type Tokens struct {
	AccessToken      string
	AccessExpiresAt  time.Time
	RefreshToken     string
	RefreshExpiresAt time.Time
	SessionID        string
	DeviceID         string
}

// Verify checks a code and signs the user in, creating the account on first sign-in. No tenant is joined.
func (s *Service) Verify(ctx context.Context, challengeID, rawEmail, code string, device DeviceInfo) (User, Tokens, error) {
	email, err := NormalizeEmail(rawEmail)
	if err != nil || !IsUUID(challengeID) || !isSixDigits(code) {
		return User{}, Tokens{}, ErrVerifyFailed
	}
	now := s.now().UTC()
	challenge, err := s.store.GetChallenge(ctx, challengeID)
	if errors.Is(err, ErrNotFound) {
		return User{}, Tokens{}, ErrVerifyFailed
	}
	if err != nil {
		return User{}, Tokens{}, err
	}
	if challenge.ConsumedAt != nil || !now.Before(challenge.ExpiresAt) || challenge.AttemptCount >= s.opts.MaxAttempts {
		return User{}, Tokens{}, ErrVerifyFailed
	}
	expected := OTPMAC(s.otpKey, challenge.ID, challenge.EmailNormalized, code)
	// Both checks always run; hmac.Equal is constant-time.
	matches := hmac.Equal(expected, challenge.OTPHMAC)
	sameEmail := hmac.Equal([]byte(email), []byte(challenge.EmailNormalized))
	if !matches || !sameEmail {
		if _, err := s.store.RecordFailedAttempt(ctx, challenge.ID); err != nil && !errors.Is(err, ErrChallengeInvalid) {
			return User{}, Tokens{}, err
		}
		return User{}, Tokens{}, ErrVerifyFailed
	}

	userID, err := NewUUIDv7(now)
	if err != nil {
		return User{}, Tokens{}, err
	}
	user, err := s.store.ConsumeChallenge(ctx, challenge.ID, now, s.opts.MaxAttempts, userID)
	if errors.Is(err, ErrChallengeInvalid) {
		return User{}, Tokens{}, ErrVerifyFailed // lost a race with another verify, or it expired meanwhile
	}
	if err != nil {
		return User{}, Tokens{}, err
	}

	deviceID, err := NewUUIDv7(now)
	if err != nil {
		return User{}, Tokens{}, err
	}
	if err := s.store.CreateDevice(ctx, Device{ID: deviceID, UserID: user.ID, Name: cleanDeviceName(device.Name), Platform: cleanPlatform(device.Platform), SeenAt: now}); err != nil {
		return User{}, Tokens{}, err
	}
	familyID, err := NewUUIDv7(now)
	if err != nil {
		return User{}, Tokens{}, err
	}
	tokens, err := s.newSession(ctx, user.ID, deviceID, familyID, now, nil)
	if err != nil {
		return User{}, Tokens{}, err
	}
	s.audit(ctx, AuditEvent{ActorUserID: user.ID, EventType: "auth.sign_in", TargetID: tokens.SessionID, Metadata: map[string]string{"device_id": deviceID}, CreatedAt: now})
	return user, tokens, nil
}

// Refresh rotates a refresh token. Reusing an already rotated token revokes every session in its family.
func (s *Service) Refresh(ctx context.Context, refreshToken string) (Tokens, error) {
	if !strings.HasPrefix(refreshToken, RefreshTokenPrefix) || len(refreshToken) > 128 {
		return Tokens{}, ErrRefreshInvalid
	}
	now := s.now().UTC()
	session, err := s.store.GetSessionByRefreshHash(ctx, HashRefreshToken(refreshToken))
	if errors.Is(err, ErrNotFound) {
		return Tokens{}, ErrRefreshInvalid
	}
	if err != nil {
		return Tokens{}, err
	}
	if session.RevokedAt != nil {
		if session.RevokeReason == RevokeRotated {
			return Tokens{}, s.reuseDetected(ctx, session, now)
		}
		return Tokens{}, ErrRefreshInvalid
	}
	if !now.Before(session.ExpiresAt) {
		return Tokens{}, ErrRefreshInvalid
	}
	tokens, err := s.newSession(ctx, session.UserID, session.DeviceID, session.TokenFamilyID, now, &session.ID)
	if errors.Is(err, ErrSessionRotated) {
		return Tokens{}, s.reuseDetected(ctx, session, now)
	}
	if err != nil {
		return Tokens{}, err
	}
	if err := s.store.TouchDevice(ctx, session.DeviceID, now); err != nil {
		s.log.Warn("device last_seen not updated", "error", err.Error())
	}
	return tokens, nil
}

func (s *Service) reuseDetected(ctx context.Context, session Session, now time.Time) error {
	if err := s.store.RevokeFamily(ctx, session.TokenFamilyID, RevokeReuse, now); err != nil {
		return err
	}
	s.audit(ctx, AuditEvent{ActorUserID: session.UserID, EventType: "auth.refresh_reuse", TargetID: session.TokenFamilyID, CreatedAt: now})
	s.log.Warn("refresh token reuse; session family revoked", "user_id", session.UserID, "family_id", session.TokenFamilyID)
	return ErrRefreshReused
}

// newSession creates (or, when rotateFrom is set, rotates into) a session and issues both tokens.
func (s *Service) newSession(ctx context.Context, userID, deviceID, familyID string, now time.Time, rotateFrom *string) (Tokens, error) {
	sessionID, err := NewUUIDv7(now)
	if err != nil {
		return Tokens{}, err
	}
	refresh, hash, err := NewRefreshToken()
	if err != nil {
		return Tokens{}, err
	}
	session := Session{
		ID: sessionID, UserID: userID, DeviceID: deviceID, RefreshHash: hash, TokenFamilyID: familyID,
		ExpiresAt: now.Add(s.opts.RefreshTTL), CreatedAt: now, LastUsedAt: now,
	}
	if rotateFrom == nil {
		err = s.store.CreateSession(ctx, session)
	} else {
		err = s.store.RotateSession(ctx, *rotateFrom, session, now)
	}
	if err != nil {
		return Tokens{}, err
	}
	access, err := s.signer.Issue(userID, sessionID, now, s.opts.AccessTTL)
	if err != nil {
		return Tokens{}, err
	}
	return Tokens{
		AccessToken: access, AccessExpiresAt: now.Add(s.opts.AccessTTL),
		RefreshToken: refresh, RefreshExpiresAt: session.ExpiresAt,
		SessionID: sessionID, DeviceID: deviceID,
	}, nil
}

// Principal is the authenticated caller of a request.
type Principal struct {
	UserID    string
	SessionID string
}

// Authenticate checks an access token and that its session is still active, so logout takes effect immediately.
func (s *Service) Authenticate(ctx context.Context, accessToken string) (Principal, error) {
	now := s.now().UTC()
	claims, err := s.signer.Verify(accessToken, now)
	if err != nil {
		return Principal{}, ErrInvalidToken
	}
	session, err := s.store.GetSession(ctx, claims.SessionID)
	if errors.Is(err, ErrNotFound) {
		return Principal{}, ErrInvalidToken
	}
	if err != nil {
		return Principal{}, err
	}
	// A rotated session's access token stays valid until it expires (at most AccessTTL); any other revocation ends it now.
	if session.UserID != claims.Subject || !now.Before(session.ExpiresAt) || (session.RevokedAt != nil && session.RevokeReason != RevokeRotated) {
		return Principal{}, ErrSessionInactive
	}
	return Principal{UserID: claims.Subject, SessionID: claims.SessionID}, nil
}

// Logout revokes the caller's current session family member (this device's sign-in).
func (s *Service) Logout(ctx context.Context, p Principal) error {
	now := s.now().UTC()
	session, err := s.store.GetSession(ctx, p.SessionID)
	if err != nil {
		return err
	}
	if err := s.store.RevokeFamily(ctx, session.TokenFamilyID, RevokeLogout, now); err != nil {
		return err
	}
	s.audit(ctx, AuditEvent{ActorUserID: p.UserID, EventType: "auth.logout", TargetID: session.TokenFamilyID, CreatedAt: now})
	return nil
}

// LogoutAll revokes every session of the caller on every device.
func (s *Service) LogoutAll(ctx context.Context, p Principal) error {
	now := s.now().UTC()
	if err := s.store.RevokeAllForUser(ctx, p.UserID, RevokeLogoutAll, now); err != nil {
		return err
	}
	s.audit(ctx, AuditEvent{ActorUserID: p.UserID, EventType: "auth.logout_all", TargetID: p.UserID, CreatedAt: now})
	return nil
}

func (s *Service) User(ctx context.Context, id string) (User, error) { return s.store.GetUser(ctx, id) }

func (s *Service) audit(ctx context.Context, event AuditEvent) {
	if err := s.store.Audit(ctx, event); err != nil {
		s.log.Error("audit write failed", "event", event.EventType, "error", err.Error())
	}
}

func isSixDigits(code string) bool {
	if len(code) != 6 {
		return false
	}
	for _, c := range code {
		if c < '0' || c > '9' {
			return false
		}
	}
	return true
}

func cleanDeviceName(name string) string {
	name = strings.Map(func(r rune) rune {
		if r < 0x20 || r == 0x7f {
			return -1
		}
		return r
	}, strings.TrimSpace(name))
	if name == "" {
		return "Unnamed device"
	}
	for utf8.RuneCountInString(name) > 100 {
		_, size := utf8.DecodeLastRuneInString(name)
		name = name[:len(name)-size]
	}
	return name
}

func cleanPlatform(platform string) string {
	switch strings.ToLower(strings.TrimSpace(platform)) {
	case "windows":
		return "windows"
	case "android":
		return "android"
	case "ios":
		return "ios"
	}
	return "other"
}

// windowLimiter is a process-wide fixed-window limit on code requests, a last line of defense against mail flooding.
type windowLimiter struct {
	mu     sync.Mutex
	window time.Duration
	limit  int
	start  time.Time
	count  int
}

func (l *windowLimiter) allow(now time.Time) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	if now.Sub(l.start) >= l.window {
		l.start, l.count = now, 0
	}
	if l.count >= l.limit {
		return false
	}
	l.count++
	return true
}
