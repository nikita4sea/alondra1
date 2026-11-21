package appconfig

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func secretFile(t *testing.T, content string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "secret")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

func validEnv(t *testing.T) map[string]string {
	return map[string]string{
		"ALONDRA1_DB_PASSWORD_FILE": secretFile(t, "database-password\n"),
		"ALONDRA1_TOKEN_KEY_FILE":   secretFile(t, strings.Repeat("t", 48)),
		"ALONDRA1_OTP_KEY_FILE":     secretFile(t, strings.Repeat("o", 48)),
	}
}

func load(env map[string]string) (Config, error) {
	return Load(func(name string) (string, bool) { value, ok := env[name]; return value, ok })
}

func TestLoadReadsSecretsFromFilesAndTrimsNewlines(t *testing.T) {
	cfg, err := load(validEnv(t))
	if err != nil {
		t.Fatal(err)
	}
	if cfg.DBPassword != "database-password" {
		t.Fatalf("password = %q", cfg.DBPassword)
	}
	if cfg.HTTPAddr != ":8080" || cfg.DBSSLMode != "require" || cfg.SMTPTLSMode != TLSModeStartTLS {
		t.Fatalf("unexpected defaults: %+v", cfg)
	}
}

func TestLoadRefusesPlainSecretVariables(t *testing.T) {
	env := validEnv(t)
	env["ALONDRA1_DB_PASSWORD"] = "oops"
	_, err := load(env)
	if err == nil || !strings.Contains(err.Error(), "ALONDRA1_DB_PASSWORD is set as a plain environment variable") {
		t.Fatalf("expected refusal, got %v", err)
	}
	if strings.Contains(err.Error(), "oops") {
		t.Fatal("the error message must not echo the secret")
	}
}

func TestLoadRequiresKeysOfSufficientLengthThatDiffer(t *testing.T) {
	env := validEnv(t)
	env["ALONDRA1_TOKEN_KEY_FILE"] = secretFile(t, "short")
	if _, err := load(env); err == nil || !strings.Contains(err.Error(), "at least 32 bytes") {
		t.Fatalf("expected key length error, got %v", err)
	}
	env = validEnv(t)
	env["ALONDRA1_OTP_KEY_FILE"] = env["ALONDRA1_TOKEN_KEY_FILE"]
	if _, err := load(env); err == nil || !strings.Contains(err.Error(), "must be different") {
		t.Fatalf("expected different-keys error, got %v", err)
	}
	env = validEnv(t)
	delete(env, "ALONDRA1_OTP_KEY_FILE")
	if _, err := load(env); err == nil || !strings.Contains(err.Error(), "ALONDRA1_OTP_KEY_FILE is required") {
		t.Fatalf("expected missing key error, got %v", err)
	}
}

func TestPlaintextSMTPOnlyForLocalSink(t *testing.T) {
	env := validEnv(t)
	env["ALONDRA1_MAIL_FROM"] = "Alondra1 <no-reply@example.test>"
	env["ALONDRA1_SMTP_TLS_MODE"] = "none"
	env["ALONDRA1_SMTP_HOST"] = "mailpit"
	if _, err := load(env); err != nil {
		t.Fatalf("mailpit without TLS should be allowed: %v", err)
	}
	env["ALONDRA1_SMTP_HOST"] = "smtp.example.com"
	if _, err := load(env); err == nil || !strings.Contains(err.Error(), "only for the local mailpit sink") {
		t.Fatalf("expected plaintext refusal, got %v", err)
	}
}

func TestSMTPHostNeedsSender(t *testing.T) {
	env := validEnv(t)
	env["ALONDRA1_SMTP_HOST"] = "smtp.example.com"
	if _, err := load(env); err == nil || !strings.Contains(err.Error(), "ALONDRA1_MAIL_FROM is required") {
		t.Fatalf("expected sender error, got %v", err)
	}
}
