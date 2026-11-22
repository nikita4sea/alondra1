-- 0003: tasks, lists, groups, completion history, portable settings (docs/BACKEND_SPEC.md §6-§8). Forward-only.
--
-- Each resource is the todo-v1 object (packages/api-contract/schemas/todo-v1.schema.json) as validated JSONB, with the
-- key columns needed for constraints and queries. Every row belongs to one space AND one user: tasks are private to
-- their owner, including towards the space's owners and admins. Ids are client ids (kept on enrollment), unique per
-- (space, owner). deleted_at rows are tombstones kept for sync (ADR 0003).

CREATE TABLE list_groups (
    tenant_id      uuid        NOT NULL REFERENCES tenants (id),
    owner_user_id  uuid        NOT NULL REFERENCES users (id),
    id             text        NOT NULL CHECK (id ~ '^[A-Za-z0-9_-]{1,64}$'),
    data           jsonb       NOT NULL CHECK (octet_length(data::text) <= 524288),
    version        bigint      NOT NULL CHECK (version > 0),
    schema_version integer     NOT NULL,
    created_at     timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL,
    deleted_at     timestamptz,
    PRIMARY KEY (tenant_id, owner_user_id, id)
);

CREATE TABLE task_lists (
    tenant_id      uuid        NOT NULL REFERENCES tenants (id),
    owner_user_id  uuid        NOT NULL REFERENCES users (id),
    id             text        NOT NULL CHECK (id ~ '^[A-Za-z0-9_-]{1,64}$'),
    group_id       text,
    is_default     boolean     NOT NULL DEFAULT false,
    data           jsonb       NOT NULL CHECK (octet_length(data::text) <= 524288),
    version        bigint      NOT NULL CHECK (version > 0),
    schema_version integer     NOT NULL,
    created_at     timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL,
    deleted_at     timestamptz,
    PRIMARY KEY (tenant_id, owner_user_id, id)
);
-- One default "Tasks" list per user and space.
CREATE UNIQUE INDEX task_lists_one_default ON task_lists (tenant_id, owner_user_id) WHERE is_default AND deleted_at IS NULL;
CREATE INDEX task_lists_group ON task_lists (tenant_id, owner_user_id, group_id) WHERE deleted_at IS NULL;

CREATE TABLE todo_tasks (
    tenant_id      uuid        NOT NULL REFERENCES tenants (id),
    owner_user_id  uuid        NOT NULL REFERENCES users (id),
    id             text        NOT NULL CHECK (id ~ '^[A-Za-z0-9_-]{1,64}$'),
    list_id        text        NOT NULL,
    data           jsonb       NOT NULL CHECK (octet_length(data::text) <= 524288),
    version        bigint      NOT NULL CHECK (version > 0),
    schema_version integer     NOT NULL,
    created_at     timestamptz NOT NULL,
    updated_at     timestamptz NOT NULL,
    deleted_at     timestamptz,
    PRIMARY KEY (tenant_id, owner_user_id, id)
);
CREATE INDEX todo_tasks_recent ON todo_tasks (tenant_id, owner_user_id, deleted_at, updated_at DESC);
CREATE INDEX todo_tasks_list ON todo_tasks (tenant_id, owner_user_id, list_id) WHERE deleted_at IS NULL;

-- Append-only completion history (every occurrence of a recurring task). Never derived from the "completed" flag.
CREATE TABLE task_completion_records (
    tenant_id     uuid        NOT NULL REFERENCES tenants (id),
    owner_user_id uuid        NOT NULL REFERENCES users (id),
    id            text        NOT NULL CHECK (id ~ '^[A-Za-z0-9_-]{1,64}$'),
    task_id       text        NOT NULL,
    completed_at  timestamptz NOT NULL,
    data          jsonb       NOT NULL CHECK (octet_length(data::text) <= 65536),
    created_at    timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, owner_user_id, id)
);
CREATE INDEX task_completion_records_recent ON task_completion_records (tenant_id, owner_user_id, completed_at DESC, id DESC);

-- Portable settings from explicit, versioned allowlists (internal/settings). Never secrets, never device-only state.
CREATE TABLE user_configs (
    tenant_id  uuid        NOT NULL REFERENCES tenants (id),
    user_id    uuid        NOT NULL REFERENCES users (id),
    namespace  text        NOT NULL,
    data       jsonb       NOT NULL CHECK (octet_length(data::text) <= 262144),
    version    bigint      NOT NULL CHECK (version > 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, user_id, namespace)
);

CREATE TABLE tenant_configs (
    tenant_id  uuid        NOT NULL REFERENCES tenants (id),
    namespace  text        NOT NULL,
    data       jsonb       NOT NULL CHECK (octet_length(data::text) <= 262144),
    version    bigint      NOT NULL CHECK (version > 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace)
);
