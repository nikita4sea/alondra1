//go:build integration

// Integration tests against a real PostgreSQL. Run with:
//
//	go test -tags integration ./...
package postgres_test

import (
	"context"
	"io/fs"
	"testing"
	"testing/fstest"
	"time"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/auth/storetest"
	"alondra1/backend/internal/postgres"
	"alondra1/backend/internal/testdb"
	"alondra1/backend/migrations"
)

func TestPostgres(t *testing.T) {
	pool := testdb.Start(t, false)
	ctx := context.Background()
	quiet := testdb.Quiet

	t.Run("migrations apply once and are idempotent", func(t *testing.T) {
		if err := postgres.Migrate(ctx, pool, migrations.FS, quiet); err != nil {
			t.Fatal(err)
		}
		if err := postgres.Migrate(ctx, pool, migrations.FS, quiet); err != nil {
			t.Fatalf("second run must be a no-op: %v", err)
		}
		files, _ := fs.Glob(migrations.FS, "*.sql")
		var count int
		if err := pool.QueryRow(ctx, "SELECT count(*) FROM schema_migrations").Scan(&count); err != nil || count != len(files) {
			t.Fatalf("schema_migrations: %d %v", count, err)
		}
	})

	t.Run("an unknown applied migration blocks a downgrade", func(t *testing.T) {
		older := fstest.MapFS{}
		files, _ := fs.Glob(migrations.FS, "*.sql")
		for _, name := range files {
			older[name] = &fstest.MapFile{Data: []byte("SELECT 1")}
		}
		if _, err := pool.Exec(ctx, "INSERT INTO schema_migrations (version) VALUES ('9999_future')"); err != nil {
			t.Fatal(err)
		}
		defer pool.Exec(ctx, "DELETE FROM schema_migrations WHERE version = '9999_future'")
		if err := postgres.Migrate(ctx, pool, older, quiet); err == nil {
			t.Fatal("expected downgrade refusal")
		}
	})

	t.Run("store contract", func(t *testing.T) {
		storetest.Run(t, func(t *testing.T) auth.Store {
			testdb.Truncate(t, pool)
			return postgres.NewAuthStore(pool)
		})
	})

	t.Run("full sign-in flow on PostgreSQL", func(t *testing.T) {
		testdb.Truncate(t, pool)
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
