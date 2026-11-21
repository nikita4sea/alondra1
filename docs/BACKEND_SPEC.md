# Alondra1 — Local-first multi-tenant backend & monorepo specification

Status: **Implementation specification v1.2 — aligned with existing desktop app 1.3.0** · Date: 2026-11-21

## 1. Objective and non-negotiable requirements

Extend **Alondra1 1.3.0**, an existing **.NET 10 WPF** Windows app, into a primarily offline/local application for Windows, Android and iOS, with **optional** cloud backup and synchronization. Preserve no-account, no-network usage of the Windows To-do workspace. A single account may access multiple **spaces (tenants)**. On onboarding, the user must explicitly choose **Personal**, **Family**, or **Organization**; family and organization are tenant types, not separately provisioned backend stacks. A user may create additional spaces and switch between them. All configuration stored by the backend is non-secret. **No passwords**: authentication uses short-lived one-time codes delivered by email. For this stage, backend services are defined in **Docker Compose** only; provisioning cloud infrastructure is out of scope. All code lives in one monorepo.

### Out of scope for initial release
Email mailbox service; rich collaboration/chat; **cloud storage of task attachment binaries in initial sync milestone** (but preserve existing local attachments and their metadata); payments; tenant-specific cloud VMs/databases; push notifications; SSO; admin impersonation; public sharing. Design extension points, but do not implement these for MVP.

## 2. Repository layout (required)

```text
alondra1/
├── README.md
├── docs/
│   ├── BACKEND_SPEC.md
│   ├── architecture.md
│   ├── decisions/
│   └── runbooks/
├── backend/
│   ├── cmd/api/
│   ├── internal/{auth,tenancy,tasks,config,sync}/
│   ├── migrations/
│   ├── Dockerfile
│   └── go.mod
├── apps/
│   ├── windows/                 # EXISTING .NET 10/WPF solution + src/tests/installer/design
│   ├── android/                 # Android Studio project
│   └── ios/                     # Xcode project
├── packages/
│   ├── api-contract/            # OpenAPI source + generated API clients
│   └── shared-models/          # JSON Schemas and sync protocol fixtures
├── compose.yaml                 # API + PostgreSQL; optional dev SMTP sink
├── .env.example                 # non-secret local Compose settings
├── secrets/                     # ignored, local runtime secret files
│   └── .gitkeep
├── scripts/
└── .github/workflows/
```

Preserve existing solution/project names (`Alondra1.slnx`, `Alondra1.Core`, `Alondra1.Mail`, `Alondra1.Persistence`, `Alondra1.Providers`, `Alondra1.App`, `Alondra1.Tests`) and the Inno Setup installer, scripts and assets within `apps/windows/`. Do not move the existing Windows code into a new language or replace its TaskBoard implementation. Update build scripts and CI working directories. Do not build one shared runtime dependency across three clients unless the existing Windows technology makes that sensible. The **API contract and sync semantics must be shared**, not necessarily UI code. Preserve original app history with `git mv`; do not delete existing app files until build and smoke tests pass. Rename all product-facing references to `Alondra1` without blindly changing package/bundle identifiers or database paths (migrations required).

## 3. Architectural model

- **Local is source of immediate UX truth**: every user-facing read/write occurs against device-local data, including tasks and non-secret config. The Windows application must also remain useful **without ever creating an Alondra1 cloud account**. Cloud features are opt-in; after connecting a space, work must continue offline without a live session. Local writes enter an outbox queue; sync runs opportunistically. Cloud stores the durable synchronized copy and resolves conflicts.
- **Cloud**: stateless Go HTTP API, PostgreSQL, transactional change log, email OTP sender, backup process. Go is a new backend implementation, **not** a replacement for the existing C# Windows application. Initial backend runtime is Docker Compose on a developer machine or an existing Docker-capable host; PostgreSQL is on an internal Docker network and never exposes a host port. Production hardening requires offsite encrypted backups and monitored restores.
- **One database, multi-tenant row-level isolation**. Every tenant-bound row includes `tenant_id`; tenant membership is checked for every request and in every mutation. No cross-tenant access by guessing IDs.
- **Personal space**: explicitly selected in the create-space flow; default membership creator only. Family and Organization: creator is owner; other users join by invitation. Users can belong to multiple spaces of any allowed type. Internal canonical type values: `personal`, `family`, `organization`.
- Tenant names are mutable display labels; IDs are immutable. Tenant type remains immutable for MVP, unless a well-defined conversion workflow is later added.
- **Backend runtime**: Docker Compose runs the API and PostgreSQL. Host provisioning, domain, DNS, firewall, and cloud-provider automation are intentionally excluded for this stage. Compose reads runtime secrets from ignored local files; the API container image is built separately and supplied through `ALONDRA1_API_IMAGE`.

## 4. Authentication: email one-time code only

### Flow
1. Client requests `POST /v1/auth/email/start` with email; server normalizes email, enforces anti-abuse policy, generates random **6-digit numeric** OTP with cryptographically secure RNG, and delivers by transactional email service. Return generic success whether or not email already exists; no account enumeration.
2. Client submits `POST /v1/auth/email/verify` with `email`, `code`, `challenge_id`, and device metadata. Server atomically verifies challenge and creates account if none exists. For an existing user, sign-in succeeds; do not auto-join any tenant.
3. Server issues **short-lived access token** (15 min) and **rotating opaque refresh token** (30 days, revocable); each refresh token is bound to a device/session. Store hashed refresh tokens server-side. Clients store refresh token in Windows Credential Manager / Android Keystore / iOS Keychain, never in SQLite configuration.
4. `POST /v1/auth/refresh` rotates token; detect reuse and revoke the affected token family. `POST /v1/auth/logout` revokes the current session; `POST /v1/auth/logout-all` revokes all sessions.
5. Login required for first setup and to reauthenticate an expired/revoked session. Existing local data remains usable when offline; cloud synchronization resumes only after valid authentication.

### OTP rules
- OTP validity: **10 minutes**; **one-time**, consumed atomically.
- Maximum **5 verification attempts** per challenge; older challenge invalidated on issuing a new one.
- Resend cooldown **60 seconds**, at most 5 starts per email per hour, plus IP/device/global rate limits (tunable).
- OTP stored only as keyed hash/HMAC with a server-only key; never plaintext in DB/logs; use constant-time comparison. Use unpredictable challenge IDs. Avoid abuse of email delivery and monitor bounce/complaint rates.
- Never commit mail provider credentials, HMAC keys, JWT signing keys, database passwords, or refresh tokens to Git, Compose files, client config, or logs.
- Email codes prove control of the mailbox at sign-in, not strong phishing-resistant authentication. Consider passkeys later for higher-assurance organizations.
- Mail provider is a configurable adapter (`SMTP` or provider API); handle delayed/failed delivery and retries. Email delivery requires a verified sender domain with SPF/DKIM/DMARC.

## 5. Tenants and authorization

### Tenant creation UX
After first email verification, show **Create a space** options: `Personal`, `Family`, `Organization`. Selecting one creates that tenant. Show tenant name for Family/Organization; Personal can default to `My space` but is editable. A signed-in user can later create another tenant or switch between existing memberships. Never silently infer `family` from personal email domain, etc.

### Role model
Roles: `owner`, `admin`, `member`. Personal owner only for MVP. Family/organization owners/admins can invite members; only owners can delete tenant and transfer ownership. Members can manage their own tasks; shared tasks are *out of scope* for MVP, so every task has a creator/assignee owner and remains visible only to that user unless a future sharing model is explicitly introduced. Tenant admin is **not** automatically allowed to read members' private tasks or config. Tenant settings are shared to tenant admins; personal user settings stay private.

- Membership unique `(tenant_id, user_id)`, status `active` / `removed`.
- Invites include tenant, normalized invitee email, role, token hash, expiration (7 days), inviter and audit record; only logged-in account matching invited email may accept. Support cancel/revoke and no escalation above inviter permissions.
- API takes tenant context through `X-Tenant-ID` on tenant endpoints; server checks membership on every call. Do not trust a tenant claim without live authorization or bounded authorization caching with invalidation.
- Implement account deletion, tenant deletion, invitation revocation, and membership removal; every deletion has an explicit data-retention and sync tombstone policy.
- PostgreSQL row-level security (RLS) is recommended as defense in depth, but never a substitute for service authorization.

## 6. Data entities / migration contract

Use UUIDv7 (or UUIDv4 if runtime support unavailable), UTC `TIMESTAMPTZ`, PostgreSQL transaction boundaries, and foreign keys. The following are minimum tables:

| Entity | Minimum columns / constraints |
|---|---|
| `users` | `id PK`, `email_normalized UNIQUE`, `created_at`, `deleted_at` |
| `auth_challenges` | `id PK`, `email_normalized`, `otp_hmac`, `expires_at`, `attempt_count`, `consumed_at`, `created_at`, `request_ip_hash` |
| `sessions` | `id PK`, `user_id FK`, `device_id`, `refresh_hash UNIQUE`, `token_family_id`, `expires_at`, `revoked_at`, `created_at`, `last_used_at` |
| `tenants` | `id PK`, `type CHECK(personal/family/organization)`, `name`, `created_by FK`, `created_at`, `deleted_at` |
| `memberships` | `tenant_id FK`, `user_id FK`, `role`, `status`, `created_at`, unique pair |
| `invites` | `id PK`, `tenant_id FK`, `email_normalized`, `role`, `token_hash`, `expires_at`, `accepted_at`, `revoked_at`, `invited_by FK` |
| `devices` | `id PK`, `user_id FK`, `name`, `platform`, `last_seen_at`, `last_ack_seq`, `revoked_at` |
| `todo_tasks`, `task_lists`, `list_groups`, `task_steps`, `task_reminders`, `task_completion_records`, `task_attachments_metadata`, `task_preferences` | See §7: full 1.3.0-compatible task domain, tenant/user ownership, JSON schema version, version BIGINT, timestamps and tombstones; completion history preserved |
| `user_configs` | `tenant_id FK`, `user_id FK`, `namespace`, `data JSONB`, `version`, `updated_at`, composite PK |
| `tenant_configs` | `tenant_id FK`, `namespace`, `data JSONB`, `version`, `updated_at`, composite PK |
| `change_log` | `sequence BIGSERIAL PK`, `tenant_id`, `resource_type`, `resource_id`, `action`, `payload JSONB`, `visibility_user_id`, `created_at` |
| `mutation_receipts` | `tenant_id`, `user_id`, `device_id`, `mutation_id`, `result JSONB`, `created_at`, unique `(tenant_id,user_id,device_id,mutation_id)` |
| `audit_log` | `id`, `tenant_id nullable`, `actor_user_id`, `event_type`, `target_id`, `created_at`, `metadata` (no secrets) |

Create indexes for task list `(tenant_id, owner_user_id, deleted_at, updated_at DESC)`, membership by user, invite lookup, and change-log filtering by `(tenant_id, sequence)` and private visibility. Validate list/group/step ownership and relationships within tenant and permitted user. Config JSON max 256KiB, task title max 500 chars, notes max 64KiB, input metadata bounded and schema-validated; reconcile limits with current Windows model before rollout. Config secret rejection must use explicit allowed schemas rather than just string-matching secret names.

## 7. Task domain: preserve Alondra1 1.3.0, do not replace it

The existing Windows task model and behavior in `TODO.md` are **normative** for backend parity. The earlier generic `status/priority/tags/parent_id` task model is insufficient and must **not** become the canonical wire schema.

### Canonical entities and behavior

- `TodoTask`: stable task ID; list ID; title; notes; completed; `important` boolean; position; created/modified/completed timestamps; start date; due date; optional time slot; My Day date; recurrence rule; reminders; steps; attachment metadata. Preserve existing serialized field meaning and UTC/date-only distinctions.
- `Subtask`: ordered steps, each with independent completed state. Not the same as a nested `TodoTask` hierarchy.
- `TaskList`: name (emoji supported), group ID, color, tint, position, default-list flag. Default **Tasks** list cannot be deleted.
- `ListGroup`: name, position; expand/collapse **is a presentation preference** and should be device-local by default.
- `RecurrenceRule`: frequency daily/weekly/monthly/yearly; interval; chosen weekdays; end date; calendar-date anchor; follow existing Jan 31 → Feb 28 → Mar 31 rules and DST handling.
- `TaskReminder`: local wall-clock time, delivered-for marker, dismissed/snoozed-until. Distinguish shared reminder definition from **per-device delivery state** to prevent repeated alerts after sync.
- `TaskAttachment`: metadata and safe relative path; existing attachments remain on Windows disk. For initial release do **not** claim cloud backups cover attachment bytes. UI must clearly disclose this gap; a later encrypted object-upload workflow with checksums and quotas is required for complete cloud backup.
- `CompletionRecord`: append-only logical completion history, including successive occurrences of recurring tasks; never infer history from current `completed` flag.
- `TaskPreferences`: sort/filter/view/calendar and notifications. Separate cross-device user choices from screen geometry, notification delivery status, and device-specific choices.
- Smart views (My Day, Important, Planned, Upcoming, Overdue, All, Completed, Unscheduled) are **derived filters**; do not create additional copies of tasks or additional backend tables for each view.
- Natural-language quick add and date interpretation remain local; server receives validated structured task changes, not natural-language input.

### Cloud resource model

At minimum model `task_lists`, `list_groups`, `todo_tasks`, `task_steps`, `task_reminders`, `task_completion_records`, `task_attachments_metadata`, and `task_preferences` with tenant ID, owner user ID where applicable, version, deletion tombstones and stable IDs. JSONB payloads are acceptable for nested entities, but server must validate schema, relationships, size and version. Include `schema_version` on every sync resource and migrations/compatibility fixtures for existing `todo.db` records. Preserve task/list/group/step IDs during cloud **enrollment**, unlike JSON **import**, which deliberately remaps IDs.

Task mutations should preserve existing `TaskBoard` change-set invariants and undo: apply a logical change to local `todo.db` in one transaction, then add a durable sync-outbox entry in the **same database transaction**. Remote changes must be applied through an explicit remote-change entry point in `TaskBoard` (not directly by a sync adapter writing task tables). Define undo for pending and acknowledged sync mutations, including list/group deletion and recurring-task completion; use compensating mutations after acknowledgement. Local backup/restore must not silently resurrect deleted cloud data: require explicit restore-as-new-space/import, or a reviewed replacement flow with new revisions.

Existing connected **Microsoft To Do** and **Google Tasks** remain distinct external-provider items under “Account tasks”. Never ingest or upload these to the Alondra1 cloud unless the user explicitly opts in to a future import workflow. Avoid duplicate cross-provider synchronization.

## 8. Configuration: local secrets and explicit cloud allowlist

Keep existing `%LOCALAPPDATA%\alondra1\config.json` with atomic write + `.bak`, old-schema normalization, explicit Merge/Replace preview, and version/refusal safety. The backend stores **only selected portable, non-secret settings**, not the whole `config.json` blob. Cloud sync of settings must be opt-in (independent of task sync if practical).

- Eligible portable settings: theme choice, non-secret UI palette overrides, task defaults and agreed user preferences, validated against versioned allowlist schemas.
- **Device-only**: window position, splitters, local paths, selected data-home, notification delivery markers, cache/backup preferences, transient UI state, and potentially background rendering parameters tied to local files.
- **Never upload**: `secrets/` DPAPI blobs, passwords for manual mail accounts, Microsoft/Google OAuth tokens, Alondra1 refresh tokens, API/provider secrets, cached messages, mail outbox, local message bodies, contact data, calendar items, or notes from Microsoft/Google app folders. OAuth **public client IDs** are not secrets but may be device-specific; do not sync them by default.
- Background image *settings* may sync only when independent from a local image; user-supplied background **files** are not included in initial cloud sync. Retain existing local artwork and all theme tokens.
- `user_configs` and `tenant_configs` should contain portable namespaced settings only. For first release, do not equate tenant config with all local application settings. Readable defaults and schema migrations are mandatory.

## 9. API v1 (OpenAPI 3.1 source of truth)

Base path `/v1`. Success and errors are JSON, with stable error codes and request IDs. Endpoint matrix:

| Method | Route | Meaning |
|---|---|---|
| POST | `/auth/email/start` | Request email OTP |
| POST | `/auth/email/verify` | Verify OTP; register/sign in |
| POST | `/auth/refresh` | Rotate refresh token |
| POST | `/auth/logout` | Revoke current session |
| POST | `/auth/logout-all` | Revoke all sessions |
| GET | `/me` | Profile and memberships |
| POST | `/tenants` | Create selected type of space |
| GET | `/tenants` | List memberships/spaces |
| GET/PATCH | `/tenants/{id}` | Tenant details/update (authorized) |
| DELETE | `/tenants/{id}` | Owner-only delete workflow |
| GET | `/tenants/{id}/members` | List members |
| POST | `/tenants/{id}/invites` | Send email invitation |
| POST | `/invites/{token}/accept` | Accept invite as matching user |
| DELETE | `/tenants/{id}/invites/{invite_id}` | Revoke invite |
| DELETE | `/tenants/{id}/members/{user_id}` | Remove member / leave workflow |
| GET/POST | `/tasks` | List/create current-tenant full `TodoTask` records (not generic substitute) |
| GET/POST | `/task-lists` | List/create user task lists |
| GET/PATCH/DELETE | `/task-lists/{id}` | List rename/reorder/delete (cascade rules) |
| GET/POST | `/list-groups` | List/create task-list groups |
| GET/PATCH/DELETE | `/list-groups/{id}` | Group update/delete (lists become ungrouped) |
| GET | `/tasks/completion-history` | User-authorized completion log |
| GET/PATCH/DELETE | `/tasks/{id}` | Read/change/soft-delete task |
| GET/PUT/PATCH | `/config/user/{namespace}` | Private config in selected tenant |
| GET/PUT/PATCH | `/config/tenant/{namespace}` | Tenant-wide config (read/member, write/admin) |
| GET | `/sync/bootstrap` | Full authorized snapshot and cursor |
| GET | `/sync/changes?cursor=...` | Incremental authorized delta |
| POST | `/sync/push` | Idempotent local mutation batch |
| POST | `/devices` | Register device |
| GET/DELETE | `/devices` / `/devices/{id}` | List/revoke devices |
| GET | `/health/live` | Process liveness |
| GET | `/health/ready` | DB/API readiness |
| GET | `/data/export` | Export account data asynchronously later; synchronous small export MVP |
| DELETE | `/me` | Account-deletion workflow |

Use `X-Tenant-ID` on tenant-bound requests; verify authenticated membership. `401` unauthenticated, `403` not authorized, `404` absent/non-disclosable, `409` concurrency conflict, `410` expired sync cursor, `422` validation error, `429` rate limit. All write operations support idempotency where retries are expected.

## 10. Sync protocol and conflict rules

- Each cloud-enabled client uses local SQLite and an outbox, with `mutation_id`, `resource_id`, base version and local payload. **UI never waits on network** to save a task.
- Initial bootstrap downloads an authorized snapshot per tenant and returns `next_cursor`. Bootstrap should be snapshot-consistent, with paging and watermark, so changes during bootstrap are not missed.
- Push queued mutations in batches (limit 100). Server validates auth, membership, schema, expected version; transactionally updates data, writes change log, and writes mutation receipt. Return success, conflict, or validation error **per mutation**.
- Pull `GET /sync/changes` using an **opaque, signed or server-validated cursor** that binds to user+tenant and authorization visibility. Return ordered changes, tombstones and next cursor. Do not leak events for another tenant or private user.
- Update versions on the server; on conflict return server version and permitted current record. MVP conflict strategy: no silent overwrite; preserve local edit in an actionable conflict queue, allow user to keep server version or deliberately overwrite with fresh expected version.
- Retry with exponential backoff and jitter; resync on reconnect/app resume and periodic foreground checks. No dependence on push notifications.
- Retain tombstones/change events for at least 90 days (configurable). If cursor too old, respond `410 CURSOR_EXPIRED` requiring clean full bootstrap; do not discard unresolved local outbox entries.
- Removed membership revokes cloud access immediately, and clients purge inaccessible tenant cloud caches on next online authorization refresh. **Offline revocation cannot instantly erase data on a disconnected device**; communicate this limitation and encrypt local cache.
- Key acceptance test: two devices edit same task offline; one sync succeeds, the other gets a visible 409 conflict without lost data.

## 11. Client requirements

### Windows — preserve existing desktop implementation

Move existing **.NET 10/WPF** source tree to `apps/windows` using history-preserving moves, including `Alondra1.slnx`, `src/Alondra1.Core`, `Alondra1.Mail`, `Alondra1.Persistence`, `Alondra1.Providers`, `Alondra1.App`, `tests/Alondra1.Tests`, `installer/`, `design/`, logo and themed resources. Preserve `TaskBoard` as the single task-write entrypoint, existing `todo.db` and `personal.db` separation, `config.json`, DPAPI `ISecretStore`, Microsoft/Google OAuth public-client integration, and the Windows Inno Setup upgrade `AppId`. Add a new `Alondra1.Cloud` or `Alondra1.Sync` adapter project depending on `Alondra1.Core` ports, integrated via `AppServices.CreateAsync`; **do not reuse mail `SyncCoordinator` for cloud task sync without a separate boundary**.

On first launch and every later launch, the user may **Continue without cloud**; existing local data does not need a user account. Add **Connect Alondra1 Cloud**, email-code entry, create/join/switch Personal/Family/Organization space, and one-time **Link existing local tasks** workflow. Do not move/delete `todo.db` when linking; take safety backup, map existing IDs to a selected space and detect collisions, and let user preview changes. Do not put cloud session tokens in existing config exports; use DPAPI through `ISecretStore`. Add visible sync status and conflict queues without changing 56 px header + 48 px toolbar or narrow layout behavior. Keep demo mode network-free and isolated. Existing mail/organizer account OAuth is independent of Alondra1 OTP, and manual IMAP/SMTP account passwords remain permitted as **provider** credentials (never cloud login passwords).


### Android
Use Room/SQLite, encrypted credentials via Android Keystore, WorkManager for opportunistic background sync subject to OS constraints, app links for invites if applicable.

### iOS
Use SQLite/Core Data/GRDB as chosen by team, Keychain for credentials, BackgroundTasks for best-effort sync (not guaranteed), universal links for invites if applicable.

Across all clients: local DB schema migration, offline creation/edit/complete/delete, user-visible sync status/conflicts, no secrets in synced config, and separate local databases or logical partitions per account/tenant. Logout should revoke cloud session and offer user-controlled removal of local cached data.

## 12. Docker Compose backend runtime (current implementation scope)

**Only Docker Compose is in scope now.** Do not create cloud provisioning, cloud-init, Kubernetes manifests or provider-specific infrastructure code. Keep `compose.yaml` at the repository root so developers can start the backend stack without any particular cloud provider.

### Required services and networking

- `api`: independently built Go API image selected by `ALONDRA1_API_IMAGE`; listens inside container on port `8080`; publishes `127.0.0.1:${ALONDRA1_API_PORT:-8080}:8080` for local development. The production operator must put a TLS-terminating reverse proxy in front and adapt exposure according to their host setup; **plain HTTP exposed to the internet is forbidden**.
- `db`: PostgreSQL 17 on the internal Compose network only; never add `ports` to this service. Named persistent volume `postgres_data`, restart policy, health check (`pg_isready`), least-privilege database credentials from Compose secret file.
- `mailpit` (development-only, Compose profile `dev-mail`): SMTP test sink with SMTP port 1025, optional UI bound to `127.0.0.1:8025`; never enable against production users. In dev set `ALONDRA1_SMTP_HOST=mailpit`, `ALONDRA1_SMTP_PORT=1025`, and `ALONDRA1_SMTP_TLS_MODE=none` for this internal test sink only. Production SMTP must require authenticated TLS and a verified sender domain.
- API and database must share an internal network. A separate external-facing API network is unnecessary for the localhost-only stage.
- `api` starts only after database passes its health check; the API must implement retry-with-backoff regardless, as `depends_on` does not guarantee DB availability after startup.

### Configuration and secrets

- Commit `.env.example` with non-secret settings only. Do not commit `.env` or anything under `secrets/` except `.gitkeep`.
- Create `secrets/postgres_password` locally (nonempty random value). The database reads it using `POSTGRES_PASSWORD_FILE=/run/secrets/postgres_password`. **API implementation requirement:** read `ALONDRA1_DB_PASSWORD_FILE=/run/secrets/postgres_password`, not a plain password environment variable. Do not pass passwords via command-line arguments.
- API also requires signing/HMAC keys, email provider credentials, refresh-token protection and any other operational secrets, provided separately through ignored files and an explicit future API configuration contract. The Compose example intentionally does **not** pretend the missing backend image or secret integration already exists.
- Do not put user/provider OAuth tokens, manual mailbox passwords, or task attachment binaries into this backend's configuration store.

### Commands

```bash
cp .env.example .env
mkdir -p secrets
# Create a strong random password and save it in secrets/postgres_password using a local secret-generation method.
# Set ALONDRA1_API_IMAGE to your built/published API image in .env.
docker compose config
docker compose up -d db
# Once a compatible backend API image exists:
docker compose up -d api
# Development-only email catcher:
docker compose --profile dev-mail up -d mailpit
```

The Compose scaffold is **not a functioning authentication or synchronization backend** until the API image, migrations, secrets integration, and email sender are implemented. Local development can run the database alone. Avoid `docker compose down -v` unless intentionally deleting all test data.

### Operational constraints

- The Compose volume persists across routine container recreation but is **not a backup**. Production use requires encrypted off-host PostgreSQL backups, tested restores, TLS, monitoring, email deliverability, retention policies, and host security.
- Image versions should be pinned to verified immutable digests before production; the initial example uses a PostgreSQL major-version tag for ease of development.
- Keep application database schema changes in forward-compatible migration files under `backend/migrations/`, applied before serving API traffic by a controlled migration process.

## 13. CI/CD and engineering controls

- PR checks: Go unit/integration tests, migration test against ephemeral PostgreSQL, OpenAPI validation, generated client consistency, Docker Compose configuration validation, secret scanning, dependency audit; Windows/Android/iOS builds on appropriate runners.
- Release: tag image, scan and sign, deploy to staging, run smoke tests, require human approval for prod, deploy pinned digest with rollback path.
- Database migrations are forward-only, backward-compatible across staggered clients. API `/v1` maintained through mobile release lag; document minimum compatible client version.
- Metrics: login OTP request/send/verify success, HTTP latency/error rates, queue age, sync conflicts, DB health, backup success/restore recency. Logs redact emails where practical and never log OTP/tokens or task content by default.
- Privacy: define EU-friendly storage location, retention, export/deletion process, minimal audit data and access controls. Verify actual provider data-processing agreements before launch.

## 14. Testing and acceptance criteria

1. New cloud user signs in with email code, chooses Personal **or** Family **or** Organization, and sees the chosen space; no password workflow exists **for Alondra1 cloud**. Local-only Windows use and existing provider-specific authentication continue to work.
2. Existing user signs in on a second device; original membership list and tasks appear after initial sync.
3. A user belongs to multiple tenants and can switch context without leaking records; one tenant cannot access another's resources by swapping IDs.
4. Family/organization invitation reaches the intended email only; invite acceptance requires that same authenticated email; unauthorized invite actions fail.
5. Private tasks of member A cannot be viewed by member B or tenant admins.
6. Create/update/delete tasks offline for at least 24h; on reconnect, all changes sync. Cloud outage does not block local CRUD. Existing task recurrence, list groups, completion history, reminders and undo preserve their semantics.
7. Concurrent offline update causes visible conflict; original edit can be recovered, not silently overwritten.
8. Expired sync cursor forces safe bootstrap without losing unsent local mutations.
9. OTP expires, attempt/rate limits work, replay fails; refresh token rotation and reuse detection tested.
10. Tenant deletion, membership removal, account deletion, backups and restores are verified with integration tests.
11. `docker compose config` succeeds with local development settings; database starts healthy, database port is not published, API binds to localhost by default, and runtime secrets are absent from Git.
12. Build pipelines can build Windows, Android and iOS projects from new paths; existing Windows app still builds after move. Existing 97-pass/1-skipped test baseline (reported in 1.3.0 delivery notes) is **re-run**, not assumed.
13. Existing Windows To-do works on a fresh install with **no account and no network**; demo remains isolated; `todo.db` is not modified by signup until migration/link is explicitly approved.
14. Existing Google/Microsoft account mail, calendar, contacts, notes, account tasks, and local outbox are not uploaded to Alondra1 servers.
15. Recurrence month-end + DST, per-device reminder deduplication, list deletion/undo, completion history, and restore-after-sync are regression-tested with two devices.
16. App upgrades preserve Inno Setup `AppId`, existing data folder paths and DPAPI secret access. Task attachment cloud-backup limitation is displayed accurately.

## 15. Implementation sequence

**Milestone A:** baseline Windows 1.3.0 tests, history-preserving monorepo restructure, documented decisions, existing task model schema/contract fixtures and local clients' database schema and contract stubs.

**Milestone B:** Docker Compose runtime, backend foundations, DB migrations, mail adapter, OTP authentication and session security.

**Milestone C:** tenant lifecycle, role/membership controls, invitation flow and audit records.

**Milestone D:** task + config CRUD, request authorization and comprehensive isolation tests.

**Milestone E:** local-first outbox sync, bootstrap, cursor/change-log, conflicts, multi-device test harness.

**Milestone F:** operational readiness: TLS/domain, secure secrets, backups and restore tests, dashboards/alerts, release processes, client shipping readiness.

## 16. Explicit assumptions to revisit

- Deployment at this stage: Docker Compose on any suitable local or existing host; infrastructure provisioning and cloud-provider choice are deferred.
- Tenant isolation: shared DB with tenant IDs; **dedicated tenant means a logical Family/Organization space**, not a dedicated physical database/server. If contractual physical isolation is required, change architecture first.
- Tenant task data is private per creator/user by default; explicit shared task feature deferred.
- Personal tenant creation is a choice, not automatic; users can create multiple tenants later.
- No password, magic-link, or social-login flow in MVP; email OTP only.
- Send-email provider, actual domain and DNS registrar are intentionally left configurable. **Windows technology is fixed to existing .NET 10/WPF**; Android/iOS technology remains a later choice.

## 17. Compatibility and architectural delta from uploaded product documents (mandatory)

**Source baseline:** `FEATURES.md`, `GUIDELINES.md`, `OAUTH-SETUP.md`, `TODO.md`, `UI-DESIGN.md`, `ARCHITECTURE.md`, `CODE-GUIDE.md`, `DELIVERY-NOTES.md`, and screenshots of Mail, Calendar, To-do, Notes, Contacts (light/dark/narrow). These describe existing Alondra1 **1.3.0**, not an empty product. Product behavior and exact model names in those documents take precedence over generic schema examples in older backend drafts.

| Existing behavior (1.3.0) | Cloud implementation decision | Compatibility condition |
|---|---|---|
| “No Alondra1 service”, no telemetry, connections only to added mail accounts | **Intentional product policy change** to add **optional first-party cloud** | Update `GUIDELINES.md`, `FEATURES.md`, `ARCHITECTURE.md`, privacy notice and tests explicitly; no hidden network requests or telemetry; local-only mode preserved |
| `todo.db`, `TaskBoard`, `TodoChangeSet`, 50 undo steps | Keep local engine intact; add sync outbox and remote-apply bridge | Atomic local commit/outbox, no raw adapter edits to To-do tables |
| `personal.db` for Personal-mode mail accounts | Never co-migrate it into task cloud sync | Mail content, outbox and provider cache excluded by default |
| Mail account Online/Personal modes | Keep same naming/meaning | **Personal mail mode is not the Personal cloud tenant**; distinguish labels clearly in UX |
| Manual IMAP/POP/SMTP passwords + provider OAuth | Keep as existing provider auth; Alondra1 cloud login is OTP-only | No password or external token sent to Alondra1 backend |
| Calendar/Contacts via Microsoft/Google; Notes in OneDrive/Drive app folder | Keep provider-owned | Not part of Alondra1 backend data synchronization |
| Microsoft To Do / Google Tasks under Account tasks | Keep separated from local tasks | No implicit third-party task re-hosting |
| Imported JSON remaps IDs; restored local tasks replace DB | Cloud enrollment is distinct from import and restore | Migration preserves identity; restores require explicit conflict/revision handling |
| 56px header/48px toolbar, light/dark themes, narrow responsiveness | Add Cloud/Space selector within existing shell conventions | No third header row; proper keyboard/screen-reader names |
| `--demo` uses temporary data | Never create cloud session or network connection in demo | Existing demo/snapshot tests still pass |
| Existing local task attachments | Keep working locally | Cloud backup is incomplete until binary uploads are implemented; no misleading claims |
| No background services or startups | Foreground/opportunistic sync on Windows; user preference governs additional execution | Do not silently add OS startup/background service |

### Local profile / tenant mapping

A single desktop profile may eventually contain several Alondra1 cloud identities/spaces. **Do not associate the historical singleton `todo.db` to a tenant implicitly.** Initial release may link it to exactly one user-selected space through a migration preview; later multi-space support should use an explicit mapping table and isolated per-space stores (or a proven tenant discriminator in a new schema), including separate checkpoints and outboxes. A user switching spaces must not accidentally show tasks from the last active space. Personal-only local tasks without cloud account must remain accessible and exportable.

### End-to-end implementation details

1. Create a canonical `packages/api-contract/schemas/todo-v1.schema.json` from **actual** `TodoModels.cs` serialization with test vectors covering recurrence, DST, completed history, step ordering, groups/lists, reminders and deleted items. The source for this schema is Windows code; this Markdown file is not enough to reconstruct exact serialized field names.
2. Add `ICloudSyncGateway` and account/tenant identity ports to `Alondra1.Core` without introducing network/file/database code there. Cloud adapter contains HTTP and serialization; persistence adapter owns SQLite writes; `TaskBoard` remains authority for task mutations.
3. Add migration to `SqliteTaskStore.Migrations`, pre-migration safety copy, schema downgrade refusal, outbox, sync checkpoints and mapping state; preserve `personal.db` independently.
4. On Connect Cloud, show the selected target space, which lists/tasks/settings will upload, attachment limitations, and what remains strictly local. Require explicit consent; provide pause/disconnect with retention options.
5. Create tenancy-access tests against every task/list/group/completion/config endpoint and sync log. Private task content must not be visible to tenant administrators by default.
6. Distinguish device-specific reminder delivery/dismissal from synced reminder schedules. Define a stable occurrence ID for recurring task completions to make retries idempotent.
7. Define per-resource conflict policies: scalar task metadata version conflict with preserved local copy; order mutation reconciliation for lists/steps; append-only completion records deduplicated by occurrence ID; delete-vs-edit conflict exposed rather than hidden; safe behavior for imported/restored data.
8. Preserve portable task calendar `date-only` and optional local time separately from UTC instants; use IANA timezone IDs for cross-platform sync plus Windows-zone mapping as needed. Server must not blindly recompute recurrence from a UTC deadline.
9. Ensure Windows build commands move to `cd apps/windows && dotnet build Alondra1.slnx && dotnet test Alondra1.slnx`. Keep original Inno Setup AppId and existing `%LOCALAPPDATA%\alondra1` and installer upgrade behavior. Rewrite relative docs/asset/script paths only where the move requires it.
10. Android and iOS should initially match cloud-enabled local tasks/config, authentication and tenant selection; they are **not** required to implement the mature Windows IMAP/POP/SMTP/organizer integrations as part of the backend milestone unless scoped explicitly.

## 18. Requirements traceability and change protocol

- Add a `docs/decisions/0001-opt-in-cloud.md` ADR explicitly superseding the former *No Alondra1 service* rule for users who enable cloud, while retaining local-only operation and zero telemetry.
- Add a `docs/decisions/0002-local-cloud-task-boundary.md` ADR about ownership, tenant mapping, attachments, personal mail mode vs Personal tenant, and provider data exclusions.
- Update user-facing `FEATURES.md` **only when implemented**; new designs stay in spec/roadmap until verified. Update `ARCHITECTURE.md`, `CODE-GUIDE.md`, `TODO.md`, `GUIDELINES.md` in the same changes as code.
- Keep the delivered release notes factual: the uploaded `DELIVERY-NOTES.md` documents 97 passed / 1 skipped for 1.3.0; this task **does not re-run these Windows tests** or claim the backend works.
- Test Windows on its existing .NET 10/WPF toolchain and UI snapshots across themes/narrow mode; the provided screenshots are visual baselines, not editable source assets.
