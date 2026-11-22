package postgres

import (
	"crypto/tls"
	"strings"
	"testing"

	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/appconfig"
)

func TestConnStringEnforcesTheConfiguredSSLMode(t *testing.T) {
	base := appconfig.Config{DBHost: "db.internal", DBPort: 5432, DBName: "alondra1", DBUser: "o'brien", DBPassword: "secret"}

	require := base
	require.DBSSLMode = "require"
	cfg, err := pgxpool.ParseConfig(ConnString(require))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ConnConfig.TLSConfig == nil || len(cfg.ConnConfig.Fallbacks) != 0 {
		t.Fatalf("require must use TLS with no plaintext fallback: tls=%v fallbacks=%d", cfg.ConnConfig.TLSConfig != nil, len(cfg.ConnConfig.Fallbacks))
	}
	if cfg.ConnConfig.User != "o'brien" || cfg.ConnConfig.Host != "db.internal" {
		t.Fatalf("values not quoted correctly: %q %q", cfg.ConnConfig.User, cfg.ConnConfig.Host)
	}

	full := base
	full.DBSSLMode = "verify-full"
	cfg, err = pgxpool.ParseConfig(ConnString(full))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ConnConfig.TLSConfig == nil || cfg.ConnConfig.TLSConfig.ServerName != "db.internal" || cfg.ConnConfig.TLSConfig.InsecureSkipVerify && cfg.ConnConfig.TLSConfig.VerifyPeerCertificate == nil {
		t.Fatalf("verify-full must verify the server name: %+v", cfg.ConnConfig.TLSConfig)
	}

	disable := base
	disable.DBSSLMode = "disable"
	cfg, err = pgxpool.ParseConfig(ConnString(disable))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ConnConfig.TLSConfig != (*tls.Config)(nil) {
		t.Fatal("disable must not use TLS")
	}
	if strings.Contains(ConnString(require), "secret") {
		t.Fatal("the connection string must not contain the password")
	}
}
