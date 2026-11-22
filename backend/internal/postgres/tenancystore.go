package postgres

import (
	"context"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/tenancy"
)

// TenancyStore runs tenancy flows in one READ COMMITTED transaction each; LockTenant (SELECT ... FOR UPDATE)
// serializes all changes to one space, so rules such as "never remove the last owner" hold under concurrency.
type TenancyStore struct{ pool *pgxpool.Pool }

func NewTenancyStore(pool *pgxpool.Pool) *TenancyStore { return &TenancyStore{pool: pool} }

var _ tenancy.Store = (*TenancyStore)(nil)

func (s *TenancyStore) InTx(ctx context.Context, fn func(tenancy.Tx) error) error {
	return pgx.BeginFunc(ctx, s.pool, func(tx pgx.Tx) error { return fn(tenancyTx{tx}) })
}

type tenancyTx struct{ tx pgx.Tx }

func (t tenancyTx) LockTenant(ctx context.Context, id string) (tenancy.Tenant, error) {
	var x tenancy.Tenant
	err := t.tx.QueryRow(ctx, `SELECT id::text, type, name, created_by::text, created_at, updated_at FROM tenants
		WHERE id = $1 AND deleted_at IS NULL FOR UPDATE`, id).Scan(&x.ID, &x.Type, &x.Name, &x.CreatedBy, &x.CreatedAt, &x.UpdatedAt)
	if isNoRows(err) {
		return tenancy.Tenant{}, tenancy.ErrNotFound
	}
	return x, err
}

func (t tenancyTx) InsertTenant(ctx context.Context, x tenancy.Tenant) error {
	_, err := t.tx.Exec(ctx, `INSERT INTO tenants (id, type, name, created_by, created_at, updated_at) VALUES ($1, $2, $3, $4, $5, $6)`,
		x.ID, x.Type, x.Name, x.CreatedBy, x.CreatedAt, x.UpdatedAt)
	return err
}

func (t tenancyTx) RenameTenant(ctx context.Context, id, name string, now time.Time) error {
	_, err := t.tx.Exec(ctx, `UPDATE tenants SET name = $2, updated_at = $3 WHERE id = $1`, id, name, now)
	return err
}

func (t tenancyTx) SoftDeleteTenant(ctx context.Context, id string, now time.Time) error {
	_, err := t.tx.Exec(ctx, `UPDATE tenants SET deleted_at = $2, updated_at = $2 WHERE id = $1 AND deleted_at IS NULL`, id, now)
	return err
}

func (t tenancyTx) Membership(ctx context.Context, tenantID, userID string) (tenancy.Membership, error) {
	var m tenancy.Membership
	var status string
	err := t.tx.QueryRow(ctx, `SELECT tenant_id::text, user_id::text, role, status, created_at FROM memberships WHERE tenant_id = $1 AND user_id = $2`,
		tenantID, userID).Scan(&m.TenantID, &m.UserID, &m.Role, &status, &m.CreatedAt)
	if isNoRows(err) {
		return tenancy.Membership{}, tenancy.ErrNotFound
	}
	m.Active = status == "active"
	return m, err
}

func (t tenancyTx) CountActive(ctx context.Context, tenantID string) (int, int, error) {
	var owners, members int
	err := t.tx.QueryRow(ctx, `SELECT count(*) FILTER (WHERE role = 'owner'), count(*) FROM memberships WHERE tenant_id = $1 AND status = 'active'`,
		tenantID).Scan(&owners, &members)
	return owners, members, err
}

func (t tenancyTx) UpsertMembership(ctx context.Context, m tenancy.Membership, now time.Time) error {
	status := "removed"
	if m.Active {
		status = "active"
	}
	created := m.CreatedAt
	if created.IsZero() {
		created = now
	}
	// A rejoining member gets a new join date; a role change keeps it.
	_, err := t.tx.Exec(ctx, `INSERT INTO memberships (tenant_id, user_id, role, status, created_at, updated_at) VALUES ($1, $2, $3, $4, $5, $6)
		ON CONFLICT (tenant_id, user_id) DO UPDATE SET role = excluded.role, status = excluded.status, updated_at = excluded.updated_at,
			created_at = CASE WHEN memberships.status = 'removed' AND excluded.status = 'active' THEN excluded.updated_at ELSE memberships.created_at END`,
		m.TenantID, m.UserID, m.Role, status, created, now)
	return err
}

func (t tenancyTx) RemoveAllMemberships(ctx context.Context, tenantID string, now time.Time) error {
	_, err := t.tx.Exec(ctx, `UPDATE memberships SET status = 'removed', updated_at = $2 WHERE tenant_id = $1 AND status = 'active'`, tenantID, now)
	return err
}

func (t tenancyTx) ListMembers(ctx context.Context, tenantID string) ([]tenancy.MemberView, error) {
	rows, err := t.tx.Query(ctx, `SELECT m.user_id::text, u.email_normalized, m.role, m.created_at FROM memberships m JOIN users u ON u.id = m.user_id
		WHERE m.tenant_id = $1 AND m.status = 'active' AND u.deleted_at IS NULL
		ORDER BY CASE m.role WHEN 'owner' THEN 0 WHEN 'admin' THEN 1 ELSE 2 END, u.email_normalized`, tenantID)
	if err != nil {
		return nil, err
	}
	return pgx.CollectRows(rows, func(row pgx.CollectableRow) (tenancy.MemberView, error) {
		var m tenancy.MemberView
		err := row.Scan(&m.UserID, &m.Email, &m.Role, &m.JoinedAt)
		return m, err
	})
}

func (t tenancyTx) ListMemberships(ctx context.Context, userID string) ([]tenancy.View, error) {
	rows, err := t.tx.Query(ctx, `SELECT t.id::text, t.type, t.name, t.created_by::text, t.created_at, t.updated_at, m.role
		FROM memberships m JOIN tenants t ON t.id = m.tenant_id
		WHERE m.user_id = $1 AND m.status = 'active' AND t.deleted_at IS NULL ORDER BY t.created_at, t.id`, userID)
	if err != nil {
		return nil, err
	}
	return pgx.CollectRows(rows, func(row pgx.CollectableRow) (tenancy.View, error) {
		var v tenancy.View
		err := row.Scan(&v.ID, &v.Type, &v.Name, &v.CreatedBy, &v.CreatedAt, &v.UpdatedAt, &v.Role)
		return v, err
	})
}

const inviteColumns = `id::text, tenant_id::text, email_normalized, role, token_hash, expires_at, accepted_at, revoked_at, invited_by::text, created_at`

func scanInvite(row pgx.Row) (tenancy.Invite, error) {
	var i tenancy.Invite
	err := row.Scan(&i.ID, &i.TenantID, &i.Email, &i.Role, &i.TokenHash, &i.ExpiresAt, &i.AcceptedAt, &i.RevokedAt, &i.InvitedBy, &i.CreatedAt)
	if isNoRows(err) {
		return tenancy.Invite{}, tenancy.ErrNotFound
	}
	return i, err
}

func (t tenancyTx) InsertInvite(ctx context.Context, i tenancy.Invite) error {
	_, err := t.tx.Exec(ctx, `INSERT INTO invites (id, tenant_id, email_normalized, role, token_hash, expires_at, invited_by, created_at)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8)`, i.ID, i.TenantID, i.Email, i.Role, i.TokenHash, i.ExpiresAt, i.InvitedBy, i.CreatedAt)
	return err
}

func (t tenancyTx) InviteByTokenHash(ctx context.Context, hash []byte) (tenancy.Invite, error) {
	return scanInvite(t.tx.QueryRow(ctx, `SELECT `+inviteColumns+` FROM invites WHERE token_hash = $1 FOR UPDATE`, hash))
}

func (t tenancyTx) Invite(ctx context.Context, tenantID, id string) (tenancy.Invite, error) {
	return scanInvite(t.tx.QueryRow(ctx, `SELECT `+inviteColumns+` FROM invites WHERE id = $1 AND tenant_id = $2`, id, tenantID))
}

func (t tenancyTx) ListPendingInvites(ctx context.Context, tenantID string, now time.Time) ([]tenancy.Invite, error) {
	rows, err := t.tx.Query(ctx, `SELECT `+inviteColumns+` FROM invites
		WHERE tenant_id = $1 AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > $2 ORDER BY created_at DESC`, tenantID, now)
	if err != nil {
		return nil, err
	}
	return pgx.CollectRows(rows, func(row pgx.CollectableRow) (tenancy.Invite, error) { return scanInvite(row) })
}

func (t tenancyTx) InvitesCreatedSince(ctx context.Context, tenantID string, since time.Time) (int, error) {
	var count int
	err := t.tx.QueryRow(ctx, `SELECT count(*) FROM invites WHERE tenant_id = $1 AND created_at >= $2`, tenantID, since).Scan(&count)
	return count, err
}

func (t tenancyTx) RevokePendingInvites(ctx context.Context, tenantID, email string, now time.Time) error {
	_, err := t.tx.Exec(ctx, `UPDATE invites SET revoked_at = $3 WHERE tenant_id = $1 AND ($2 = '' OR email_normalized = $2)
		AND accepted_at IS NULL AND revoked_at IS NULL`, tenantID, email, now)
	return err
}

func (t tenancyTx) RevokePendingInvitesTo(ctx context.Context, email string, now time.Time) error {
	_, err := t.tx.Exec(ctx, `UPDATE invites SET revoked_at = $2 WHERE email_normalized = $1 AND accepted_at IS NULL AND revoked_at IS NULL`, email, now)
	return err
}

func (t tenancyTx) RevokeInvite(ctx context.Context, id string, now time.Time) error {
	_, err := t.tx.Exec(ctx, `UPDATE invites SET revoked_at = $2 WHERE id = $1 AND accepted_at IS NULL AND revoked_at IS NULL`, id, now)
	return err
}

func (t tenancyTx) MarkInviteAccepted(ctx context.Context, id, userID string, now time.Time) error {
	_, err := t.tx.Exec(ctx, `UPDATE invites SET accepted_at = $3, accepted_by = $2 WHERE id = $1`, id, userID, now)
	return err
}

func (t tenancyTx) UserEmail(ctx context.Context, userID string) (string, error) {
	var email string
	err := t.tx.QueryRow(ctx, `SELECT email_normalized FROM users WHERE id = $1 AND deleted_at IS NULL`, userID).Scan(&email)
	if isNoRows(err) {
		return "", tenancy.ErrNotFound
	}
	return email, err
}

// DeleteUser revokes every session and device and pseudonymizes the address, which frees it for a new sign-up and
// removes it from the database. The row stays (deleted_at) so foreign keys in history remain valid.
func (t tenancyTx) DeleteUser(ctx context.Context, userID string, now time.Time) error {
	if _, err := t.tx.Exec(ctx, `UPDATE sessions SET revoked_at = coalesce(revoked_at, $2), revoke_reason = $3 WHERE user_id = $1`, userID, now, auth.RevokeDeleted); err != nil {
		return err
	}
	if _, err := t.tx.Exec(ctx, `UPDATE devices SET revoked_at = coalesce(revoked_at, $2) WHERE user_id = $1`, userID, now); err != nil {
		return err
	}
	_, err := t.tx.Exec(ctx, `UPDATE users SET deleted_at = $2, email_normalized = 'deleted-' || id::text || '@deleted.invalid' WHERE id = $1 AND deleted_at IS NULL`, userID, now)
	return err
}

func (t tenancyTx) Audit(ctx context.Context, e auth.AuditEvent) error { return insertAudit(ctx, t.tx, e) }
