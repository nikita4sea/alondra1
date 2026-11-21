-- 0001: accounts, email sign-in challenges, devices, sessions, audit log (docs/BACKEND_SPEC.md §4, §6).
-- Forward-only. Tenancy, tasks, config, and sync tables follow in later migrations.

CREATE TABLE users (
    id               uuid        PRIMARY KEY,
    email_normalized text        NOT NULL UNIQUE CHECK (email_normalized = lower(email_normalized) AND length(email_normalized) <= 254),
    created_at       timestamptz NOT NULL,
    deleted_at       timestamptz
);

-- One emailed code. Only an HMAC of the code (keyed with a server-only secret and bound to id + email) is stored.
CREATE TABLE auth_challenges (
    id               uuid        PRIMARY KEY,
    email_normalized text        NOT NULL,
    otp_hmac         bytea       NOT NULL CHECK (length(otp_hmac) = 32),
    expires_at       timestamptz NOT NULL,
    attempt_count    integer     NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    consumed_at      timestamptz,
    created_at       timestamptz NOT NULL,
    request_ip_hash  bytea       NOT NULL
);
CREATE INDEX auth_challenges_email_created ON auth_challenges (email_normalized, created_at DESC);
CREATE INDEX auth_challenges_ip_created ON auth_challenges (request_ip_hash, created_at DESC);
CREATE INDEX auth_challenges_open ON auth_challenges (email_normalized) WHERE consumed_at IS NULL;

CREATE TABLE devices (
    id           uuid        PRIMARY KEY,
    user_id      uuid        NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    name         text        NOT NULL CHECK (length(name) <= 400),
    platform     text        NOT NULL CHECK (platform IN ('windows', 'android', 'ios', 'other')),
    last_seen_at timestamptz NOT NULL,
    last_ack_seq bigint      NOT NULL DEFAULT 0,
    revoked_at   timestamptz,
    created_at   timestamptz NOT NULL
);
CREATE INDEX devices_user ON devices (user_id);

-- A device sign-in. Each refresh revokes the row as 'rotated' and inserts its successor in the same token family.
CREATE TABLE sessions (
    id              uuid        PRIMARY KEY,
    user_id         uuid        NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    device_id       uuid        NOT NULL REFERENCES devices (id) ON DELETE CASCADE,
    refresh_hash    bytea       NOT NULL UNIQUE CHECK (length(refresh_hash) >= 2),
    token_family_id uuid        NOT NULL,
    expires_at      timestamptz NOT NULL,
    revoked_at      timestamptz,
    revoke_reason   text        CHECK (revoke_reason IN ('rotated', 'logout', 'logout_all', 'reuse_detected', 'account_deleted')),
    created_at      timestamptz NOT NULL,
    last_used_at    timestamptz NOT NULL,
    CHECK ((revoked_at IS NULL) = (revoke_reason IS NULL))
);
CREATE INDEX sessions_family ON sessions (token_family_id);
CREATE INDEX sessions_user ON sessions (user_id);

-- Security-relevant events. Metadata never contains codes, tokens, or task content.
CREATE TABLE audit_log (
    id            bigserial   PRIMARY KEY,
    tenant_id     uuid,
    actor_user_id uuid        REFERENCES users (id) ON DELETE SET NULL,
    event_type    text        NOT NULL,
    target_id     text,
    created_at    timestamptz NOT NULL,
    metadata      jsonb       NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX audit_log_actor ON audit_log (actor_user_id, created_at DESC);
CREATE INDEX audit_log_tenant ON audit_log (tenant_id, created_at DESC) WHERE tenant_id IS NOT NULL;
