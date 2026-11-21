//go:build integration

// Integration tests against a real PostgreSQL. Run with:
//
//	go test -tags integration ./internal/postgres/...
//
// With ALONDRA1_TEST_DATABASE_URL set (CI service container) that database is used and its public schema is reset.
// Otherwise an embedded PostgreSQL is downloaded and started in a temporary folder.
package postgres_test

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"testing"
	"testing/fstest"
	"time"

	embeddedpostgres "github.com/fergusstrange/embedded-postgres"
	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/auth/storetest"
	"alondra1/backend/internal/postgres"
	"alondra1/backend/migrations"
)

var quiet = slog.New(slog.NewTextHandler(io.Discard, nil))

func database(t *testing.T) *pgxpool.Pool {
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
	pool, err := postgres.ConnectWithConfig(connectCtx, config, quiet)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	if _, err := pool.Exec(ctx, "DROP SCHEMA public CASCADE; CREATE SCHEMA public"); err != nil {
		t.Fatal(err)
	}
	return pool
}

func freePort(t *testing.T) int {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	return listener.Addr().(*net.TCPAddr).Port
}

func TestPostgres(t *testing.T) {
	pool := database(t)
	ctx := context.Background()

	t.Run("migrations apply once and are idempotent", func(t *testing.T) {
		if err := postgres.Migrate(ctx, pool, migrations.FS, quiet); err != nil {
			t.Fatal(err)
		}
		if err := postgres.Migrate(ctx, pool, migrations.FS, quiet); err != nil {
			t.Fatalf("second run must be a no-op: %v", err)
		}
		var count int
		if err := pool.QueryRow(ctx, "SELECT count(*) FROM schema_migrations").Scan(&count); err != nil || count != 1 {
			t.Fatalf("schema_migrations: %d %v", count, err)
		}
	})

	t.Run("an unknown applied migration blocks a downgrade", func(t *testing.T) {
		newer := fstest.MapFS{"0001_auth.sql": {Data: []byte("SELECT 1")}}
		if _, err := pool.Exec(ctx, "INSERT INTO schema_migrations (version) VALUES ('9999_future')"); err != nil {
			t.Fatal(err)
		}
		defer pool.Exec(ctx, "DELETE FROM schema_migrations WHERE version = '9999_future'")
		if err := postgres.Migrate(ctx, pool, newer, quiet); err == nil {
			t.Fatal("expected downgrade refusal")
		}
	})

	t.Run("store contract", func(t *testing.T) {
		storetest.Run(t, func(t *testing.T) auth.Store {
			if _, err := pool.Exec(ctx, "TRUNCATE audit_log, sessions, devices, auth_challenges, users RESTART IDENTITY CASCADE"); err != nil {
				t.Fatal(err)
			}
			return postgres.NewAuthStore(pool)
		})
	})

	t.Run("full sign-in flow on PostgreSQL", func(t *testing.T) {
		if _, err := pool.Exec(ctx, "TRUNCATE audit_log, sessions, devices, auth_challenges, users RESTART IDENTITY CASCADE"); err != nil {
			t.Fatal(err)
		}
		mailer := &captureMailer{}
		signer, _ := auth.NewTokenSigner([]byte("an-integration-test-token-key-of-48-bytes-long!!"))
		svc := auth.NewService(postgres.NewAuthStore(pool), mailer, signer, []byte("a-different-integration-test-otp-key-48-bytes!!!"), time.Now, auth.DefaultOptions(), quiet)
		start, err := svc.Start(ctx, "maya@example.com", "203.0.113.5")
		if err != nil {
			t.Fatal(err)
		}
		user, tokens, err := svc.Verify(ctx, start.ChallengeID, "maya@example.com", mailer.code, auth.DeviceInfo{Name: "Laptop", Platform: "windows"})
		if err != nil {
			t.Fatal(err)
		}
		if _, _, err := svc.Verify(ctx, start.ChallengeID, "maya@example.com", mailer.code, auth.DeviceInfo{}); err == nil {
			t.Fatal("replay accepted")
		}
		rotated, err := svc.Refresh(ctx, tokens.RefreshToken)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := svc.Refresh(ctx, tokens.RefreshToken); err != auth.ErrRefreshReused {
			t.Fatalf("reuse: %v", err)
		}
		if _, err := svc.Authenticate(ctx, rotated.AccessToken); err == nil {
			t.Fatal("revoked family still authenticates")
		}
		var stored []byte
		if err := pool.QueryRow(ctx, "SELECT otp_hmac FROM auth_challenges WHERE id = $1", start.ChallengeID).Scan(&stored); err != nil || len(stored) != 32 {
			t.Fatalf("otp_hmac: %d %v", len(stored), err)
		}
		var events int
		if err := pool.QueryRow(ctx, "SELECT count(*) FROM audit_log WHERE actor_user_id = $1 AND event_type IN ('auth.sign_in','auth.refresh_reuse')", user.ID).Scan(&events); err != nil || events != 2 {
			t.Fatalf("audit events: %d %v", events, err)
		}
	})
}

type captureMailer struct{ code string }

func (m *captureMailer) SendSignInCode(_ context.Context, _, code string, _ time.Duration) error {
	m.code = code
	return nil
}
