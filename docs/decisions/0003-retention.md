# 0003: Deletion and retention of spaces, memberships, and accounts

- **Status:** accepted (2026-11-22); enforced by the retention job (`backend/internal/maintenance`) since Milestone F
- **Spec:** [BACKEND_SPEC.md](../BACKEND_SPEC.md) §5 ("every deletion has an explicit data-retention and sync tombstone policy"), §10, §13

## Decision

| Action | Immediately | Kept | Purged |
| --- | --- | --- | --- |
| **Delete a space** (owner) | `tenants.deleted_at` set; every membership becomes `removed`; pending invitations revoked. The space disappears from every member's list and answers `404`. | The tenant row and its audit log entries. | Space content (tasks, settings, change log, from Milestone D/E) is purged **30 days** after deletion; the tenant row is kept as a tombstone for sync. |
| **Remove a member / leave** | Membership `removed`; the person loses access at once. | Tasks they created in the space are private to them and are **not** shown to others; they are purged with the space or after 30 days unless the person rejoins. | After 30 days. |
| **Revoke an invitation** | `revoked_at` set; the token stops working. | The row (no token, only its hash). | With the space. |
| **Delete the account** | Refused with `OWNERSHIP_TRANSFER_REQUIRED` while the user is the only owner of a space that has other members. Otherwise: spaces where the user is the only member are deleted (as above); other memberships end; pending invitations to the user are revoked; every session and device is revoked; `users.deleted_at` set and the email replaced by `deleted-<id>@deleted.invalid`, so the address is free for a new sign-up and no longer stored. | The user row without the address (foreign keys in history stay valid) and audit events. | Private content after 30 days, with the deleted spaces. |
| **Revoke a device** | The device and all its sessions are revoked. | The row, for the device list history. | With the account. |

## Sync tombstones (Milestone E)

Deletions of spaces, lists, groups, and tasks produce change-log entries that clients see as tombstones for at least **90 days** (configurable). A client whose cursor is older gets `410 CURSOR_EXPIRED` and bootstraps again without losing its unsent outbox.

## Offline devices

Revoking a membership or a device cuts cloud access at once, but a disconnected device keeps its local copy until it next goes online and learns about the revocation; then it purges that space's cloud cache. The UI must say this plainly.

## Audit log

Audit events contain ids, event types, roles, and redacted addresses; never codes, tokens, or content. They are kept for **1 year**, then purged.

## Consequences

- The retention job (`backend/internal/maintenance`) runs every 6 hours in the API and on demand (`api maintenance`); `AlondraMaintenanceStale` alerts when it stops succeeding. It advances `change_log_floor` as it purges, so stale sync cursors get `410` and clients bootstrap again.
- Backups contain deleted data until they expire; backup retention must not exceed 35 days to keep the promise above.
