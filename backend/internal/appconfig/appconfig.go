// Package appconfig reads the API's runtime configuration from environment variables and secret files.
//
// Secrets are read only from files named by *_FILE variables (Docker Compose secrets). A secret passed as a plain
// environment variable is refused, so it cannot leak through `docker inspect`, process listings, or logs.
package appconfig

import (
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
)

// SMTP TLS modes.
const (
	TLSModeStartTLS = "starttls"
	TLSModeTLS      = "tls"
	TLSModeNone     = "none"
)

// MinKeyBytes is the minimum length of the signing and HMAC keys.
const MinKeyBytes = 32

type Config struct {
	HTTPAddr string

	DBHost     string
	DBPort     int
	DBName     string
	DBUser     string
	DBPassword string
	DBSSLMode  string

	// TokenKey signs access tokens. OTPKey keys the one-time-code and IP hashes.
	TokenKey []byte
	OTPKey   []byte

	MailFrom     string
	SMTPHost     string
	SMTPPort     int
	SMTPTLSMode  string
	SMTPUsername string
	SMTPPassword string

	TrustProxyHeaders bool

	// Tunable abuse limits for sign-in code requests.
	OTPPerIPPerHour     int
	OTPGlobalPerMinute  int
}

// plainSecretVariables must never be set; their *_FILE counterparts are used instead.
var plainSecretVariables = []string{
	"ALONDRA1_DB_PASSWORD", "ALONDRA1_TOKEN_KEY", "ALONDRA1_OTP_KEY", "ALONDRA1_SMTP_PASSWORD", "POSTGRES_PASSWORD",
}

// Load reads the configuration. lookup is os.LookupEnv in production; tests pass a map.
func Load(lookup func(string) (string, bool)) (Config, error) {
	var problems []string
	for _, name := range plainSecretVariables {
		if value, ok := lookup(name); ok && value != "" {
			problems = append(problems, name+" is set as a plain environment variable; use "+name+"_FILE with a secret file instead")
		}
	}

	get := func(name, fallback string) string {
		if value, ok := lookup(name); ok && strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
		return fallback
	}
	getInt := func(name string, fallback int) int {
		raw := get(name, "")
		if raw == "" {
			return fallback
		}
		value, err := strconv.Atoi(raw)
		if err != nil || value <= 0 {
			problems = append(problems, fmt.Sprintf("%s must be a positive integer", name))
			return fallback
		}
		return value
	}
	readSecret := func(name string, required bool) string {
		path := get(name, "")
		if path == "" {
			if required {
				problems = append(problems, name+" is required")
			}
			return ""
		}
		data, err := os.ReadFile(path)
		if err != nil {
			problems = append(problems, fmt.Sprintf("%s: cannot read the secret file (%v)", name, errors.Unwrap(err)))
			return ""
		}
		// Trailing newlines from editors and `echo` are not part of the secret.
		return strings.TrimRight(string(data), "\r\n")
	}

	cfg := Config{
		HTTPAddr:           get("ALONDRA1_HTTP_ADDR", ":8080"),
		DBHost:             get("ALONDRA1_DB_HOST", "db"),
		DBPort:             getInt("ALONDRA1_DB_PORT", 5432),
		DBName:             get("ALONDRA1_DB_NAME", "alondra1"),
		DBUser:             get("ALONDRA1_DB_USER", "alondra1"),
		DBSSLMode:          get("ALONDRA1_DB_SSLMODE", "require"),
		MailFrom:           get("ALONDRA1_MAIL_FROM", ""),
		SMTPHost:           get("ALONDRA1_SMTP_HOST", ""),
		SMTPPort:           getInt("ALONDRA1_SMTP_PORT", 587),
		SMTPTLSMode:        strings.ToLower(get("ALONDRA1_SMTP_TLS_MODE", TLSModeStartTLS)),
		SMTPUsername:       get("ALONDRA1_SMTP_USERNAME", ""),
		TrustProxyHeaders:  strings.EqualFold(get("ALONDRA1_TRUST_PROXY_HEADERS", "false"), "true"),
		OTPPerIPPerHour:    getInt("ALONDRA1_OTP_PER_IP_PER_HOUR", 30),
		OTPGlobalPerMinute: getInt("ALONDRA1_OTP_GLOBAL_PER_MINUTE", 300),
	}
	cfg.DBPassword = readSecret("ALONDRA1_DB_PASSWORD_FILE", true)
	cfg.TokenKey = []byte(readSecret("ALONDRA1_TOKEN_KEY_FILE", true))
	cfg.OTPKey = []byte(readSecret("ALONDRA1_OTP_KEY_FILE", true))
	cfg.SMTPPassword = readSecret("ALONDRA1_SMTP_PASSWORD_FILE", false)

	if cfg.DBPassword == "" && !contains(problems, "ALONDRA1_DB_PASSWORD_FILE") {
		problems = append(problems, "the database password file is empty")
	}
	for name, key := range map[string][]byte{"ALONDRA1_TOKEN_KEY_FILE": cfg.TokenKey, "ALONDRA1_OTP_KEY_FILE": cfg.OTPKey} {
		if len(key) > 0 && len(key) < MinKeyBytes {
			problems = append(problems, fmt.Sprintf("%s must contain at least %d bytes", name, MinKeyBytes))
		}
	}
	if len(cfg.TokenKey) >= MinKeyBytes && string(cfg.TokenKey) == string(cfg.OTPKey) {
		problems = append(problems, "the token key and the OTP key must be different")
	}
	switch cfg.SMTPTLSMode {
	case TLSModeStartTLS, TLSModeTLS:
	case TLSModeNone:
		// Unencrypted SMTP is only for the local development sink (spec §12).
		if cfg.SMTPHost != "" && !IsLocalMailSink(cfg.SMTPHost) {
			problems = append(problems, "ALONDRA1_SMTP_TLS_MODE=none is allowed only for the local mailpit sink (host mailpit, localhost, or 127.0.0.1)")
		}
	default:
		problems = append(problems, "ALONDRA1_SMTP_TLS_MODE must be starttls, tls, or none")
	}
	if cfg.SMTPHost != "" && cfg.MailFrom == "" {
		problems = append(problems, "ALONDRA1_MAIL_FROM is required when ALONDRA1_SMTP_HOST is set")
	}

	if len(problems) > 0 {
		return Config{}, errors.New("invalid configuration:\n  - " + strings.Join(problems, "\n  - "))
	}
	return cfg, nil
}

// IsLocalMailSink reports whether host is the development mail sink.
func IsLocalMailSink(host string) bool {
	switch strings.ToLower(host) {
	case "mailpit", "localhost", "127.0.0.1", "::1":
		return true
	}
	return false
}

func contains(items []string, fragment string) bool {
	for _, item := range items {
		if strings.Contains(item, fragment) {
			return true
		}
	}
	return false
}
