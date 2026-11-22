// Command api is the Alondra1 cloud API: email one-time-code sign-in, spaces, tasks, portable settings, and sync.
// Configuration: docs/runbooks/compose.md. Operations: docs/runbooks/production.md.
//
//	api              serve (applies migrations first)
//	api maintenance  run the retention job once and exit (for an external scheduler)
//	api healthcheck  exit 0 if this container's API is ready (the image has no curl)
package main

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/appconfig"
	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/cloudsync"
	"alondra1/backend/internal/httpapi"
	"alondra1/backend/internal/mail"
	"alondra1/backend/internal/maintenance"
	"alondra1/backend/internal/metrics"
	"alondra1/backend/internal/postgres"
	"alondra1/backend/internal/settings"
	"alondra1/backend/internal/tasks"
	"alondra1/backend/internal/tenancy"
	"alondra1/backend/migrations"
)

// version is set at build time: -ldflags "-X main.version=1.0.0".
var version = "dev"

func main() {
	log := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})).With("version", version)
	command := ""
	if len(os.Args) > 1 {
		command = os.Args[1]
	}
	var err error
	switch command {
	case "":
		err = serve(log)
	case "maintenance":
		err = maintainOnce(log)
	case "healthcheck":
		err = healthcheck()
	default:
		err = errors.New("unknown command " + command + ` (use no argument to serve, or "maintenance")`)
	}
	if err != nil {
		log.Error("api stopped", "error", err.Error())
		os.Exit(1)
	}
}

func connect(ctx context.Context, cfg appconfig.Config, log *slog.Logger) (*pgxpool.Pool, error) {
	startup, cancel := context.WithTimeout(ctx, 2*time.Minute)
	defer cancel()
	pool, err := postgres.Connect(startup, cfg, log)
	if err != nil {
		return nil, err
	}
	// Schema changes are applied before any traffic is served or any maintenance runs.
	if err := postgres.Migrate(ctx, pool, migrations.FS, log); err != nil {
		pool.Close()
		return nil, err
	}
	return pool, nil
}

// healthcheck asks the local API for readiness; it needs no secrets.
func healthcheck() error {
	addr := os.Getenv("ALONDRA1_HTTP_ADDR")
	if addr == "" || strings.HasPrefix(addr, ":") {
		addr = "127.0.0.1" + addr
		if addr == "127.0.0.1" {
			addr += ":8080"
		}
	}
	client := &http.Client{Timeout: 3 * time.Second}
	res, err := client.Get("http://" + addr + "/v1/health/ready")
	if err != nil {
		return err
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		return errors.New("not ready: " + res.Status)
	}
	return nil
}

func maintainOnce(log *slog.Logger) error {
	cfg, err := appconfig.Load(os.LookupEnv)
	if err != nil {
		return err
	}
	ctx := context.Background()
	pool, err := connect(ctx, cfg, log)
	if err != nil {
		return err
	}
	defer pool.Close()
	_, err = maintenance.Run(ctx, pool, maintenance.DefaultPolicy(), time.Now().UTC(), log)
	if errors.Is(err, maintenance.ErrBusy) {
		log.Info("maintenance already running elsewhere")
		return nil
	}
	return err
}

func serve(log *slog.Logger) error {
	cfg, err := appconfig.Load(os.LookupEnv)
	if err != nil {
		return err
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	pool, err := connect(ctx, cfg, log)
	if err != nil {
		return err
	}
	defer pool.Close()

	signer, err := auth.NewTokenSigner(cfg.TokenKey)
	if err != nil {
		return err
	}
	signer.WithPrevious(cfg.PreviousTokenKey)
	options := auth.DefaultOptions()
	options.MaxStartsPerIPHour = cfg.OTPPerIPPerHour
	options.GlobalPerMinute = cfg.OTPGlobalPerMinute
	if cfg.SMTPHost == "" {
		log.Warn("ALONDRA1_SMTP_HOST is empty: sign-in codes cannot be sent")
	}
	registry := metrics.New()
	describe(registry, pool, cfg)

	mailer := mail.FromConfig(cfg)
	api := &httpapi.Server{
		Auth:              auth.NewService(postgres.NewAuthStore(pool), mailer, signer, cfg.OTPKey, time.Now, options, log),
		Tenancy:           tenancy.NewService(postgres.NewTenancyStore(pool), mailer, time.Now, log),
		Tasks:             tasks.NewService(postgres.NewTaskStore(pool), time.Now),
		Settings:          settings.NewService(postgres.NewSettingsStore(pool), time.Now),
		Sync:              cloudsync.NewService(postgres.NewSyncStore(pool), cfg.TokenKey, cfg.PreviousTokenKey),
		Ready:             pool.Ping,
		Log:               log,
		TrustProxyHeaders: cfg.TrustProxyHeaders,
		Metrics:           registry,
		Version:           version,
	}
	server := &http.Server{
		Addr:              cfg.HTTPAddr,
		Handler:           api.Handler(),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       30 * time.Second,
		WriteTimeout:      60 * time.Second,
		IdleTimeout:       120 * time.Second,
		MaxHeaderBytes:    32 << 10,
	}
	errs := make(chan error, 2)
	go func() {
		log.Info("listening", "addr", cfg.HTTPAddr)
		errs <- server.ListenAndServe()
	}()
	var metricsServer *http.Server
	if cfg.MetricsAddr != "off" {
		mux := http.NewServeMux()
		mux.Handle("GET /metrics", registry.Handler())
		metricsServer = &http.Server{Addr: cfg.MetricsAddr, Handler: mux, ReadHeaderTimeout: 5 * time.Second}
		go func() {
			log.Info("metrics listening", "addr", cfg.MetricsAddr)
			if err := metricsServer.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
				errs <- err
			}
		}()
	}
	if cfg.MaintenanceInterval > 0 {
		go maintainPeriodically(ctx, pool, cfg.MaintenanceInterval, registry, log)
	}

	select {
	case err := <-errs:
		if !errors.Is(err, http.ErrServerClosed) {
			return err
		}
		return nil
	case <-ctx.Done():
	}
	// Fail readiness first so a load balancer stops routing here, then finish in-flight requests.
	log.Info("draining")
	api.Draining.Store(true)
	time.Sleep(5 * time.Second)
	shutdown, cancel := context.WithTimeout(context.Background(), 25*time.Second)
	defer cancel()
	if metricsServer != nil {
		_ = metricsServer.Shutdown(shutdown)
	}
	return server.Shutdown(shutdown)
}

func maintainPeriodically(ctx context.Context, pool *pgxpool.Pool, interval time.Duration, registry *metrics.Registry, log *slog.Logger) {
	run := func() {
		started := time.Now()
		result, err := maintenance.Run(ctx, pool, maintenance.DefaultPolicy(), started.UTC(), log)
		switch {
		case errors.Is(err, maintenance.ErrBusy):
			return
		case err != nil:
			registry.Inc("alondra1_maintenance_runs_total", "result", "error")
			log.Error("maintenance failed", "error", err.Error())
			return
		}
		registry.Inc("alondra1_maintenance_runs_total", "result", "ok")
		registry.Set("alondra1_maintenance_last_success_timestamp_seconds", float64(time.Now().Unix()))
		for step, n := range result {
			registry.Add("alondra1_maintenance_deleted_rows_total", float64(n), "step", step)
		}
	}
	// First run shortly after start, then on the interval.
	timer := time.NewTimer(time.Minute)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
			run()
			timer.Reset(interval)
		}
	}
}

// describe registers help texts and the scrape-time gauges (database health, backup status, build info).
func describe(r *metrics.Registry, pool *pgxpool.Pool, cfg appconfig.Config) {
	for name, help := range map[string]string{
		"alondra1_http_requests_total":                         "HTTP requests by route pattern and status class.",
		"alondra1_http_request_duration_seconds":               "HTTP request latency by route pattern.",
		"alondra1_auth_code_requests_total":                    "Sign-in code requests by result.",
		"alondra1_auth_code_verifications_total":               "Sign-in code verifications by result.",
		"alondra1_auth_refreshes_total":                        "Refresh-token rotations by result.",
		"alondra1_sync_push_mutations_total":                   "Pushed sync mutations by status (applied, conflict, rejected).",
		"alondra1_db_up":                                       "1 if the database answered the last scrape-time ping.",
		"alondra1_maintenance_runs_total":                      "Retention job runs by result.",
		"alondra1_maintenance_last_success_timestamp_seconds":  "Unix time of the last successful retention run.",
		"alondra1_maintenance_deleted_rows_total":              "Rows deleted by the retention job, by step.",
		"alondra1_backup_last_success_timestamp_seconds":       "Unix time of the last successful encrypted backup.",
		"alondra1_backup_last_restore_check_timestamp_seconds": "Unix time of the last successful restore check of a backup.",
		"alondra1_build_info":                                  "Always 1; the version label is the API version.",
	} {
		r.Help(name, help)
	}
	r.Set("alondra1_build_info", 1, "version", version)
	r.OnScrape(func(r *metrics.Registry) {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		up := 0.0
		if pool.Ping(ctx) == nil {
			up = 1
		}
		r.Set("alondra1_db_up", up)
		if cfg.BackupStatusFile == "" {
			return
		}
		data, err := os.ReadFile(cfg.BackupStatusFile)
		if err != nil {
			return
		}
		var status struct {
			LastSuccess      int64 `json:"last_success_unix"`
			LastRestoreCheck int64 `json:"last_restore_check_unix"`
		}
		if json.Unmarshal(data, &status) == nil {
			r.Set("alondra1_backup_last_success_timestamp_seconds", float64(status.LastSuccess))
			r.Set("alondra1_backup_last_restore_check_timestamp_seconds", float64(status.LastRestoreCheck))
		}
	})
}
