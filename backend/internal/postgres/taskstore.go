package postgres

import (
	"context"
	"encoding/json"
	"errors"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/auth"
	"alondra1/backend/internal/settings"
	"alondra1/backend/internal/tasks"
)

// withMember runs fn in one transaction after confirming the caller is an active member of a live space. The share
// lock on the membership row makes a concurrent removal wait until this transaction ends, so no write lands after it.
func withMember(ctx context.Context, pool *pgxpool.Pool, scope tasks.Scope, fn func(tx pgx.Tx, role string) error) error {
	if !auth.IsUUID(scope.TenantID) || !auth.IsUUID(scope.UserID) {
		return tasks.ErrNotMember
	}
	return pgx.BeginFunc(ctx, pool, func(tx pgx.Tx) error {
		var role string
		err := tx.QueryRow(ctx, `SELECT m.role FROM memberships m JOIN tenants t ON t.id = m.tenant_id
			WHERE m.tenant_id = $1 AND m.user_id = $2 AND m.status = 'active' AND t.deleted_at IS NULL
			FOR SHARE OF m`, scope.TenantID, scope.UserID).Scan(&role)
		if isNoRows(err) {
			return tasks.ErrNotMember
		}
		if err != nil {
			return err
		}
		return fn(tx, role)
	})
}

// TaskStore implements tasks.Store.
type TaskStore struct{ pool *pgxpool.Pool }

func NewTaskStore(pool *pgxpool.Pool) *TaskStore { return &TaskStore{pool: pool} }

var _ tasks.Store = (*TaskStore)(nil)

func (s *TaskStore) InScope(ctx context.Context, scope tasks.Scope, fn func(tasks.Tx) error) error {
	return withMember(ctx, s.pool, scope, func(tx pgx.Tx, _ string) error { return fn(taskTx{tx: tx, scope: scope}) })
}

type taskTx struct {
	tx    pgx.Tx
	scope tasks.Scope
}

func table(kind tasks.Kind) string {
	return [...]string{"list_groups", "task_lists", "todo_tasks"}[kind]
}

func scanRecord(row pgx.Row) (tasks.Record, error) {
	var r tasks.Record
	var data []byte
	err := row.Scan(&r.ID, &data, &r.Version, &r.UpdatedAt, &r.Deleted)
	if isNoRows(err) {
		return tasks.Record{}, tasks.ErrNotFound
	}
	r.Data = data
	return r, err
}

const recordColumns = `id, data, version, updated_at, deleted_at IS NOT NULL`

func (t taskTx) Get(ctx context.Context, kind tasks.Kind, id string, lock bool) (tasks.Record, error) {
	sql := `SELECT ` + recordColumns + ` FROM ` + table(kind) + ` WHERE tenant_id = $1 AND owner_user_id = $2 AND id = $3`
	if lock {
		sql += ` FOR UPDATE`
	}
	return scanRecord(t.tx.QueryRow(ctx, sql, t.scope.TenantID, t.scope.UserID, id))
}

func (t taskTx) collect(ctx context.Context, sql string, args ...any) ([]tasks.Record, error) {
	rows, err := t.tx.Query(ctx, sql, args...)
	if err != nil {
		return nil, err
	}
	return pgx.CollectRows(rows, func(row pgx.CollectableRow) (tasks.Record, error) { return scanRecord(row) })
}

func (t taskTx) List(ctx context.Context, kind tasks.Kind, q tasks.Query) ([]tasks.Record, error) {
	if kind == tasks.Tasks && q.ListID != "" {
		return t.collect(ctx, `SELECT `+recordColumns+` FROM todo_tasks WHERE tenant_id = $1 AND owner_user_id = $2
			AND deleted_at IS NULL AND id > $3 AND list_id = $5 ORDER BY id LIMIT $4`, t.scope.TenantID, t.scope.UserID, q.After, q.Limit, q.ListID)
	}
	return t.collect(ctx, `SELECT `+recordColumns+` FROM `+table(kind)+` WHERE tenant_id = $1 AND owner_user_id = $2
		AND deleted_at IS NULL AND id > $3 ORDER BY id LIMIT $4`, t.scope.TenantID, t.scope.UserID, q.After, q.Limit)
}

func (t taskTx) Insert(ctx context.Context, kind tasks.Kind, r tasks.Record, c tasks.Columns, now time.Time) error {
	var err error
	switch kind {
	case tasks.Groups:
		_, err = t.tx.Exec(ctx, `INSERT INTO list_groups (tenant_id, owner_user_id, id, data, version, schema_version, created_at, updated_at)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $7)`, t.scope.TenantID, t.scope.UserID, r.ID, []byte(r.Data), r.Version, tasks.SchemaVersion, now)
	case tasks.Lists:
		_, err = t.tx.Exec(ctx, `INSERT INTO task_lists (tenant_id, owner_user_id, id, group_id, is_default, data, version, schema_version, created_at, updated_at)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $9)`, t.scope.TenantID, t.scope.UserID, r.ID, c.GroupID, c.IsDefault, []byte(r.Data), r.Version, tasks.SchemaVersion, now)
	default:
		_, err = t.tx.Exec(ctx, `INSERT INTO todo_tasks (tenant_id, owner_user_id, id, list_id, data, version, schema_version, created_at, updated_at)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $8)`, t.scope.TenantID, t.scope.UserID, r.ID, c.ListID, []byte(r.Data), r.Version, tasks.SchemaVersion, now)
	}
	return mapUnique(err)
}

func (t taskTx) Update(ctx context.Context, kind tasks.Kind, r tasks.Record, c tasks.Columns, now time.Time) error {
	var err error
	switch kind {
	case tasks.Groups:
		_, err = t.tx.Exec(ctx, `UPDATE list_groups SET data = $4, version = $5, updated_at = $6 WHERE tenant_id = $1 AND owner_user_id = $2 AND id = $3`,
			t.scope.TenantID, t.scope.UserID, r.ID, []byte(r.Data), r.Version, now)
	case tasks.Lists:
		_, err = t.tx.Exec(ctx, `UPDATE task_lists SET data = $4, version = $5, updated_at = $6, group_id = $7, is_default = $8
			WHERE tenant_id = $1 AND owner_user_id = $2 AND id = $3`, t.scope.TenantID, t.scope.UserID, r.ID, []byte(r.Data), r.Version, now, c.GroupID, c.IsDefault)
	default:
		_, err = t.tx.Exec(ctx, `UPDATE todo_tasks SET data = $4, version = $5, updated_at = $6, list_id = $7
			WHERE tenant_id = $1 AND owner_user_id = $2 AND id = $3`, t.scope.TenantID, t.scope.UserID, r.ID, []byte(r.Data), r.Version, now, c.ListID)
	}
	return mapUnique(err)
}

func (t taskTx) SoftDelete(ctx context.Context, kind tasks.Kind, id string, version int64, now time.Time) error {
	_, err := t.tx.Exec(ctx, `UPDATE `+table(kind)+` SET deleted_at = $5, updated_at = $5, version = $4
		WHERE tenant_id = $1 AND owner_user_id = $2 AND id = $3 AND deleted_at IS NULL`, t.scope.TenantID, t.scope.UserID, id, version, now)
	return err
}

func (t taskTx) ListsInGroup(ctx context.Context, groupID string) ([]tasks.Record, error) {
	return t.collect(ctx, `SELECT `+recordColumns+` FROM task_lists WHERE tenant_id = $1 AND owner_user_id = $2 AND group_id = $3
		AND deleted_at IS NULL ORDER BY id FOR UPDATE`, t.scope.TenantID, t.scope.UserID, groupID)
}

func (t taskTx) TasksInList(ctx context.Context, listID string) ([]tasks.Record, error) {
	return t.collect(ctx, `SELECT `+recordColumns+` FROM todo_tasks WHERE tenant_id = $1 AND owner_user_id = $2 AND list_id = $3
		AND deleted_at IS NULL ORDER BY id FOR UPDATE`, t.scope.TenantID, t.scope.UserID, listID)
}

func (t taskTx) DefaultListExists(ctx context.Context) (bool, error) {
	var exists bool
	err := t.tx.QueryRow(ctx, `SELECT EXISTS (SELECT 1 FROM task_lists WHERE tenant_id = $1 AND owner_user_id = $2 AND is_default AND deleted_at IS NULL)`,
		t.scope.TenantID, t.scope.UserID).Scan(&exists)
	return exists, err
}

func (t taskTx) InsertCompletion(ctx context.Context, c tasks.CompletionRecord, data json.RawMessage, completedAt, now time.Time) (bool, json.RawMessage, error) {
	tag, err := t.tx.Exec(ctx, `INSERT INTO task_completion_records (tenant_id, owner_user_id, id, task_id, completed_at, data, created_at)
		VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING`, t.scope.TenantID, t.scope.UserID, c.ID, c.TaskID, completedAt, []byte(data), now)
	if err != nil {
		return false, nil, err
	}
	if tag.RowsAffected() == 1 {
		return true, nil, nil
	}
	var existing []byte
	err = t.tx.QueryRow(ctx, `SELECT data FROM task_completion_records WHERE tenant_id = $1 AND owner_user_id = $2 AND id = $3`,
		t.scope.TenantID, t.scope.UserID, c.ID).Scan(&existing)
	return false, existing, err
}

func (t taskTx) Completions(ctx context.Context, before *tasks.Cursor, limit int) ([]json.RawMessage, *tasks.Cursor, error) {
	beforeAt, beforeID := time.Date(9999, 1, 1, 0, 0, 0, 0, time.UTC), ""
	if before != nil {
		beforeAt, beforeID = before.CompletedAt, before.ID
	}
	rows, err := t.tx.Query(ctx, `SELECT id, completed_at, data FROM task_completion_records
		WHERE tenant_id = $1 AND owner_user_id = $2 AND (completed_at < $3 OR (completed_at = $3 AND ($4 = '' OR id < $4)))
		ORDER BY completed_at DESC, id DESC LIMIT $5`, t.scope.TenantID, t.scope.UserID, beforeAt, beforeID, limit+1)
	if err != nil {
		return nil, nil, err
	}
	defer rows.Close()
	var items []json.RawMessage
	var last tasks.Cursor
	for rows.Next() {
		var id string
		var at time.Time
		var data []byte
		if err := rows.Scan(&id, &at, &data); err != nil {
			return nil, nil, err
		}
		if len(items) == limit {
			return items, &last, rows.Err()
		}
		items = append(items, data)
		last = tasks.Cursor{CompletedAt: at, ID: id}
	}
	return items, nil, rows.Err()
}

// mapUnique turns the one-default-list unique index into its domain error.
func mapUnique(err error) error {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) && pgErr.Code == "23505" && pgErr.ConstraintName == "task_lists_one_default" {
		return tasks.ErrDefaultExists
	}
	return err
}

// SettingsStore implements settings.Store.
type SettingsStore struct{ pool *pgxpool.Pool }

func NewSettingsStore(pool *pgxpool.Pool) *SettingsStore { return &SettingsStore{pool: pool} }

var _ settings.Store = (*SettingsStore)(nil)

func (s *SettingsStore) InScope(ctx context.Context, scope tasks.Scope, fn func(settings.Tx, string) error) error {
	return withMember(ctx, s.pool, scope, func(tx pgx.Tx, role string) error { return fn(settingsTx{tx: tx, scope: scope}, role) })
}

type settingsTx struct {
	tx    pgx.Tx
	scope tasks.Scope
}

func (t settingsTx) Get(ctx context.Context, level settings.Level, namespace string, lock bool) (settings.Doc, error) {
	doc := settings.Doc{Namespace: namespace, Data: json.RawMessage(`{}`)}
	var data []byte
	var err error
	suffix := ""
	if lock {
		suffix = " FOR UPDATE"
	}
	if level == settings.User {
		err = t.tx.QueryRow(ctx, `SELECT data, version, updated_at FROM user_configs WHERE tenant_id = $1 AND user_id = $2 AND namespace = $3`+suffix,
			t.scope.TenantID, t.scope.UserID, namespace).Scan(&data, &doc.Version, &doc.UpdatedAt)
	} else {
		err = t.tx.QueryRow(ctx, `SELECT data, version, updated_at FROM tenant_configs WHERE tenant_id = $1 AND namespace = $2`+suffix,
			t.scope.TenantID, namespace).Scan(&data, &doc.Version, &doc.UpdatedAt)
	}
	if isNoRows(err) {
		return doc, nil
	}
	doc.Data = data
	return doc, err
}

// Put inserts version 1 or updates from version-1; a concurrent first write loses with a version conflict.
func (t settingsTx) Put(ctx context.Context, level settings.Level, doc settings.Doc) error {
	var tag pgconn.CommandTag
	var err error
	switch {
	case level == settings.User && doc.Version == 1:
		tag, err = t.tx.Exec(ctx, `INSERT INTO user_configs (tenant_id, user_id, namespace, data, version, updated_at) VALUES ($1, $2, $3, $4, 1, $5)
			ON CONFLICT DO NOTHING`, t.scope.TenantID, t.scope.UserID, doc.Namespace, []byte(doc.Data), doc.UpdatedAt)
	case level == settings.User:
		tag, err = t.tx.Exec(ctx, `UPDATE user_configs SET data = $4, version = $5, updated_at = $6 WHERE tenant_id = $1 AND user_id = $2 AND namespace = $3 AND version = $5 - 1`,
			t.scope.TenantID, t.scope.UserID, doc.Namespace, []byte(doc.Data), doc.Version, doc.UpdatedAt)
	case doc.Version == 1:
		tag, err = t.tx.Exec(ctx, `INSERT INTO tenant_configs (tenant_id, namespace, data, version, updated_at) VALUES ($1, $2, $3, 1, $4) ON CONFLICT DO NOTHING`,
			t.scope.TenantID, doc.Namespace, []byte(doc.Data), doc.UpdatedAt)
	default:
		tag, err = t.tx.Exec(ctx, `UPDATE tenant_configs SET data = $3, version = $4, updated_at = $5 WHERE tenant_id = $1 AND namespace = $2 AND version = $4 - 1`,
			t.scope.TenantID, doc.Namespace, []byte(doc.Data), doc.Version, doc.UpdatedAt)
	}
	if err != nil {
		return err
	}
	if tag.RowsAffected() != 1 {
		return &tasks.ConflictError{Kind: "version"}
	}
	return nil
}
