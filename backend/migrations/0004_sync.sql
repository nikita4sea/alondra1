-- 0004: change log, retention floor, and mutation receipts for sync (docs/BACKEND_SPEC.md §10). Forward-only.

-- Every write to tasks, lists, groups, completion history, and settings appends one entry in the same transaction.
-- Writers of one space serialize on a transaction-scoped advisory lock before taking a sequence number, so sequence
-- numbers become visible in commit order within a space and a reader at cursor N never misses a lower number later.
-- visibility_user_id: the only member who may receive the entry; NULL = every member (space settings).
CREATE TABLE change_log (
    sequence           bigserial   PRIMARY KEY,
    tenant_id          uuid        NOT NULL REFERENCES tenants (id),
    resource_type      text        NOT NULL CHECK (resource_type IN ('group', 'list', 'task', 'completion', 'user_config', 'tenant_config')),
    resource_id        text        NOT NULL,
    action             text        NOT NULL CHECK (action IN ('upsert', 'delete')),
    payload            jsonb       NOT NULL,
    visibility_user_id uuid        REFERENCES users (id),
    created_at         timestamptz NOT NULL
);
CREATE INDEX change_log_tenant_sequence ON change_log (tenant_id, sequence);
CREATE INDEX change_log_private ON change_log (tenant_id, visibility_user_id, sequence);

-- Entries at or below floor_sequence may have been purged (ADR 0003: at least 90 days are kept). A cursor below the
-- floor gets 410 CURSOR_EXPIRED and the client bootstraps again.
CREATE TABLE change_log_floor (
    tenant_id      uuid   PRIMARY KEY REFERENCES tenants (id),
    floor_sequence bigint NOT NULL
);

-- The result of every applied sync mutation, per device, so a retried batch is answered from here and never applied twice.
CREATE TABLE mutation_receipts (
    tenant_id   uuid        NOT NULL REFERENCES tenants (id),
    user_id     uuid        NOT NULL REFERENCES users (id),
    device_id   uuid        NOT NULL REFERENCES devices (id),
    mutation_id text        NOT NULL CHECK (mutation_id ~ '^[A-Za-z0-9_-]{1,64}$'),
    result      jsonb       NOT NULL,
    created_at  timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, user_id, device_id, mutation_id)
);
