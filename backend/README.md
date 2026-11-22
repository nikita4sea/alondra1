# Alondra1 cloud API (Go)

The optional cloud for Alondra1 ([ADR 0001](../docs/decisions/0001-opt-in-cloud.md), [BACKEND_SPEC.md](../docs/BACKEND_SPEC.md)). It is not a replacement for the Windows app, and the Windows app does not use it yet.

**Status: Milestones A to F.** Implemented: runtime configuration from secret files, PostgreSQL connection with retry, forward-only migrations, SMTP delivery, email one-time-code sign-in with rotating refresh tokens (B); spaces with roles, invitations, devices, and account deletion (C); tasks, lists, groups, completion history, portable settings, and export (D); sync with change log, paged bootstrap, signed change cursors, idempotent push, and a reference offline-first client (E); retention job, metrics, zero-downtime key rotation, readiness draining, TLS proxy, encrypted backups with restore checks, alert rules, and the release pipeline (F). The Windows cloud adapter is not written yet.

## Layout

| Path | Contents |
| --- | --- |
| `cmd/api` | Entry point: load config, wait for the database, migrate, serve `/v1`, shut down cleanly. |
| `internal/appconfig` | Environment and `*_FILE` secrets. Plain secret variables are refused. |
| `internal/auth` | One-time codes, access and refresh tokens, sessions, rate limits; the `Store` interface and an in-memory store. |
| `internal/auth/storetest` | Behavioral contract every `auth.Store` must pass (memory and PostgreSQL). |
| `internal/postgres` | Connection with backoff, migration runner (advisory lock, downgrade refusal), PostgreSQL `auth.Store`. |
| `internal/mail` | SMTP sender: STARTTLS or implicit TLS; plaintext only for the local mailpit sink. |
| `internal/tenancy` | Spaces: types, roles and permission rules (pure functions), invitations, member removal, ownership transfer, account deletion; every change runs in one transaction that locks the space. |
| `internal/tasks` | The todo-v1 model: strict validation, device-local fields dropped, merge patch, versions, references, cascades, completion history, export. |
| `internal/settings` | Allowlisted settings namespaces with strict schemas and versions. |
| `internal/cloudsync` | Sync pull side: signed cursors, paged bootstrap with watermark, change feed. (Push is `tasks.Service.Push`.) |
| `internal/cloudsync/client` | Reference offline-first client: local copy, outbox with patch composition, conflict queue, bootstrap replay, purge on lost access. Drives the multi-device tests. |
| `internal/maintenance` | Retention job (ADR 0003): purges and cursor floors; one instance at a time. |
| `internal/metrics` | Dependency-free Prometheus registry; served on the internal metrics port. |
| `internal/httpapi` | Routes, JSON error contract, request ids, auth middleware. |
| `internal/testdb` | Real, migrated PostgreSQL 17 for integration tests (embedded or `ALONDRA1_TEST_DATABASE_URL`). |
| `migrations` | `NNNN_name.sql`, embedded into the binary. |

The HTTP contract is [packages/api-contract/openapi.yaml](../packages/api-contract/openapi.yaml); `httpapi` tests fail if a documented operation is not routed or an error code is undocumented.

## Sign-in rules (spec §4)

- 6-digit codes from the OS CSPRNG, valid 10 minutes, one use, at most 5 attempts; a new code replaces older ones.
- Only an HMAC of the code is stored (server-only key, bound to challenge id and email); comparison is constant-time.
- Limits: one request per 60 s and 5 per hour per address, per-IP (keyed hash, never the raw IP) and global limits.
- The start response is identical for new and existing addresses.
- Access tokens: HS256, 15 minutes, fixed header (no `alg` negotiation); every request also checks that the session is still active, so logout is immediate.
- Refresh tokens: 256-bit opaque, `a1r_` prefix, 30 days, stored as SHA-256; each refresh rotates. Reusing a rotated token revokes the whole sign-in (token family), including access tokens.
- Logs never contain codes, tokens, request bodies, or full email addresses.

## Spaces (spec §5)

- Types `personal`, `family`, `organization`, always chosen explicitly and immutable. The creator is owner. A Personal space has only its owner (no invitations, no leaving; delete it instead).
- Roles: `owner` (rename, delete, invite, remove anyone, change roles), `admin` (rename, invite admins or members, remove members), `member`. Nobody invites above their own role or as owner; ownership moves only by an owner changing a role.
- A space with other members always keeps at least one owner (`LAST_OWNER`), also under concurrent requests: every change locks the space row first.
- A caller who is not an active member gets `404` for a space, exactly like a missing id.
- Invitations: emailed token (`a1i_`, 256-bit, stored as SHA-256, never returned by the API), 7 days, one use, only for the signed-in account with the invited address; a new one replaces a pending one; 20 per space per hour. If the email cannot be sent, the invitation is revoked.
- Account deletion: refused while the user is the only owner of a space with other members (`OWNERSHIP_TRANSFER_REQUIRED` with the space ids); otherwise solo spaces are deleted, memberships end, sessions and devices are revoked, and the address is pseudonymized so it can sign up again. Retention: [ADR 0003](../docs/decisions/0003-retention.md).
- Request logs record the route pattern, never the raw path, so invitation tokens never reach the logs.

## Tasks and settings (spec §6 to §8)

- Resources are the todo-v1 objects the Windows app writes, wrapped in `{id, version, schema_version, updated_at, task | list | group}`. The space comes from `X-Tenant-ID`; membership is checked inside each transaction.
- Private: every row is (space, owner, client id). Nobody else in the space, admins included, can read it.
- Writes need `If-Match`. A stale version gets `409 VERSION_CONFLICT` with the server copy; creating an existing or deleted id gets `409 ALREADY_EXISTS`.
- Deleting a group ungroups its lists; deleting a list deletes its tasks; the default list stays. Tombstones and completion history are kept.
- `append_completion` adds a completion record atomically with a task change, idempotent by its id.
- Reminder delivery state and group collapse are device-local: accepted, never stored.
- Settings: `appearance` and `todo.preferences` per user, `space.profile` per space (owners and admins write). Unknown keys are rejected.
- `GET /v1/data/export`: everything of the caller, with tasks per space as an `alondra1-todo` document the Windows app imports.
- Decisions: [ADR 0004](../docs/decisions/0004-cloud-task-storage.md).

## Sync (spec §10)

- Every write (REST or push) appends to `change_log` in the same transaction; writers of a space serialize on an advisory lock, so sequences are visible in commit order.
- `GET /v1/sync/bootstrap`: paged snapshot (groups, lists, tasks, completions, personal settings, space settings) with a watermark cursor.
- `GET /v1/sync/changes?cursor=`: ordered entries after a signed cursor bound to user and space; only what the caller may see; `410 CURSOR_EXPIRED` below the retention floor.
- `POST /v1/sync/push`: up to 100 mutations, each `applied`, `conflict` (with the server copy), or `rejected`; applied ones are recorded per device and mutation id, so retries never apply twice. Delete-versus-edit surfaces as a conflict.
- Client rules (outbox, patch composition, conflicts, expired cursors, lost access): [ADR 0005](../docs/decisions/0005-sync-protocol.md), implemented by `internal/cloudsync/client`.

## Operations (Milestone F)

- `api` serves; `api maintenance` runs the retention job once; `api healthcheck` probes readiness (the image has no curl).
- `/metrics` on `ALONDRA1_METRICS_ADDR` (`:9090`), internal network only: traffic and latency per route, sign-in and refresh outcomes, push results, database health, maintenance and backup freshness.
- Readiness turns `503` when shutdown begins, then in-flight requests finish. Every response carries `X-Alondra1-API-Version`.
- Token-key rotation: `ALONDRA1_TOKEN_KEY_PREVIOUS_FILE` still verifies access tokens and sync cursors signed with the old key.
- Deployment pieces in [`deploy/`](../deploy): Caddy TLS proxy, backup sidecar (age-encrypted dumps, restore-checked), Prometheus with alert rules. Release pipeline: `.github/workflows/release.yml`.
- Runbooks: [production](../docs/runbooks/production.md), [backups and restores](../docs/runbooks/backup-restore.md), [monitoring](../docs/runbooks/monitoring.md), [release](../docs/runbooks/release.md).

## Develop

```powershell
cd backend
go vet ./...
go test ./...                                         # unit tests (memory store, fake SMTP server)
go test -p 1 -tags integration ./...                 # real PostgreSQL 17 (embedded, or ALONDRA1_TEST_DATABASE_URL); -p 1 because packages share it
```

Running with Docker Compose: [docs/runbooks/compose.md](../docs/runbooks/compose.md).
