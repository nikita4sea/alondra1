package auth

import (
	"context"
	"errors"
	"time"
)

// Store errors. Implementations return these (possibly wrapped) so the service and HTTP layer can map them.
var (
	ErrNotFound         = errors.New("not found")
	ErrChallengeInvalid = errors.New("challenge expired, consumed, or locked")
	ErrSessionRotated   = errors.New("session was already rotated")
)

// Challenge is one emailed sign-in code. The code itself is never stored; only an HMAC bound to the challenge and email.
type Challenge struct {
	ID              string
	EmailNormalized string
	OTPHMAC         []byte
	ExpiresAt       time.Time
	AttemptCount    int
	ConsumedAt      *time.Time
	CreatedAt       time.Time
	RequestIPHash   []byte
}

type User struct {
	ID              string
	EmailNormalized string
	CreatedAt       time.Time
}

type Device struct {
	ID        string
	UserID    string
	Name      string
	Platform  string
	SeenAt    time.Time
	RevokedAt *time.Time
}

// Session is one device sign-in. Each refresh rotates it: the old row is revoked with reason "rotated" and a new row in
// the same family takes its place. Presenting a rotated token again is reuse and revokes the whole family.
type Session struct {
	ID            string
	UserID        string
	DeviceID      string
	RefreshHash   []byte
	TokenFamilyID string
	ExpiresAt     time.Time
	RevokedAt     *time.Time
	RevokeReason  string
	CreatedAt     time.Time
	LastUsedAt    time.Time
}

// Revoke reasons.
const (
	RevokeRotated   = "rotated"
	RevokeLogout    = "logout"
	RevokeLogoutAll = "logout_all"
	RevokeReuse     = "reuse_detected"
	RevokeDevice    = "device_revoked"
	RevokeDeleted   = "account_deleted"
)

type AuditEvent struct {
	TenantID    string // empty for account-level events
	ActorUserID string // empty when no user is known yet
	EventType   string
	TargetID    string
	Metadata    map[string]string // never secrets, codes, or tokens
	CreatedAt   time.Time
}

// Store persists authentication state. Every method is atomic.
type Store interface {
	// CreateChallenge marks every unconsumed challenge for the same email as consumed (a new code replaces older
	// ones) and inserts the new challenge, in one transaction.
	CreateChallenge(ctx context.Context, challenge Challenge) error
	// RecentChallenges reports how many challenges were created for the email since `since`, and when the latest was.
	RecentChallenges(ctx context.Context, email string, since time.Time) (count int, latest time.Time, err error)
	// RecentChallengesByIP counts challenges created from the same IP hash since `since`.
	RecentChallengesByIP(ctx context.Context, ipHash []byte, since time.Time) (int, error)
	GetChallenge(ctx context.Context, id string) (Challenge, error)
	// RecordFailedAttempt increments the attempt counter of an unconsumed challenge and returns the new count.
	RecordFailedAttempt(ctx context.Context, id string) (int, error)
	// ConsumeChallenge atomically consumes a challenge that is unconsumed, unexpired at `now`, and below maxAttempts,
	// then returns the user for its email, creating the user if needed. ErrChallengeInvalid otherwise.
	ConsumeChallenge(ctx context.Context, id string, now time.Time, maxAttempts int, newUserID string) (User, error)

	GetUser(ctx context.Context, id string) (User, error)

	CreateDevice(ctx context.Context, device Device) error
	CreateSession(ctx context.Context, session Session) error
	GetSessionByRefreshHash(ctx context.Context, hash []byte) (Session, error)
	GetSession(ctx context.Context, id string) (Session, error)
	// RotateSession revokes `oldID` with reason "rotated" if it is still active and inserts `next`, atomically.
	// ErrSessionRotated if `oldID` was no longer active (a concurrent rotation won).
	RotateSession(ctx context.Context, oldID string, next Session, now time.Time) error
	RevokeSession(ctx context.Context, id, reason string, now time.Time) error
	// RevokeFamily and RevokeAllForUser set the reason on EVERY matching session, including rows already revoked as
	// "rotated", so no access token issued for any of them is accepted afterwards. revoked_at keeps its first value.
	RevokeFamily(ctx context.Context, familyID, reason string, now time.Time) error
	RevokeAllForUser(ctx context.Context, userID, reason string, now time.Time) error
	TouchDevice(ctx context.Context, deviceID string, now time.Time) error
	// ListDevices returns the user's devices that are not revoked, most recently seen first.
	ListDevices(ctx context.Context, userID string) ([]Device, error)
	// RevokeDevice marks the user's device revoked and revokes all its sessions (reason "device_revoked").
	// ErrNotFound if the device does not belong to the user or is already revoked.
	RevokeDevice(ctx context.Context, userID, deviceID string, now time.Time) error

	Audit(ctx context.Context, event AuditEvent) error
}
