# 0002: Boundary between local tasks and the cloud

- **Status:** accepted (2026-11-21)
- **Spec:** [BACKEND_SPEC.md](../BACKEND_SPEC.md) §7, §8, §10, §17

## Ownership

- `TaskBoard` stays the only writer of task data on Windows. Local changes are committed to `todo.db` and, once a space is linked, recorded in a sync outbox **in the same SQLite transaction**. Remote changes enter through an explicit remote-apply entry point on `TaskBoard`; the sync adapter never writes task tables directly.
- The cloud adapter is a new project (`Alondra1.Sync` or `Alondra1.Cloud`) that depends on ports in `Alondra1.Core`. It does **not** reuse the mail `SyncCoordinator`.
- The wire model is the 1.3.0 model, unchanged: [todo-v1.schema.json](../../packages/api-contract/schemas/todo-v1.schema.json), generated from and tested against `TodoModels.cs`. Date-only, local wall-clock, and UTC values keep their distinct formats; the server never recomputes recurrence from a UTC deadline.

## Tenant mapping

- The historical single `todo.db` is **not** associated with a space implicitly. Linking is a one-time, previewed, consented step to exactly one space, after a safety backup; ids are preserved (enrollment), unlike JSON import, which remaps ids.
- Later multi-space support uses an explicit mapping and separate per-space stores with their own outboxes and checkpoints, so switching spaces never shows the previous space's tasks.
- Restoring a local backup after linking never silently resurrects data deleted in the cloud; it needs an explicit restore-as-new-space or a reviewed replacement.

## Device-local state

Reminder delivery state (`deliveredFor`, `dismissed`, `snoozedUntilUtc`), list-group expand/collapse, window geometry, notification delivery markers, and local paths stay on the device. Reminder **definitions** sync.

## Attachments

Attachment **metadata** syncs; attachment **bytes** stay on the device in the first release. The UI must say clearly that cloud backup does not include attachment files until an encrypted upload workflow with checksums and quotas exists.

## Mail "Personal mode" is not the "Personal" space

Mail accounts keep their Online and Personal modes (`personal.db`). The cloud "Personal" space is a tenant type. The UI labels them differently, and `personal.db` is never migrated into cloud sync.

## Never uploaded

Mail (messages, bodies, outbox, provider caches), calendar items, contacts, provider notes, Microsoft To Do and Google Tasks items ("Account tasks"), DPAPI secret files, provider passwords and OAuth tokens, Alondra1 refresh tokens, and user-picked background image files. OAuth public client ids are device-specific by default.

## Consequences

- A new SQLite migration adds the outbox, checkpoints, and mapping state, with a pre-migration safety copy and a downgrade refusal.
- Conflicts are surfaced, never silently overwritten: per-resource policies for scalar fields, ordering, append-only completion records (deduplicated by occurrence id), and delete-versus-edit.
