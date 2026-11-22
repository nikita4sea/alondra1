package postgres

import (
	"context"
	"encoding/json"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/auth"
)

// AuthStore implements auth.Store on PostgreSQL. Every multi-statement operation runs in one transaction; the
// conditional UPDATEs take row locks, so concurrent consume/rotate calls succeed at most once.
type AuthStore struct{ pool *pgxpool.Pool }

func NewAuthStore(pool *pgxpool.Pool) *AuthStore { return &AuthStore{pool: pool} }

var _ auth.Store = (*AuthStore)(nil)

func (s *AuthStore) CreateChallenge(ctx context.Context, c auth.Challenge) error {
	return pgx.BeginFunc(ctx, s.pool, func(tx pgx.Tx) error {
		if _, err := tx.Exec(ctx, `UPDATE auth_challenges SET consumed_at = $2 WHERE email_normalized = $1 AND consumed_at IS NULL`,
			c.EmailNormalized, c.CreatedAt); err != nil {
			return err
		}
		_, err := tx.Exec(ctx, `INSERT INTO auth_challenges (id, email_normalized, otp_hmac, expires_at, attempt_count, created_at, request_ip_hash)
			VALUES ($1, $2, $3, $4, 0, $5, $6)`, c.ID, c.EmailNormalized, c.OTPHMAC, c.ExpiresAt, c.CreatedAt, c.RequestIPHash)
		return err
	})
}

func (s *AuthStore) RecentChallenges(ctx context.Context, email string, since time.Time) (int, time.Time, error) {
	var count int
	var latest *time.Time
	err := s.pool.QueryRow(ctx, `SELECT count(*), max(created_at) FROM auth_challenges WHERE email_normalized = $1 AND created_at >= $2`,
		email, since).Scan(&count, &latest)
	if err != nil {
		return 0, time.Time{}, err
	}
	if latest == nil {
		return count, time.Time{}, nil
	}
	return count, *latest, nil
}

func (s *AuthStore) RecentChallengesByIP(ctx context.Context, ipHash []byte, since time.Time) (int, error) {
	var count int
	err := s.pool.QueryRow(ctx, `SELECT count(*) FROM auth_challenges WHERE request_ip_hash = $1 AND created_at >= $2`, ipHash, since).Scan(&count)
	return count, err
}

func (s *AuthStore) GetChallenge(ctx context.Context, id string) (auth.Challenge, error) {
	var c auth.Challenge
	err := s.pool.QueryRow(ctx, `SELECT id::text, email_normalized, otp_hmac, expires_at, attempt_count, consumed_at, created_at, request_ip_hash
		FROM auth_challenges WHERE id = $1`, id).
		Scan(&c.ID, &c.EmailNormalized, &c.OTPHMAC, &c.ExpiresAt, &c.AttemptCount, &c.ConsumedAt, &c.CreatedAt, &c.RequestIPHash)
	if isNoRows(err) {
		return auth.Challenge{}, auth.ErrNotFound
	}
	return c, err
}

func (s *AuthStore) RecordFailedAttempt(ctx context.Context, id string) (int, error) {
	var count int
	err := s.pool.QueryRow(ctx, `UPDATE auth_challenges SET attempt_count = attempt_count + 1 WHERE id = $1 AND consumed_at IS NULL RETURNING attempt_count`, id).Scan(&count)
	if isNoRows(err) {
		return 0, auth.ErrChallengeInvalid
	}
	return count, err
}

func (s *AuthStore) ConsumeChallenge(ctx context.Context, id string, now time.Time, maxAttempts int, newUserID string) (auth.User, error) {
	var user auth.User
	err := pgx.BeginFunc(ctx, s.pool, func(tx pgx.Tx) error {
		var email string
		err := tx.QueryRow(ctx, `UPDATE auth_challenges SET consumed_at = $2
			WHERE id = $1 AND consumed_at IS NULL AND expires_at > $2 AND attempt_count < $3
			RETURNING email_normalized`, id, now, maxAttempts).Scan(&email)
		if isNoRows(err) {
			return auth.ErrChallengeInvalid
		}
		if err != nil {
			return err
		}
		if _, err := tx.Exec(ctx, `INSERT INTO users (id, email_normalized, created_at) VALUES ($1, $2, $3) ON CONFLICT (email_normalized) DO NOTHING`,
			newUserID, email, now); err != nil {
			return err
		}
		return tx.QueryRow(ctx, `SELECT id::text, email_normalized, created_at FROM users WHERE email_normalized = $1`, email).
			Scan(&user.ID, &user.EmailNormalized, &user.CreatedAt)
	})
	return user, err
}

func (s *AuthStore) GetUser(ctx context.Context, id string) (auth.User, error) {
	var u auth.User
	err := s.pool.QueryRow(ctx, `SELECT id::text, email_normalized, created_at FROM users WHERE id = $1 AND deleted_at IS NULL`, id).
		Scan(&u.ID, &u.EmailNormalized, &u.CreatedAt)
	if isNoRows(err) {
		return auth.User{}, auth.ErrNotFound
	}
	return u, err
}

func (s *AuthStore) CreateDevice(ctx context.Context, d auth.Device) error {
	_, err := s.pool.Exec(ctx, `INSERT INTO devices (id, user_id, name, platform, last_seen_at, created_at) VALUES ($1, $2, $3, $4, $5, $5)`,
		d.ID, d.UserID, d.Name, d.Platform, d.SeenAt)
	return err
}

const insertSessionSQL = `INSERT INTO sessions (id, user_id, device_id, refresh_hash, token_family_id, expires_at, created_at, last_used_at)
	VALUES ($1, $2, $3, $4, $5, $6, $7, $8)`

func (s *AuthStore) CreateSession(ctx context.Context, x auth.Session) error {
	_, err := s.pool.Exec(ctx, insertSessionSQL, x.ID, x.UserID, x.DeviceID, x.RefreshHash, x.TokenFamilyID, x.ExpiresAt, x.CreatedAt, x.LastUsedAt)
	return err
}

const sessionColumns = `id::text, user_id::text, device_id::text, refresh_hash, token_family_id::text, expires_at, revoked_at, coalesce(revoke_reason, ''), created_at, last_used_at`

func scanSession(row pgx.Row) (auth.Session, error) {
	var x auth.Session
	err := row.Scan(&x.ID, &x.UserID, &x.DeviceID, &x.RefreshHash, &x.TokenFamilyID, &x.ExpiresAt, &x.RevokedAt, &x.RevokeReason, &x.CreatedAt, &x.LastUsedAt)
	if isNoRows(err) {
		return auth.Session{}, auth.ErrNotFound
	}
	return x, err
}

func (s *AuthStore) GetSessionByRefreshHash(ctx context.Context, hash []byte) (auth.Session, error) {
	return scanSession(s.pool.QueryRow(ctx, `SELECT `+sessionColumns+` FROM sessions WHERE refresh_hash = $1`, hash))
}

func (s *AuthStore) GetSession(ctx context.Context, id string) (auth.Session, error) {
	return scanSession(s.pool.QueryRow(ctx, `SELECT `+sessionColumns+` FROM sessions WHERE id = $1`, id))
}

func (s *AuthStore) RotateSession(ctx context.Context, oldID string, next auth.Session, now time.Time) error {
	return pgx.BeginFunc(ctx, s.pool, func(tx pgx.Tx) error {
		tag, err := tx.Exec(ctx, `UPDATE sessions SET revoked_at = $2, revoke_reason = 'rotated', last_used_at = $2 WHERE id = $1 AND revoked_at IS NULL`, oldID, now)
		if err != nil {
			return err
		}
		if tag.RowsAffected() != 1 {
			return auth.ErrSessionRotated
		}
		_, err = tx.Exec(ctx, insertSessionSQL, next.ID, next.UserID, next.DeviceID, next.RefreshHash, next.TokenFamilyID, next.ExpiresAt, next.CreatedAt, next.LastUsedAt)
		return err
	})
}

func (s *AuthStore) RevokeSession(ctx context.Context, id, reason string, now time.Time) error {
	_, err := s.pool.Exec(ctx, `UPDATE sessions SET revoked_at = coalesce(revoked_at, $3), revoke_reason = $2 WHERE id = $1`, id, reason, now)
	return err
}

func (s *AuthStore) RevokeFamily(ctx context.Context, familyID, reason string, now time.Time) error {
	_, err := s.pool.Exec(ctx, `UPDATE sessions SET revoked_at = coalesce(revoked_at, $3), revoke_reason = $2 WHERE token_family_id = $1`, familyID, reason, now)
	return err
}

func (s *AuthStore) RevokeAllForUser(ctx context.Context, userID, reason string, now time.Time) error {
	_, err := s.pool.Exec(ctx, `UPDATE sessions SET revoked_at = coalesce(revoked_at, $3), revoke_reason = $2 WHERE user_id = $1`, userID, reason, now)
	return err
}

func (s *AuthStore) TouchDevice(ctx context.Context, deviceID string, now time.Time) error {
	_, err := s.pool.Exec(ctx, `UPDATE devices SET last_seen_at = greatest(last_seen_at, $2) WHERE id = $1`, deviceID, now)
	return err
}

func (s *AuthStore) ListDevices(ctx context.Context, userID string) ([]auth.Device, error) {
	rows, err := s.pool.Query(ctx, `SELECT id::text, user_id::text, name, platform, last_seen_at FROM devices
		WHERE user_id = $1 AND revoked_at IS NULL ORDER BY last_seen_at DESC`, userID)
	if err != nil {
		return nil, err
	}
	return pgx.CollectRows(rows, func(row pgx.CollectableRow) (auth.Device, error) {
		var d auth.Device
		err := row.Scan(&d.ID, &d.UserID, &d.Name, &d.Platform, &d.SeenAt)
		return d, err
	})
}

func (s *AuthStore) RevokeDevice(ctx context.Context, userID, deviceID string, now time.Time) error {
	return pgx.BeginFunc(ctx, s.pool, func(tx pgx.Tx) error {
		tag, err := tx.Exec(ctx, `UPDATE devices SET revoked_at = $3 WHERE id = $1 AND user_id = $2 AND revoked_at IS NULL`, deviceID, userID, now)
		if err != nil {
			return err
		}
		if tag.RowsAffected() != 1 {
			return auth.ErrNotFound
		}
		_, err = tx.Exec(ctx, `UPDATE sessions SET revoked_at = coalesce(revoked_at, $2), revoke_reason = $3 WHERE device_id = $1`, deviceID, now, auth.RevokeDevice)
		return err
	})
}

func (s *AuthStore) Audit(ctx context.Context, e auth.AuditEvent) error {
	return insertAudit(ctx, s.pool, e)
}

type execer interface {
	Exec(ctx context.Context, sql string, arguments ...any) (pgconn.CommandTag, error)
}

func insertAudit(ctx context.Context, db execer, e auth.AuditEvent) error {
	metadata := e.Metadata
	if metadata == nil {
		metadata = map[string]string{}
	}
	payload, err := json.Marshal(metadata)
	if err != nil {
		return err
	}
	_, err = db.Exec(ctx, `INSERT INTO audit_log (tenant_id, actor_user_id, event_type, target_id, created_at, metadata)
		VALUES ($1, $2, $3, $4, $5, $6)`, nullable(e.TenantID), nullable(e.ActorUserID), e.EventType, nullable(e.TargetID), e.CreatedAt, payload)
	return err
}

func nullable(value string) any {
	if value == "" {
		return nil
	}
	return value
}
