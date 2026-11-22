// Command api is the Alondra1 cloud API: email one-time-code sign-in and (in later milestones) spaces, tasks,
// portable settings, and sync. Configuration: docs/runbooks/compose.md.
package main

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"alondra1/backend/internal/appconfig"
	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/httpapi"
	"alondra1/backend/internal/mail"
	"alondra1/backend/internal/postgres"
	"alondra1/backend/internal/tenancy"
	"alondra1/backend/migrations"
)

func main() {
	log := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	if err := run(log); err != nil {
		log.Error("api stopped", "error", err.Error())
		os.Exit(1)
	}
}

func run(log *slog.Logger) error {
	cfg, err := appconfig.Load(os.LookupEnv)
	if err != nil {
		return err
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	startup, cancel := context.WithTimeout(ctx, 2*time.Minute)
	pool, err := postgres.Connect(startup, cfg, log)
	cancel()
	if err != nil {
		return err
	}
	defer pool.Close()
	// Schema changes are applied before any traffic is served.
	if err := postgres.Migrate(ctx, pool, migrations.FS, log); err != nil {
		return err
	}

	signer, err := auth.NewTokenSigner(cfg.TokenKey)
	if err != nil {
		return err
	}
	options := auth.DefaultOptions()
	options.MaxStartsPerIPHour = cfg.OTPPerIPPerHour
	options.GlobalPerMinute = cfg.OTPGlobalPerMinute
	if cfg.SMTPHost == "" {
		log.Warn("ALONDRA1_SMTP_HOST is empty: sign-in codes cannot be sent")
	}
	mailer := mail.FromConfig(cfg)
	service := auth.NewService(postgres.NewAuthStore(pool), mailer, signer, cfg.OTPKey, time.Now, options, log)
	spaces := tenancy.NewService(postgres.NewTenancyStore(pool), mailer, time.Now, log)
	api := &httpapi.Server{Auth: service, Tenancy: spaces, Ready: pool.Ping, Log: log, TrustProxyHeaders: cfg.TrustProxyHeaders}

	server := &http.Server{
		Addr:              cfg.HTTPAddr,
		Handler:           api.Handler(),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       120 * time.Second,
		MaxHeaderBytes:    32 << 10,
	}
	errs := make(chan error, 1)
	go func() {
		log.Info("listening", "addr", cfg.HTTPAddr)
		errs <- server.ListenAndServe()
	}()
	select {
	case err := <-errs:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
	}
	log.Info("shutting down")
	shutdown, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	return server.Shutdown(shutdown)
}
