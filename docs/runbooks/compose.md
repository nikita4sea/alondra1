# Runbook: backend on Docker Compose (development)

This runs the Alondra1 API and PostgreSQL on one machine for development. It is **not** a production setup: there is no TLS, no backup, and no monitoring. See [BACKEND_SPEC.md](../BACKEND_SPEC.md) §12.

## First start

```powershell
Copy-Item .env.example .env
powershell -ExecutionPolicy Bypass -File scripts/new-dev-secrets.ps1   # creates secrets/postgres_password, token_key, otp_key, smtp_password
docker build -t alondra1-api:dev backend                                 # the image named by ALONDRA1_API_IMAGE
docker compose config                                                    # validate
docker compose up -d db                                                  # database only
docker compose up -d api                                                 # applies migrations, then serves on 127.0.0.1:8080
```

For sign-in codes during development, start the mail sink and point the API at it in `.env` (`ALONDRA1_SMTP_HOST=mailpit`, `ALONDRA1_SMTP_PORT=1025`, `ALONDRA1_SMTP_TLS_MODE=none`):

```powershell
docker compose --profile dev-mail up -d mailpit    # UI at http://127.0.0.1:8025
docker compose up -d api                           # recreate so it picks up the new settings
```

## Checks

- `curl http://127.0.0.1:8080/v1/health/live` returns `{"status":"ok"}`; `/v1/health/ready` also checks the database.
- `docker compose ps`: `db` is healthy; it has no published port.
- `git status` shows nothing under `secrets/` and no `.env`.

## Rules

- The database never gets a `ports:` entry. Use `docker compose exec db psql -U alondra1 alondra1` for inspection.
- The API reads every secret from a file (`*_FILE` settings), never from a plain environment variable or a command-line argument.
- `docker compose down` keeps data. `docker compose down -v` **deletes the database volume**; use it only to discard all test data on purpose.
- The volume is not a backup. Before any non-local use: encrypted off-host backups with tested restores, TLS in front of the API, a verified sender domain (SPF, DKIM, DMARC), monitoring, and pinned image digests.

## Rotating secrets

- `token_key`: rotating it signs everyone out of their access tokens (15 minutes at most); refresh tokens keep working.
- `otp_key`: rotating it invalidates pending sign-in codes (at most 10 minutes old). Sessions are unaffected: refresh tokens are random 256-bit values stored as SHA-256 hashes. To sign everyone out, revoke sessions in the database.
- `postgres_password`: change it in PostgreSQL first (`ALTER ROLE`), then update the file and recreate `api`.
