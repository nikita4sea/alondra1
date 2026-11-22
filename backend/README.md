# Alondra1 cloud API (Go)

The optional cloud for Alondra1 ([ADR 0001](../docs/decisions/0001-opt-in-cloud.md), [BACKEND_SPEC.md](../docs/BACKEND_SPEC.md)). It is not a replacement for the Windows app, and the Windows app does not use it yet.

**Status: Milestone C.** Implemented: runtime configuration from secret files, PostgreSQL connection with retry, forward-only migrations, SMTP delivery, email one-time-code sign-in with rotating refresh tokens (B); spaces with roles, invitations, devices, and account deletion (C). Tasks, settings, and sync are Milestones D and E.

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

## Develop

```powershell
cd backend
go vet ./...
go test ./...                                         # unit tests (memory store, fake SMTP server)
go test -p 1 -tags integration ./...                 # real PostgreSQL 17 (embedded, or ALONDRA1_TEST_DATABASE_URL); -p 1 because packages share it
```

Running with Docker Compose: [docs/runbooks/compose.md](../docs/runbooks/compose.md).
