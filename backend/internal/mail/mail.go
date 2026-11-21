// Package mail delivers sign-in codes over SMTP. Codes are never logged.
package mail

import (
	"context"
	"crypto/rand"
	"crypto/tls"
	"encoding/hex"
	"errors"
	"fmt"
	"mime"
	"net"
	"net/smtp"
	"strconv"
	"strings"
	"time"

	"alondra1/backend/internal/appconfig"
)

// ErrNotConfigured is returned when no SMTP host is configured.
var ErrNotConfigured = errors.New("email delivery is not configured (ALONDRA1_SMTP_HOST is empty)")

// SMTPSender implements auth.Mailer.
type SMTPSender struct {
	Host     string
	Port     int
	TLSMode  string
	Username string
	Password string
	From     string
	Timeout  time.Duration
	// TLSConfig overrides the default (server name = Host, TLS 1.2+); tests use it to trust a local certificate.
	TLSConfig *tls.Config
}

func FromConfig(cfg appconfig.Config) *SMTPSender {
	return &SMTPSender{
		Host: cfg.SMTPHost, Port: cfg.SMTPPort, TLSMode: cfg.SMTPTLSMode, Username: cfg.SMTPUsername,
		Password: cfg.SMTPPassword, From: cfg.MailFrom, Timeout: 20 * time.Second,
	}
}

func (s *SMTPSender) SendSignInCode(ctx context.Context, to, code string, validFor time.Duration) error {
	if s.Host == "" {
		return ErrNotConfigured
	}
	if containsLineBreak(to) || containsLineBreak(s.From) {
		return errors.New("refusing a header with a line break")
	}
	minutes := int(validFor.Round(time.Minute) / time.Minute)
	body := fmt.Sprintf("Your Alondra1 sign-in code is %s\r\n\r\nIt expires in %d minutes and works once.\r\n\r\nIf you did not try to sign in to Alondra1, you can ignore this email. Nobody can sign in without this code.\r\n", code, minutes)
	return s.send(ctx, to, "Your Alondra1 sign-in code", body)
}

func (s *SMTPSender) send(ctx context.Context, to, subject, body string) error {
	timeout := s.Timeout
	if timeout <= 0 {
		timeout = 20 * time.Second
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	deadline, _ := ctx.Deadline()

	address := net.JoinHostPort(s.Host, strconv.Itoa(s.Port))
	tlsConfig := s.TLSConfig
	if tlsConfig == nil {
		tlsConfig = &tls.Config{ServerName: s.Host, MinVersion: tls.VersionTLS12}
	}
	dialer := &net.Dialer{}
	var conn net.Conn
	var err error
	if s.TLSMode == appconfig.TLSModeTLS {
		conn, err = (&tls.Dialer{NetDialer: dialer, Config: tlsConfig}).DialContext(ctx, "tcp", address)
	} else {
		conn, err = dialer.DialContext(ctx, "tcp", address)
	}
	if err != nil {
		return fmt.Errorf("connect to SMTP server: %w", err)
	}
	_ = conn.SetDeadline(deadline)
	client, err := smtp.NewClient(conn, s.Host)
	if err != nil {
		conn.Close()
		return fmt.Errorf("SMTP greeting: %w", err)
	}
	defer client.Close()

	if s.TLSMode == appconfig.TLSModeStartTLS {
		if ok, _ := client.Extension("STARTTLS"); !ok {
			return errors.New("the SMTP server does not offer STARTTLS; refusing to send unencrypted")
		}
		if err := client.StartTLS(tlsConfig); err != nil {
			return fmt.Errorf("STARTTLS: %w", err)
		}
	}
	if s.Username != "" {
		if err := client.Auth(smtp.PlainAuth("", s.Username, s.Password, s.Host)); err != nil {
			return fmt.Errorf("SMTP authentication: %w", err)
		}
	}
	fromAddress := s.From
	if parsed, err := parseAddress(s.From); err == nil {
		fromAddress = parsed
	}
	if err := client.Mail(fromAddress); err != nil {
		return fmt.Errorf("MAIL FROM: %w", err)
	}
	if err := client.Rcpt(to); err != nil {
		return fmt.Errorf("RCPT TO: %w", err)
	}
	writer, err := client.Data()
	if err != nil {
		return fmt.Errorf("DATA: %w", err)
	}
	if _, err := writer.Write([]byte(message(s.From, to, subject, body, fromAddress))); err != nil {
		return fmt.Errorf("write message: %w", err)
	}
	if err := writer.Close(); err != nil {
		return fmt.Errorf("end of message: %w", err)
	}
	return client.Quit()
}

func message(from, to, subject, body, fromAddress string) string {
	domain := "alondra1.invalid"
	if at := strings.LastIndexByte(fromAddress, '@'); at >= 0 {
		domain = fromAddress[at+1:]
	}
	random := make([]byte, 12)
	_, _ = rand.Read(random)
	headers := []string{
		"From: " + from,
		"To: " + to,
		"Subject: " + mime.QEncoding.Encode("utf-8", subject),
		"Date: " + time.Now().UTC().Format(time.RFC1123Z),
		"Message-ID: <" + hex.EncodeToString(random) + "@" + domain + ">",
		"MIME-Version: 1.0",
		"Content-Type: text/plain; charset=utf-8",
		"Content-Transfer-Encoding: 8bit",
		"Auto-Submitted: auto-generated",
	}
	return strings.Join(headers, "\r\n") + "\r\n\r\n" + body
}

func parseAddress(value string) (string, error) {
	start, end := strings.LastIndexByte(value, '<'), strings.LastIndexByte(value, '>')
	if start >= 0 && end > start {
		return value[start+1 : end], nil
	}
	if strings.Contains(value, "@") {
		return strings.TrimSpace(value), nil
	}
	return "", errors.New("no address")
}

func containsLineBreak(value string) bool { return strings.ContainsAny(value, "\r\n") }
