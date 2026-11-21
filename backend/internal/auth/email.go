package auth

import (
	"errors"
	"net/mail"
	"strings"
	"unicode"
)

// ErrInvalidEmail is returned for an address that cannot receive a sign-in code.
var ErrInvalidEmail = errors.New("invalid email address")

const maxEmailLength = 254

// NormalizeEmail trims and lower-cases an address and checks that it is a single plain address (no display name, no
// list, no quoted local part tricks). Provider-specific rewriting (Gmail dots, plus tags) is deliberately not done:
// those are distinct mailboxes as far as Alondra1 can prove.
func NormalizeEmail(raw string) (string, error) {
	email := strings.ToLower(strings.TrimSpace(raw))
	if email == "" || len(email) > maxEmailLength {
		return "", ErrInvalidEmail
	}
	for _, r := range email {
		if unicode.IsSpace(r) || unicode.IsControl(r) || r == '<' || r == '>' || r == ',' || r == '"' {
			return "", ErrInvalidEmail
		}
	}
	address, err := mail.ParseAddress(email)
	if err != nil || address.Name != "" || address.Address != email {
		return "", ErrInvalidEmail
	}
	at := strings.LastIndexByte(email, '@')
	if at <= 0 || at != strings.IndexByte(email, '@') || at == len(email)-1 {
		return "", ErrInvalidEmail
	}
	domain := email[at+1:]
	if !strings.Contains(domain, ".") || strings.HasPrefix(domain, ".") || strings.HasSuffix(domain, ".") || strings.Contains(domain, "..") {
		return "", ErrInvalidEmail
	}
	return email, nil
}

// RedactEmail keeps enough of an address for operators to correlate log lines without logging it: "m***@example.com".
func RedactEmail(email string) string {
	at := strings.LastIndexByte(email, '@')
	if at <= 0 {
		return "***"
	}
	return email[:1] + "***" + email[at:]
}
