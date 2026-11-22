-- 0002: spaces (tenants), memberships, invitations; device revocation and account deletion (docs/BACKEND_SPEC.md §5).
-- Forward-only.

CREATE TABLE tenants (
    id         uuid        PRIMARY KEY,
    type       text        NOT NULL CHECK (type IN ('personal', 'family', 'organization')),
    name       text        NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    created_by uuid        NOT NULL REFERENCES users (id),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz
);

CREATE TABLE memberships (
    tenant_id  uuid        NOT NULL REFERENCES tenants (id),
    user_id    uuid        NOT NULL REFERENCES users (id),
    role       text        NOT NULL CHECK (role IN ('owner', 'admin', 'member')),
    status     text        NOT NULL CHECK (status IN ('active', 'removed')),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, user_id)
);
CREATE INDEX memberships_user_active ON memberships (user_id) WHERE status = 'active';

-- The invite token is sent only by email; the database keeps its SHA-256.
CREATE TABLE invites (
    id               uuid        PRIMARY KEY,
    tenant_id        uuid        NOT NULL REFERENCES tenants (id),
    email_normalized text        NOT NULL,
    role             text        NOT NULL CHECK (role IN ('admin', 'member')),
    token_hash       bytea       NOT NULL UNIQUE CHECK (length(token_hash) = 32),
    expires_at       timestamptz NOT NULL,
    accepted_at      timestamptz,
    accepted_by      uuid        REFERENCES users (id),
    revoked_at       timestamptz,
    invited_by       uuid        NOT NULL REFERENCES users (id),
    created_at       timestamptz NOT NULL
);
CREATE INDEX invites_tenant_pending ON invites (tenant_id, created_at DESC) WHERE accepted_at IS NULL AND revoked_at IS NULL;
CREATE INDEX invites_email ON invites (email_normalized);

ALTER TABLE audit_log ADD CONSTRAINT audit_log_tenant_fk FOREIGN KEY (tenant_id) REFERENCES tenants (id);

ALTER TABLE sessions DROP CONSTRAINT sessions_revoke_reason_check;
ALTER TABLE sessions ADD CONSTRAINT sessions_revoke_reason_check
    CHECK (revoke_reason IN ('rotated', 'logout', 'logout_all', 'reuse_detected', 'account_deleted', 'device_revoked'));
