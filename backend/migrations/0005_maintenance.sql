-- 0005: bookkeeping for the retention job (docs/decisions/0003-retention.md). Forward-only.

-- Set when the content of a deleted space has been purged; the tenant row stays as the tombstone.
ALTER TABLE tenants ADD COLUMN purged_at timestamptz;

-- Indexes for the purge queries.
CREATE INDEX change_log_created ON change_log (created_at);
CREATE INDEX mutation_receipts_created ON mutation_receipts (created_at);
CREATE INDEX auth_challenges_created ON auth_challenges (created_at);
CREATE INDEX sessions_expires ON sessions (expires_at);
CREATE INDEX audit_log_created ON audit_log (created_at);
CREATE INDEX memberships_removed ON memberships (updated_at) WHERE status = 'removed';
CREATE INDEX tenants_deleted ON tenants (deleted_at) WHERE deleted_at IS NOT NULL AND purged_at IS NULL;
