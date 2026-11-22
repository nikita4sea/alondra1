# 0004: How the cloud stores tasks and settings

- **Status:** accepted (2026-11-22)
- **Spec:** [BACKEND_SPEC.md](../BACKEND_SPEC.md) §6, §7, §8, §10

## Decisions

1. **The wire format is todo-v1, unchanged.** Groups, lists, tasks, and completion records travel exactly as the Windows serializer writes them ([schema](../../packages/api-contract/schemas/todo-v1.schema.json)). API envelopes add `id`, `version`, `schema_version`, and `updated_at` around the object; they never rename its fields.
2. **Stored as validated JSONB with key columns.** Each resource is one row: the todo-v1 object in `data`, plus the columns needed for constraints and queries (`list_id`, `group_id`, `is_default`, timestamps, tombstone). Steps, reminder definitions, and attachment metadata stay nested in the task (the spec allows JSONB for nested entities); completion history has its own append-only table. The server validates every write against the schema in Go (`internal/tasks`), checks references (a task's list, a list's group) within the same space and user, and stores the canonical result.
3. **Values are never converted.** Date-only values, local wall-clock times (due time, reminder time), and UTC instants are validated as their own formats and stored as the client's strings, offset included. The server never recomputes recurrence.
4. **Device-local state is dropped at the door.** Reminder delivery state (`deliveredFor`, `dismissed`, `snoozedUntilUtc`) and list-group `collapsed` are accepted on input, so clients can send their model as-is, but are not stored or returned. This prevents repeated alerts after sync and keeps presentation per device.
5. **Private per user.** Every row is keyed by (space, owner, client id). Queries always filter by the caller; owners and admins of a space cannot read members' tasks, completion history, or personal settings. The same client id in two users' data is two different records.
6. **Membership is checked inside each transaction** with a share lock on the membership row, so removing a member waits for in-flight writes and then cuts access completely.
7. **Optimistic concurrency, no silent overwrite.** Every record has a version (ETag). PATCH and DELETE require `If-Match`; a stale version returns `409 VERSION_CONFLICT` with the server copy, for the client's conflict queue. Creating an existing id (live or deleted) returns `409 ALREADY_EXISTS` with the server copy: retries are recognizable and tombstones are never resurrected by a create.
8. **Completion records are idempotent by id** and can be appended in the same transaction as the task change (`append_completion`), so completing one occurrence of a repeating task and advancing its due date is atomic.
9. **Limits follow the Windows model**, not the smaller numbers in spec §6: title 2,000 characters, notes 100,000, 512 KiB per resource, 500 steps, 50 reminders, 100 attachments. Anything the app stores locally can be uploaded.
10. **Settings are allowlists, not blobs.** Only `appearance` and `todo.preferences` (per user and space) and `space.profile` (per space, owners and admins write) exist; each has a strict schema and rejects unknown keys. Secrets, OAuth client ids, local paths, window geometry, the selected view, and background picture files cannot be stored.

## Consequences

- Sync (Milestone E) can emit change-log entries from the same writes; versions and tombstones are already in place.
- Changing the task model means a new schema version (`schema_version` column) and a migration path, tested by the shared fixtures on both sides.
