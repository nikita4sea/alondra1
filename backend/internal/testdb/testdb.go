// Package testdb provides a real, migrated PostgreSQL 17 for integration tests (build tag "integration").
//
// With ALONDRA1_TEST_DATABASE_URL set (CI service container) that database is used; otherwise an embedded
// PostgreSQL is downloaded once, cached in the temp folder, and started for the test. Only test files import this.
package testdb

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"

	embeddedpostgres "github.com/fergusstrange/embedded-postgres"
	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/postgres"
	"alondra1/backend/migrations"
)

var Quiet = slog.New(slog.NewTextHandler(io.Discard, nil))

// Start returns a pool on an empty database. If migrate is true, all migrations are applied.
func Start(t *testing.T, migrate bool) *pgxpool.Pool {
	t.Helper()
	ctx := context.Background()
	url := os.Getenv("ALONDRA1_TEST_DATABASE_URL")
	if url == "" {
		port := freePort(t)
		dir := t.TempDir()
		server := embeddedpostgres.NewDatabase(embeddedpostgres.DefaultConfig().
			Version(embeddedpostgres.V17).
			Port(uint32(port)).
			Database("alondra1").Username("alondra1").Password("test-only").
			RuntimePath(filepath.Join(dir, "runtime")).DataPath(filepath.Join(dir, "data")).
			BinariesPath(filepath.Join(os.TempDir(), "alondra1-embedded-postgres-17")).
			Logger(io.Discard).StartTimeout(2 * time.Minute))
		if err := server.Start(); err != nil {
			t.Fatalf("embedded PostgreSQL: %v", err)
		}
		t.Cleanup(func() { _ = server.Stop() })
		url = fmt.Sprintf("postgres://alondra1:test-only@127.0.0.1:%d/alondra1?sslmode=disable", port)
	}
	config, err := pgxpool.ParseConfig(url)
	if err != nil {
		t.Fatal(err)
	}
	connectCtx, cancel := context.WithTimeout(ctx, time.Minute)
	defer cancel()
	pool, err := postgres.ConnectWithConfig(connectCtx, config, Quiet)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	if _, err := pool.Exec(ctx, "DROP SCHEMA public CASCADE; CREATE SCHEMA public"); err != nil {
		t.Fatal(err)
	}
	if migrate {
		if err := postgres.Migrate(ctx, pool, migrations.FS, Quiet); err != nil {
			t.Fatal(err)
		}
	}
	return pool
}

// Truncate empties every application table.
func Truncate(t *testing.T, pool *pgxpool.Pool) {
	t.Helper()
	if _, err := pool.Exec(context.Background(), `TRUNCATE audit_log, user_configs, tenant_configs, task_completion_records, todo_tasks, task_lists, list_groups, invites, memberships, tenants, sessions, devices, auth_challenges, users RESTART IDENTITY CASCADE`); err != nil {
		t.Fatal(err)
	}
}

func freePort(t *testing.T) int {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	return listener.Addr().(*net.TCPAddr).Port
}
