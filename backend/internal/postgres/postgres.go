// Package postgres connects to PostgreSQL, applies migrations, and implements the stores.
package postgres

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"math/rand/v2"
	"sort"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/appconfig"
)

// Connect opens a pool and waits for the database with exponential backoff and jitter, because Compose's
// depends_on does not guarantee the database stays reachable after it first reports healthy.
func Connect(ctx context.Context, cfg appconfig.Config, log *slog.Logger) (*pgxpool.Pool, error) {
	poolConfig, err := pgxpool.ParseConfig("")
	if err != nil {
		return nil, err
	}
	poolConfig.ConnConfig.Host = cfg.DBHost
	poolConfig.ConnConfig.Port = uint16(cfg.DBPort)
	poolConfig.ConnConfig.Database = cfg.DBName
	poolConfig.ConnConfig.User = cfg.DBUser
	poolConfig.ConnConfig.Password = cfg.DBPassword
	if cfg.DBSSLMode == "disable" {
		poolConfig.ConnConfig.TLSConfig = nil
		poolConfig.ConnConfig.Fallbacks = nil
	}
	poolConfig.MaxConns = 20
	return ConnectWithConfig(ctx, poolConfig, log)
}

func ConnectWithConfig(ctx context.Context, poolConfig *pgxpool.Config, log *slog.Logger) (*pgxpool.Pool, error) {
	pool, err := pgxpool.NewWithConfig(ctx, poolConfig)
	if err != nil {
		return nil, err
	}
	delay := 500 * time.Millisecond
	for attempt := 1; ; attempt++ {
		pingCtx, cancel := context.WithTimeout(ctx, 5*time.Second)
		err = pool.Ping(pingCtx)
		cancel()
		if err == nil {
			return pool, nil
		}
		if log != nil {
			log.Warn("database not reachable yet", "attempt", attempt, "retry_in", delay.String(), "error", err.Error())
		}
		jitter := time.Duration(rand.Int64N(int64(delay / 2)))
		select {
		case <-ctx.Done():
			pool.Close()
			return nil, fmt.Errorf("database not reachable: %w", err)
		case <-time.After(delay + jitter):
		}
		delay = min(delay*2, 10*time.Second)
	}
}

// migrationLockID is an arbitrary constant for pg_advisory_lock, so two API instances never migrate concurrently.
const migrationLockID = 7_351_204_611

// Migrate applies every *.sql file in files that is not yet recorded, in name order, each in its own transaction.
func Migrate(ctx context.Context, pool *pgxpool.Pool, files fs.FS, log *slog.Logger) error {
	conn, err := pool.Acquire(ctx)
	if err != nil {
		return err
	}
	defer conn.Release()
	if _, err := conn.Exec(ctx, "SELECT pg_advisory_lock($1)", migrationLockID); err != nil {
		return err
	}
	defer conn.Exec(context.Background(), "SELECT pg_advisory_unlock($1)", migrationLockID)

	if _, err := conn.Exec(ctx, `CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())`); err != nil {
		return err
	}
	names, err := fs.Glob(files, "*.sql")
	if err != nil {
		return err
	}
	sort.Strings(names)
	applied := map[string]bool{}
	rows, err := conn.Query(ctx, "SELECT version FROM schema_migrations")
	if err != nil {
		return err
	}
	for rows.Next() {
		var version string
		if err := rows.Scan(&version); err != nil {
			rows.Close()
			return err
		}
		applied[version] = true
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return err
	}
	// Refuse to run against a database migrated by a newer API (downgrade protection).
	known := map[string]bool{}
	for _, name := range names {
		known[strings.TrimSuffix(name, ".sql")] = true
	}
	for version := range applied {
		if !known[version] {
			return fmt.Errorf("the database has migration %q that this API version does not know; refusing to start (downgrade?)", version)
		}
	}

	for _, name := range names {
		version := strings.TrimSuffix(name, ".sql")
		if applied[version] {
			continue
		}
		sql, err := fs.ReadFile(files, name)
		if err != nil {
			return err
		}
		err = pgx.BeginFunc(ctx, conn, func(tx pgx.Tx) error {
			if _, err := tx.Exec(ctx, string(sql)); err != nil {
				return err
			}
			_, err := tx.Exec(ctx, "INSERT INTO schema_migrations (version) VALUES ($1)", version)
			return err
		})
		if err != nil {
			return fmt.Errorf("migration %s: %w", name, err)
		}
		if log != nil {
			log.Info("migration applied", "version", version)
		}
	}
	return nil
}

func isNoRows(err error) bool { return errors.Is(err, pgx.ErrNoRows) }
