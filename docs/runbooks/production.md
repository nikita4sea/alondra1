# Runbook: production on Docker Compose

How to run the Alondra1 cloud on one Docker host with TLS, backups, monitoring, and retention. Development setup is in [compose.md](compose.md). Provisioning the host, DNS, and the firewall is the operator's job and not automated here ([BACKEND_SPEC.md](../BACKEND_SPEC.md) §12).

## Before the first start

1. **Host:** a Linux host with Docker Engine and the Compose plugin, automatic security updates, SSH by key only, and a firewall that allows only 22 (from your admin addresses), 80, and 443.
2. **DNS:** an A/AAAA record for the API domain (for example `api.example.com`) pointing at the host.
3. **Mail:** an authenticated SMTP relay with TLS, and a sender domain with SPF, DKIM, and DMARC. Without working mail nobody can sign in.
4. **Secrets** (in `secrets/`, never in git, mode 0600, owned by the deploying user):
   - `postgres_password`, `token_key`, `otp_key`: random values (`scripts/new-dev-secrets.ps1`, or `head -c 48 /dev/urandom | base64`).
   - `smtp_password`: the relay password.
   - `token_key_previous`: empty (used only during a key rotation).
   - `backup_recipients`: the age **public** key, see [backup-restore.md](backup-restore.md). The private key never goes on this host.
5. **`.env`:** copy `.env.example`, then set:
   - `ALONDRA1_API_IMAGE` and `ALONDRA1_BACKUP_IMAGE`: the digests from the release ([release.md](release.md));
   - `ALONDRA1_DOMAIN` and `ALONDRA1_ACME_EMAIL`;
   - `ALONDRA1_TRUST_PROXY_HEADERS=true`;
   - the SMTP settings.
6. **Pin the base images.** In `compose.yaml`, replace `postgres:17`, `caddy:2.8`, and `prom/prometheus:v2.55.1` with digests: `docker pull <image>`, then `docker inspect --format '{{index .RepoDigests 0}}' <image>`.

## Start

```sh
docker compose --profile tls --profile backup --profile monitoring up -d
docker compose ps                                   # api, db healthy; caddy, backup, prometheus running
curl -fsS https://api.example.com/v1/health/ready   # {"status":"ready"} with a valid certificate
curl -sI https://api.example.com/v1/health/live | grep -i strict-transport-security
```

The API applies database migrations before it serves. The retention job runs 1 minute after start and then every `ALONDRA1_MAINTENANCE_INTERVAL` (6 h). The first backup runs immediately.

## What is exposed

| Port | Service | Exposure |
| --- | --- | --- |
| 80, 443 | Caddy | public (80 only redirects and answers ACME challenges) |
| 8080 | API | 127.0.0.1 only (for local checks); public traffic goes through Caddy |
| 9090 | API metrics | internal network only; Caddy answers `/metrics` with 404 |
| 9091 | Prometheus UI | 127.0.0.1 only (use an SSH tunnel) |
| 5432 | PostgreSQL | internal network only |

## Routine operations

- **Upgrade or roll back:** [release.md](release.md).
- **Backups and restores:** [backup-restore.md](backup-restore.md).
- **Alerts:** [monitoring.md](monitoring.md).
- **Run the retention job by hand:** `docker compose run --rm api maintenance`. It is safe while the API runs; only one instance works at a time.
- **Logs:** `docker compose logs api`. Logs are JSON with request ids. They never contain codes, tokens, request bodies, or full email addresses; Caddy logs no URIs.

## Rotating secrets

- **Token key (zero downtime):**
  1. Copy `secrets/token_key` to `secrets/token_key_previous`.
  2. Write a new random value to `secrets/token_key`.
  3. Run `docker compose up -d api`.

  Access tokens and sync cursors signed with the old key keep working. After 30 days (the longest a cursor or token chain matters), empty `token_key_previous` and restart again. Sync cursors still older than that make clients bootstrap again, which is harmless.
- **OTP key:** replace `secrets/otp_key` and restart. Only sign-in codes from the last 10 minutes stop working.
- **Database password:**
  1. `docker compose exec db psql -U alondra1 -c "ALTER ROLE alondra1 PASSWORD '...'"`.
  2. Update `secrets/postgres_password`.
  3. `docker compose up -d api backup`.
- **SMTP password:** update `secrets/smtp_password`, then `docker compose up -d api`.

## Minimum client versions

The API is `/v1`. Additive changes (new endpoints or fields) keep `/v1`; clients must ignore unknown fields. A breaking change gets `/v2`, with `/v1` kept for at least 12 months, long enough for mobile app-store release lag. Every response carries `X-Alondra1-API-Version`. A client may use the cloud if it implements the sync rules of [ADR 0005](../decisions/0005-sync-protocol.md); there is no older client to support yet.
