# Alondra1 cloud API (Go)

The optional cloud for Alondra1 ([ADR 0001](../docs/decisions/0001-opt-in-cloud.md), [BACKEND_SPEC.md](../docs/BACKEND_SPEC.md)). It is not a replacement for the Windows app, and the Windows app does not use it yet.

**Status: Milestone B.** Implemented: runtime configuration from secret files, PostgreSQL connection with retry, forward-only migrations, SMTP delivery, and email one-time-code sign-in with rotating refresh tokens. Spaces (tenants), tasks, settings, and sync are Milestones C to E.

## Layout

| Path | Contents |
| --- | --- |
| `cmd/api` | Entry point: load config, wait for the database, migrate, serve `/v1`, shut down cleanly. |
| `internal/appconfig` | Environment and `*_FILE` secrets. Plain secret variables are refused. |
| `internal/auth` | One-time codes, access and refresh tokens, sessions, rate limits; the `Store` interface and an in-memory store. |
| `internal/auth/storetest` | Behavioral contract every `auth.Store` must pass (memory and PostgreSQL). |
| `internal/postgres` | Connection with backoff, migration runner (advisory lock, downgrade refusal), PostgreSQL `auth.Store`. |
| `internal/mail` | SMTP sender: STARTTLS or implicit TLS; plaintext only for the local mailpit sink. |
| `internal/httpapi` | Routes, JSON error contract, request ids, auth middleware. |
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

## Develop

```powershell
cd backend
go vet ./...
go test ./...                                         # unit tests (memory store, fake SMTP server)
go test -tags integration ./internal/postgres/...      # real PostgreSQL 17: embedded, or ALONDRA1_TEST_DATABASE_URL
```

Running with Docker Compose: [docs/runbooks/compose.md](../docs/runbooks/compose.md).
