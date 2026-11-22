# 0005: Sync protocol and client rules

- **Status:** accepted (2026-11-22)
- **Spec:** [BACKEND_SPEC.md](../BACKEND_SPEC.md) §10, §14 (items 2, 6, 7, 8)
- **Reference client:** [`backend/internal/cloudsync/client`](../../backend/internal/cloudsync/client), driven by the multi-device tests in `backend/internal/httpapi/sync_integration_test.go`. The Windows, Android, and iOS adapters must behave the same way.

## Server

1. **Change log.** Every write to groups, lists, tasks, completion history, and settings (through REST or push) appends an entry in the same transaction. Writers of one space take a transaction-scoped advisory lock before their sequence number, so sequence numbers become visible in commit order: a reader at cursor N can never miss a lower number that commits later. Entries are visible only to the record's owner, except space settings (every member).
2. **Bootstrap** (`GET /v1/sync/bootstrap`) pages through the current state in a fixed order (groups, lists, tasks, completion records, personal settings, space settings). The first page fixes a watermark at the newest sequence. Changes during paging arrive through the change feed, so nothing is missed; an item can arrive twice and is applied by version.
3. **Change feed** (`GET /v1/sync/changes`): ordered entries after an opaque cursor, signed with a key derived from the server's token key and bound to the user and the space. Deletions are tombstones. A cursor below the retention floor gets `410 CURSOR_EXPIRED`. The returned cursor also moves past entries the caller may not see.
4. **Push** (`POST /v1/sync/push`): up to 100 mutations (`create`, `patch` with `base_version`, `delete` with `base_version`), each applied in its own transaction and answered `applied`, `conflict` (with the server copy; nothing changed), or `rejected` (invalid; nothing changed). Applied mutations are recorded per device (from the signed-in session) and mutation id, in the same transaction: a retried batch is answered from the receipts (`replayed: true`) and never applies twice. A patch or delete of a record deleted elsewhere is a `NOT_FOUND` conflict with `current_deleted`, never silently dropped.

## Client rules

1. **Local first.** Every edit changes the local copy at once and enters the outbox with a new mutation id. The UI never waits for the network.
2. **Coalesce, but compose.** Several offline edits of one record become one pending mutation. Patches are combined with patch *composition* (`tasks.ComposePatch`), not merge-patch application: a `null` ("remove this field") must survive. A patch after a pending create updates the create; a delete after a pending create drops both.
3. **Base versions.** A patch or delete carries the version the edit was made on. While a mutation for a record is pending, incoming server versions of that record do not overwrite the local view; the server decides on push.
4. **Sync order.** Push the outbox first, then pull. If the push fails (network), keep the outbox unchanged and send the same batch later; receipts make that safe.
5. **Conflicts.** Keep the local edit in a conflict queue, show the server version, and let the user keep the server version or re-apply the edit on top of it (a new mutation based on the server version). Never overwrite silently.
6. **Expired cursor.** On `410`, bootstrap again, then re-apply the still-unsent outbox locally on top of the snapshot and push it later. Never discard unsent mutations.
7. **Lost access.** On `404 TENANT_NOT_FOUND`, remove the space's cloud copy from the device and keep unsent edits aside, where the user can still see or export them. A device that is offline keeps its copy until it is next online (ADR 0003).
8. **Settings** sync down through the same feed (`user_config`, `tenant_config`). They are written with the versioned REST endpoints (`If-Match`), not through push.

## Consequences

- The Windows adapter keeps its outbox in `todo.db`, written in the same SQLite transaction as the task change (ADR 0002), and applies remote items through a `TaskBoard` entry point.
- A purge job (Milestone F) must advance `change_log_floor` when it deletes old entries; until then cursors never expire.
- Devices register at sign-in; `POST /v1/devices` from the spec's endpoint list is not needed for sync.
