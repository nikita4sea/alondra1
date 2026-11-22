# Runbook: monitoring and alerts

The API serves Prometheus metrics on `api:9090/metrics`, on the internal network only. The `monitoring` profile runs Prometheus with the rules in `deploy/monitoring/alerts.yml`; its UI is on `127.0.0.1:9091` (use `ssh -L 9091:127.0.0.1:9091 host`). To get notified, point `alerting:` in `deploy/monitoring/prometheus.yml` at your Alertmanager, Grafana alerting, or another receiver.

## Metrics

| Metric | Meaning |
| --- | --- |
| `alondra1_http_requests_total{route, code}` | Requests by route pattern and status class (`2xx` … `5xx`). |
| `alondra1_http_request_duration_seconds{route}` | Latency histogram. |
| `alondra1_auth_code_requests_total{result}` | Sign-in codes: `sent`, `rate_limited`, `invalid_email`, `delivery_failed`. |
| `alondra1_auth_code_verifications_total{result}` | `signed_in`, `invalid_code`. |
| `alondra1_auth_refreshes_total{result}` | `rotated`, `invalid`, `reuse_detected`. |
| `alondra1_sync_push_mutations_total{status}` | `applied`, `conflict`, `rejected`. |
| `alondra1_db_up` | 1 if PostgreSQL answered at scrape time. |
| `alondra1_maintenance_*` | Retention runs, last success, deleted rows per step. |
| `alondra1_backup_last_success_timestamp_seconds`, `alondra1_backup_last_restore_check_timestamp_seconds` | From the backup service's `status.json`. |
| `alondra1_build_info{version}` | The running API version. |

Labels never contain ids, addresses, or tokens.

Useful graphs in the Prometheus UI:

- request rate: `sum by (code) (rate(alondra1_http_requests_total[5m]))`
- p95 latency: `histogram_quantile(0.95, sum by (le, route) (rate(alondra1_http_request_duration_seconds_bucket[5m])))`
- sign-in funnel: `sum by (result) (increase(alondra1_auth_code_requests_total[1h]))`
- conflicts: `sum by (status) (increase(alondra1_sync_push_mutations_total[1h]))`

## Alerts and what to do

### API down

`AlondraApiDown`.

1. `docker compose ps api` and `docker compose logs --tail 200 api`.
2. A configuration error stops the API at start; the log says which setting is wrong (secrets are never printed).
3. A migration refusal ("database has migration … this API version does not know") means an older image is running against a newer database. Deploy the newer digest again ([release.md](release.md)).

### Database down

`AlondraDatabaseDown`.

1. `docker compose ps db` and `docker compose logs db`.
2. Check disk space (`df -h`). Restart the database: `docker compose restart db`.
3. The API retries with backoff and recovers on its own.

### Errors or latency

`AlondraHighErrorRate` and `AlondraSlowRequests`.

1. Find the route in the graphs above.
2. Correlate the request ids in `docker compose logs api`: `level=ERROR` lines carry `request_id`.
3. Check the database (`docker compose exec db psql -U alondra1 -c "select * from pg_stat_activity where state <> 'idle'"`).

### Sign-in email

`AlondraSignInEmailFailing`: nobody can sign in.

1. Check the relay credentials and TLS (`docker compose logs api | grep "sign-in email not sent"`: the redacted address and the SMTP error).
2. Check the relay's status page and the sender domain's SPF, DKIM, and DMARC.

### Sign-in abuse

`AlondraSignInAbuse`. The per-address, per-IP, and global limits are holding. If it persists:

1. Lower `ALONDRA1_OTP_PER_IP_PER_HOUR` or `ALONDRA1_OTP_GLOBAL_PER_MINUTE`.
2. Block abusive networks at the firewall.
3. Make sure `ALONDRA1_TRUST_PROXY_HEADERS=true` is set behind Caddy, so limits apply per client and not per proxy.

### Refresh reuse

`AlondraRefreshTokenReuse`. Each event already revoked the affected sign-in.

1. Look at `audit_log` (`event_type = 'auth.refresh_reuse'`) for a pattern: one user (a misbehaving client) or many (stolen tokens).
2. For a buggy client version, see the sync-conflict notes below.

### Maintenance

`AlondraMaintenanceStale`.

1. `docker compose logs api | grep maintenance`.
2. Run it by hand: `docker compose run --rm api maintenance`.
3. While it is stale, deleted data is kept longer than [ADR 0003](../decisions/0003-retention.md) promises.

### Sync conflicts

`AlondraSyncConflictSpike` (info). Conflicts are normal when people edit the same task on two offline devices. A spike usually means a client that does not compose patches or sends a wrong `base_version` (see [ADR 0005](../decisions/0005-sync-protocol.md)). Check `X-Alondra1-API-Version` and client versions in support reports.
