# Runbook: releasing and deploying the backend

## Release

1. All checks pass on `main` (`backend` workflow): unit and integration tests on PostgreSQL 17, `govulncheck`, OpenAPI lint, Compose, Caddy and Prometheus config validation, shellcheck, and the backup → decrypt → restore round trip.
2. Write the release notes in [DELIVERY-NOTES.md](../DELIVERY-NOTES.md).
3. Tag and push: `git tag api-v1.0.0 && git push origin api-v1.0.0`.
4. The `release-backend` workflow:
   - runs every check again;
   - builds `alondra1-api` (with `VERSION` baked in) and `alondra1-backup`;
   - pushes both to GHCR with provenance and an SBOM;
   - scans them with Trivy (fails on fixable HIGH/CRITICAL vulnerabilities);
   - signs them by digest with cosign (keyless, GitHub OIDC);
   - waits on the **production** environment.
5. A required reviewer approves the `production` job. Configure required reviewers once, under *Settings → Environments → production*. Its summary shows the two digests to deploy.

## Deploy (on the server)

1. Verify the signature:
   ```sh
   cosign verify --certificate-identity-regexp 'https://github.com/OWNER/alondra1/' \
     --certificate-oidc-issuer https://token.actions.githubusercontent.com ghcr.io/owner/alondra1-api@sha256:...
   ```
2. Note the current `ALONDRA1_API_IMAGE` and `ALONDRA1_BACKUP_IMAGE` (the rollback target), then set the new digests in `.env`.
3. Take a backup first: `docker compose run --rm backup once`.
4. Deploy: `docker compose pull api backup && docker compose up -d api backup`. The API applies new migrations before serving. On shutdown, the old container fails readiness first and finishes in-flight requests (up to 30 s).
5. Smoke test:
   - `curl -fsS https://api.example.com/v1/health/ready`;
   - `curl -sI https://api.example.com/v1/health/live | grep -i x-alondra1-api-version` (the new version);
   - sign in once with a test account, create, edit, and delete a task, and sync two clients;
   - check that there are no `5xx` and no new alerts for 15 minutes.

## Roll back

If the release added **no** migration, the previous image runs against the database unchanged:

1. Set the previous digests in `.env`.
2. Run `docker compose up -d api backup`.

If the release **did** add a migration, the previous API refuses to start against the migrated database, which protects against silent downgrades. Migrations are forward-only. Either roll forward with a fix (preferred), or restore the backup taken just before the deploy into the live database ([backup-restore.md](backup-restore.md)) and deploy the previous digest; writes made since that backup are lost unless clients still hold them in their outboxes. Write migrations so that a fix-forward is always possible: add columns and tables, and never drop or rename in the same release that stops using them.

## Staging

Run a second Compose project on another host (or `docker compose -p alondra1-staging` with its own `.env`, domain, and secrets) and deploy each release there first with the same steps. Promoting to production is the approval in step 5 of the release.
