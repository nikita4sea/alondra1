package mail

import (
	"bufio"
	"context"
	"errors"
	"net"
	"strings"
	"testing"
	"time"

	"alondra1/backend/internal/appconfig"
)

// fakeSMTP is a minimal SMTP server that records one message. It offers STARTTLS only when asked to, so tests can
// prove the sender refuses to fall back to plaintext.
type fakeSMTP struct {
	listener   net.Listener
	offerTLS   bool
	received   chan string
	rcpt, from chan string
}

func startFakeSMTP(t *testing.T, offerTLS bool) *fakeSMTP {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	f := &fakeSMTP{listener: listener, offerTLS: offerTLS, received: make(chan string, 1), rcpt: make(chan string, 1), from: make(chan string, 1)}
	t.Cleanup(func() { listener.Close() })
	go f.serve()
	return f
}

func (f *fakeSMTP) port() int { return f.listener.Addr().(*net.TCPAddr).Port }

func (f *fakeSMTP) serve() {
	conn, err := f.listener.Accept()
	if err != nil {
		return
	}
	defer conn.Close()
	reader := bufio.NewReader(conn)
	write := func(line string) { conn.Write([]byte(line + "\r\n")) }
	write("220 fake ESMTP")
	for {
		line, err := reader.ReadString('\n')
		if err != nil {
			return
		}
		command := strings.ToUpper(strings.TrimSpace(line))
		switch {
		case strings.HasPrefix(command, "EHLO"):
			if f.offerTLS {
				write("250-fake")
				write("250 STARTTLS")
			} else {
				write("250 fake")
			}
		case strings.HasPrefix(command, "MAIL FROM:"):
			f.from <- strings.TrimSpace(line)
			write("250 ok")
		case strings.HasPrefix(command, "RCPT TO:"):
			f.rcpt <- strings.TrimSpace(line)
			write("250 ok")
		case command == "DATA":
			write("354 go ahead")
			var message strings.Builder
			for {
				data, err := reader.ReadString('\n')
				if err != nil {
					return
				}
				if data == ".\r\n" {
					break
				}
				message.WriteString(data)
			}
			f.received <- message.String()
			write("250 queued")
		case command == "QUIT":
			write("221 bye")
			return
		default:
			write("502 not implemented")
		}
	}
}

func TestSendsAPlainTextCodeToTheLocalSink(t *testing.T) {
	server := startFakeSMTP(t, false)
	sender := &SMTPSender{Host: "127.0.0.1", Port: server.port(), TLSMode: appconfig.TLSModeNone, From: "Alondra1 <no-reply@example.test>", Timeout: 5 * time.Second}
	if err := sender.SendSignInCode(context.Background(), "maya@example.com", "042917", 10*time.Minute); err != nil {
		t.Fatal(err)
	}
	if from := <-server.from; from != "MAIL FROM:<no-reply@example.test>" {
		t.Fatalf("from: %s", from)
	}
	if rcpt := <-server.rcpt; rcpt != "RCPT TO:<maya@example.com>" {
		t.Fatalf("rcpt: %s", rcpt)
	}
	message := <-server.received
	for _, want := range []string{"From: Alondra1 <no-reply@example.test>\r\n", "To: maya@example.com\r\n", "Subject: Your Alondra1 sign-in code\r\n", "Content-Type: text/plain; charset=utf-8\r\n", "Message-ID: <", "@example.test>\r\n", "Your Alondra1 sign-in code is 042917", "expires in 10 minutes"} {
		if !strings.Contains(message, want) {
			t.Errorf("message lacks %q:\n%s", want, message)
		}
	}
}

func TestStartTLSModeRefusesAServerWithoutStartTLS(t *testing.T) {
	server := startFakeSMTP(t, false)
	sender := &SMTPSender{Host: "127.0.0.1", Port: server.port(), TLSMode: appconfig.TLSModeStartTLS, From: "no-reply@example.test", Timeout: 5 * time.Second}
	err := sender.SendSignInCode(context.Background(), "maya@example.com", "042917", 10*time.Minute)
	if err == nil || !strings.Contains(err.Error(), "does not offer STARTTLS") {
		t.Fatalf("expected refusal, got %v", err)
	}
	select {
	case <-server.received:
		t.Fatal("nothing may be sent without TLS")
	default:
	}
}

func TestRefusesHeaderInjectionAndMissingHost(t *testing.T) {
	sender := &SMTPSender{Host: "127.0.0.1", Port: 1, TLSMode: appconfig.TLSModeNone, From: "no-reply@example.test"}
	if err := sender.SendSignInCode(context.Background(), "a@example.com\r\nBcc: x@example.com", "000000", time.Minute); err == nil {
		t.Fatal("header injection accepted")
	}
	if err := (&SMTPSender{}).SendSignInCode(context.Background(), "a@example.com", "000000", time.Minute); !errors.Is(err, ErrNotConfigured) {
		t.Fatalf("expected ErrNotConfigured, got %v", err)
	}
}
