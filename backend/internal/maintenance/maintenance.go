// Package maintenance enforces the retention policy of docs/decisions/0003-retention.md: it purges old change-log
// entries (advancing each space's cursor floor), mutation receipts, sign-in challenges, expired sessions, the content
// of spaces deleted more than 30 days ago, the private data of members removed more than 30 days ago, closed
// invitations, and old audit entries. Only one API instance runs it at a time.
package maintenance

import (
	"context"
	"errors"
	"log/slog"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type Policy struct {
	ChangeLog            time.Duration // sync tombstones and changes; clients older than this bootstrap again
	Receipts             time.Duration
	Challenges           time.Duration
	SessionsAfterExpiry  time.Duration
	DeletedSpaceContent  time.Duration
	RemovedMemberContent time.Duration
	ClosedInvites        time.Duration
	Audit                time.Duration
	BatchSize            int
}

// DefaultPolicy is ADR 0003.
func DefaultPolicy() Policy {
	return Policy{
		ChangeLog:            90 * 24 * time.Hour,
		Receipts:             90 * 24 * time.Hour,
		Challenges:           24 * time.Hour,
		SessionsAfterExpiry:  7 * 24 * time.Hour,
		DeletedSpaceContent:  30 * 24 * time.Hour,
		RemovedMemberContent: 30 * 24 * time.Hour,
		ClosedInvites:        30 * 24 * time.Hour,
		Audit:                365 * 24 * time.Hour,
		BatchSize:            5000,
	}
}

// Result counts deleted rows per category.
type Result map[string]int64

// ErrBusy: another instance holds the maintenance lock; nothing was done.
var ErrBusy = errors.New("maintenance is running elsewhere")

const lockID = 7_351_204_612

// Run performs one pass. Each step commits on its own (in batches), so a failure leaves earlier steps done.
func Run(ctx context.Context, pool *pgxpool.Pool, policy Policy, now time.Time, log *slog.Logger) (Result, error) {
	conn, err := pool.Acquire(ctx)
	if err != nil {
		return nil, err
	}
	defer conn.Release()
	var locked bool
	if err := conn.QueryRow(ctx, `SELECT pg_try_advisory_lock($1)`, lockID).Scan(&locked); err != nil {
		return nil, err
	}
	if !locked {
		return nil, ErrBusy
	}
	defer conn.Exec(context.Background(), `SELECT pg_advisory_unlock($1)`, lockID)

	result := Result{}
	steps := []struct {
		name string
		run  func() (int64, error)
	}{
		{"change_log", func() (int64, error) { return purgeChangeLog(ctx, pool, now.Add(-policy.ChangeLog), policy.BatchSize) }},
		{"mutation_receipts", func() (int64, error) {
			return batched(ctx, pool, policy.BatchSize, `DELETE FROM mutation_receipts WHERE ctid IN (SELECT ctid FROM mutation_receipts WHERE created_at < $1 LIMIT $2)`, now.Add(-policy.Receipts))
		}},
		{"auth_challenges", func() (int64, error) {
			return batched(ctx, pool, policy.BatchSize, `DELETE FROM auth_challenges WHERE ctid IN (SELECT ctid FROM auth_challenges WHERE created_at < $1 LIMIT $2)`, now.Add(-policy.Challenges))
		}},
		// Rotated sessions are kept until they would have expired anyway (+ a margin), so reuse of a stolen refresh
		// token is detected for as long as that token could have been valid.
		{"sessions", func() (int64, error) {
			return batched(ctx, pool, policy.BatchSize, `DELETE FROM sessions WHERE ctid IN (SELECT ctid FROM sessions WHERE expires_at < $1 LIMIT $2)`, now.Add(-policy.SessionsAfterExpiry))
		}},
		{"deleted_spaces", func() (int64, error) { return purgeDeletedSpaces(ctx, pool, now, now.Add(-policy.DeletedSpaceContent)) }},
		{"removed_members", func() (int64, error) { return purgeRemovedMembers(ctx, pool, now.Add(-policy.RemovedMemberContent)) }},
		{"invites", func() (int64, error) {
			tag, err := pool.Exec(ctx, `DELETE FROM invites WHERE expires_at < $1 OR accepted_at < $1 OR revoked_at < $1`, now.Add(-policy.ClosedInvites))
			return tag.RowsAffected(), err
		}},
		{"audit_log", func() (int64, error) {
			return batched(ctx, pool, policy.BatchSize, `DELETE FROM audit_log WHERE id IN (SELECT id FROM audit_log WHERE created_at < $1 LIMIT $2)`, now.Add(-policy.Audit))
		}},
	}
	for _, step := range steps {
		n, err := step.run()
		if err != nil {
			return result, errors.New("maintenance step " + step.name + ": " + err.Error())
		}
		result[step.name] = n
	}
	if log != nil {
		args := []any{}
		for name, n := range result {
			args = append(args, name, n)
		}
		log.Info("maintenance finished", args...)
	}
	return result, nil
}

func batched(ctx context.Context, pool *pgxpool.Pool, size int, sql string, cutoff time.Time) (int64, error) {
	var total int64
	for {
		tag, err := pool.Exec(ctx, sql, cutoff, size)
		if err != nil {
			return total, err
		}
		total += tag.RowsAffected()
		if tag.RowsAffected() < int64(size) {
			return total, nil
		}
	}
}

// purgeChangeLog deletes old entries and raises each space's floor to the highest deleted sequence, in the same
// transaction, so a client whose cursor points into the deleted range gets 410 CURSOR_EXPIRED and bootstraps again.
func purgeChangeLog(ctx context.Context, pool *pgxpool.Pool, cutoff time.Time, size int) (int64, error) {
	var total int64
	for {
		var n int64
		err := pgx.BeginFunc(ctx, pool, func(tx pgx.Tx) error {
			return tx.QueryRow(ctx, `
				WITH purged AS (
					DELETE FROM change_log WHERE sequence IN (
						SELECT sequence FROM change_log WHERE created_at < $1 ORDER BY sequence LIMIT $2)
					RETURNING tenant_id, sequence),
				floors AS (
					INSERT INTO change_log_floor (tenant_id, floor_sequence)
					SELECT tenant_id, max(sequence) FROM purged GROUP BY tenant_id
					ON CONFLICT (tenant_id) DO UPDATE SET floor_sequence = greatest(change_log_floor.floor_sequence, excluded.floor_sequence)
					RETURNING 1)
				SELECT count(*) FROM purged`, cutoff, size).Scan(&n)
		})
		if err != nil {
			return total, err
		}
		total += n
		if n < int64(size) {
			return total, nil
		}
	}
}

// purgeDeletedSpaces removes the content of spaces deleted before cutoff and marks them purged. The tenant row,
// memberships, and audit entries stay as the tombstone.
func purgeDeletedSpaces(ctx context.Context, pool *pgxpool.Pool, now, cutoff time.Time) (int64, error) {
	rows, err := pool.Query(ctx, `SELECT id::text FROM tenants WHERE deleted_at < $1 AND purged_at IS NULL`, cutoff)
	if err != nil {
		return 0, err
	}
	ids, err := pgx.CollectRows(rows, pgx.RowTo[string])
	if err != nil {
		return 0, err
	}
	var total int64
	for _, id := range ids {
		err := pgx.BeginFunc(ctx, pool, func(tx pgx.Tx) error {
			for _, table := range []string{"todo_tasks", "task_lists", "list_groups", "task_completion_records", "user_configs", "tenant_configs", "mutation_receipts", "change_log", "invites"} {
				tag, err := tx.Exec(ctx, `DELETE FROM `+table+` WHERE tenant_id = $1`, id)
				if err != nil {
					return err
				}
				total += tag.RowsAffected()
			}
			_, err := tx.Exec(ctx, `UPDATE tenants SET purged_at = $2 WHERE id = $1`, id, now)
			return err
		})
		if err != nil {
			return total, err
		}
	}
	return total, nil
}

// purgeRemovedMembers removes the private data of members whose membership ended before cutoff and who have not
// rejoined (a rejoin makes the membership active again and keeps the data).
func purgeRemovedMembers(ctx context.Context, pool *pgxpool.Pool, cutoff time.Time) (int64, error) {
	var total int64
	statements := []string{
		`DELETE FROM todo_tasks x USING memberships m WHERE m.tenant_id = x.tenant_id AND m.user_id = x.owner_user_id AND m.status = 'removed' AND m.updated_at < $1`,
		`DELETE FROM task_lists x USING memberships m WHERE m.tenant_id = x.tenant_id AND m.user_id = x.owner_user_id AND m.status = 'removed' AND m.updated_at < $1`,
		`DELETE FROM list_groups x USING memberships m WHERE m.tenant_id = x.tenant_id AND m.user_id = x.owner_user_id AND m.status = 'removed' AND m.updated_at < $1`,
		`DELETE FROM task_completion_records x USING memberships m WHERE m.tenant_id = x.tenant_id AND m.user_id = x.owner_user_id AND m.status = 'removed' AND m.updated_at < $1`,
		`DELETE FROM user_configs x USING memberships m WHERE m.tenant_id = x.tenant_id AND m.user_id = x.user_id AND m.status = 'removed' AND m.updated_at < $1`,
		`DELETE FROM mutation_receipts x USING memberships m WHERE m.tenant_id = x.tenant_id AND m.user_id = x.user_id AND m.status = 'removed' AND m.updated_at < $1`,
		`DELETE FROM change_log x USING memberships m WHERE m.tenant_id = x.tenant_id AND m.user_id = x.visibility_user_id AND m.status = 'removed' AND m.updated_at < $1`,
	}
	for _, sql := range statements {
		tag, err := pool.Exec(ctx, sql, cutoff)
		if err != nil {
			return total, err
		}
		total += tag.RowsAffected()
	}
	return total, nil
}
