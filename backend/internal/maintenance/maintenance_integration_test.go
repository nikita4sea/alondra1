//go:build integration

package maintenance_test

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/maintenance"
	"alondra1/backend/internal/testdb"
)

var now = time.Date(2026, 11, 22, 12, 0, 0, 0, time.UTC)

func days(n int) time.Time { return now.Add(-time.Duration(n) * 24 * time.Hour) }

func exec(t *testing.T, pool *pgxpool.Pool, sql string, args ...any) {
	t.Helper()
	if _, err := pool.Exec(context.Background(), sql, args...); err != nil {
		t.Fatalf("%s: %v", sql, err)
	}
}

func count(t *testing.T, pool *pgxpool.Pool, sql string, args ...any) int {
	t.Helper()
	var n int
	if err := pool.QueryRow(context.Background(), sql, args...).Scan(&n); err != nil {
		t.Fatalf("%s: %v", sql, err)
	}
	return n
}

const (
	owner     = "00000000-0000-7000-8000-000000000001"
	goneLong  = "00000000-0000-7000-8000-000000000002" // removed 40 days ago
	goneShort = "00000000-0000-7000-8000-000000000003" // removed 5 days ago
	live      = "00000000-0000-7000-8000-0000000000a1"
	deadOld   = "00000000-0000-7000-8000-0000000000a2" // deleted 40 days ago
	deadNew   = "00000000-0000-7000-8000-0000000000a3" // deleted 10 days ago
	device    = "00000000-0000-7000-8000-0000000000d1"
)

func seed(t *testing.T, pool *pgxpool.Pool) {
	for i, user := range []string{owner, goneLong, goneShort} {
		exec(t, pool, `INSERT INTO users (id, email_normalized, created_at) VALUES ($1, $2, $3)`, user, []string{"o@x.test", "l@x.test", "s@x.test"}[i], days(500))
	}
	exec(t, pool, `INSERT INTO devices (id, user_id, name, platform, last_seen_at, created_at) VALUES ($1, $2, 'd', 'windows', $3, $3)`, device, owner, days(1))
	for _, tenant := range []struct {
		id      string
		deleted *time.Time
	}{{live, nil}, {deadOld, ptr(days(40))}, {deadNew, ptr(days(10))}} {
		exec(t, pool, `INSERT INTO tenants (id, type, name, created_by, created_at, updated_at, deleted_at) VALUES ($1, 'family', 'F', $2, $3, $3, $4)`, tenant.id, owner, days(500), tenant.deleted)
	}
	exec(t, pool, `INSERT INTO memberships VALUES ($1, $2, 'owner', 'active', $3, $3)`, live, owner, days(500))
	exec(t, pool, `INSERT INTO memberships VALUES ($1, $2, 'member', 'removed', $3, $4)`, live, goneLong, days(500), days(40))
	exec(t, pool, `INSERT INTO memberships VALUES ($1, $2, 'member', 'removed', $3, $4)`, live, goneShort, days(500), days(5))
	task := func(tenant, user, id string) {
		exec(t, pool, `INSERT INTO todo_tasks (tenant_id, owner_user_id, id, list_id, data, version, schema_version, created_at, updated_at)
			VALUES ($1, $2, $3, 'l', '{}', 1, 1, $4, $4)`, tenant, user, id, days(50))
	}
	task(live, owner, "keep-owner")
	task(live, goneLong, "purge-removed-long-ago")
	task(live, goneShort, "keep-removed-recently")
	task(deadOld, owner, "purge-deleted-space")
	task(deadNew, owner, "keep-recently-deleted-space")
	exec(t, pool, `INSERT INTO user_configs VALUES ($1, $2, 'appearance', '{}', 1, $3)`, live, goneLong, days(50))

	// Change log: two old entries (purged, floor = the higher one), one recent.
	for _, at := range []time.Time{days(100), days(95), days(10)} {
		exec(t, pool, `INSERT INTO change_log (tenant_id, resource_type, resource_id, action, payload, visibility_user_id, created_at)
			VALUES ($1, 'task', 'keep-owner', 'upsert', '{}', $2, $3)`, live, owner, at)
	}
	exec(t, pool, `INSERT INTO mutation_receipts VALUES ($1, $2, $3, 'old', '{}', $4), ($1, $2, $3, 'new', '{}', $5)`, live, owner, device, days(100), days(1))
	exec(t, pool, `INSERT INTO auth_challenges (id, email_normalized, otp_hmac, expires_at, created_at, request_ip_hash)
		VALUES ('00000000-0000-4000-8000-0000000000c1', 'o@x.test', decode(repeat('00', 32), 'hex'), $1, $2, '\x01'),
		       ('00000000-0000-4000-8000-0000000000c2', 'o@x.test', decode(repeat('00', 32), 'hex'), $3, $4, '\x01')`,
		days(2), days(2), now.Add(5*time.Minute), now.Add(-5*time.Minute))
	exec(t, pool, `INSERT INTO sessions (id, user_id, device_id, refresh_hash, token_family_id, expires_at, created_at, last_used_at)
		VALUES ('00000000-0000-7000-8000-0000000000e1', $1, $2, '\x0101', '00000000-0000-7000-8000-0000000000f1', $3, $4, $4),
		       ('00000000-0000-7000-8000-0000000000e2', $1, $2, '\x0202', '00000000-0000-7000-8000-0000000000f2', $5, $6, $6)`,
		owner, device, days(10), days(40), now.Add(29*24*time.Hour), days(1))
	exec(t, pool, `INSERT INTO audit_log (event_type, created_at) VALUES ('old', $1), ('new', $2)`, days(400), days(1))
	exec(t, pool, `INSERT INTO invites (id, tenant_id, email_normalized, role, token_hash, expires_at, invited_by, created_at)
		VALUES ('00000000-0000-4000-8000-0000000000b1', $1, 'a@x.test', 'member', decode(repeat('01', 32), 'hex'), $2, $3, $4),
		       ('00000000-0000-4000-8000-0000000000b2', $1, 'b@x.test', 'member', decode(repeat('02', 32), 'hex'), $5, $3, $6)`,
		live, days(40), owner, days(47), now.Add(6*24*time.Hour), days(1))
}

func ptr(t time.Time) *time.Time { return &t }

func TestRetentionPolicy(t *testing.T) {
	pool := testdb.Start(t, true)
	seed(t, pool)
	ctx := context.Background()

	result, err := maintenance.Run(ctx, pool, maintenance.DefaultPolicy(), now, testdb.Quiet)
	if err != nil {
		t.Fatal(err)
	}

	tasks := map[string]bool{}
	rows, _ := pool.Query(ctx, `SELECT id FROM todo_tasks`)
	for rows.Next() {
		var id string
		_ = rows.Scan(&id)
		tasks[id] = true
	}
	rows.Close()
	for id, want := range map[string]bool{
		"keep-owner": true, "keep-removed-recently": true, "keep-recently-deleted-space": true,
		"purge-removed-long-ago": false, "purge-deleted-space": false,
	} {
		if tasks[id] != want {
			t.Errorf("task %s present=%v, want %v", id, tasks[id], want)
		}
	}
	if n := count(t, pool, `SELECT count(*) FROM user_configs`); n != 0 {
		t.Errorf("removed member's settings: %d", n)
	}
	if n := count(t, pool, `SELECT count(*) FROM tenants WHERE purged_at IS NOT NULL`); n != 1 {
		t.Errorf("purged spaces: %d", n)
	}
	if n := count(t, pool, `SELECT count(*) FROM tenants`); n != 3 {
		t.Errorf("tenant rows stay as tombstones: %d", n)
	}

	// Change log: the two old entries are gone and the floor is the higher of them.
	var floor, oldestKept int64
	_ = pool.QueryRow(ctx, `SELECT floor_sequence FROM change_log_floor WHERE tenant_id = $1`, live).Scan(&floor)
	_ = pool.QueryRow(ctx, `SELECT min(sequence) FROM change_log WHERE tenant_id = $1`, live).Scan(&oldestKept)
	if count(t, pool, `SELECT count(*) FROM change_log`) != 1 || floor != oldestKept-1 {
		t.Errorf("change log: floor=%d oldest kept=%d", floor, oldestKept)
	}

	for table, want := range map[string]int{
		"mutation_receipts": 1, "auth_challenges": 1, "sessions": 1, "audit_log": 1, "invites": 1,
	} {
		if n := count(t, pool, `SELECT count(*) FROM `+table); n != want {
			t.Errorf("%s: %d rows left, want %d", table, n, want)
		}
	}
	if result["change_log"] != 2 || result["deleted_spaces"] != 1 {
		t.Errorf("result: %v", result)
	}

	// A second pass finds nothing more.
	again, err := maintenance.Run(ctx, pool, maintenance.DefaultPolicy(), now, testdb.Quiet)
	if err != nil {
		t.Fatal(err)
	}
	for step, n := range again {
		if n != 0 {
			t.Errorf("second pass deleted %d in %s", n, step)
		}
	}
}

func TestOnlyOneInstanceRunsAtATime(t *testing.T) {
	pool := testdb.Start(t, true)
	ctx := context.Background()
	conn, err := pool.Acquire(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Release()
	if _, err := conn.Exec(ctx, `SELECT pg_advisory_lock(7351204612)`); err != nil {
		t.Fatal(err)
	}
	if _, err := maintenance.Run(ctx, pool, maintenance.DefaultPolicy(), now, testdb.Quiet); !errors.Is(err, maintenance.ErrBusy) {
		t.Fatalf("expected ErrBusy, got %v", err)
	}
	if _, err := conn.Exec(ctx, `SELECT pg_advisory_unlock(7351204612)`); err != nil {
		t.Fatal(err)
	}
	if _, err := maintenance.Run(ctx, pool, maintenance.DefaultPolicy(), now, testdb.Quiet); err != nil {
		t.Fatalf("after the other instance: %v", err)
	}
}
