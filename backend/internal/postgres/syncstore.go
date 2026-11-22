package postgres

import (
	"context"
	"encoding/json"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"alondra1/backend/internal/cloudsync"
	"alondra1/backend/internal/settings"
	"alondra1/backend/internal/tasks"
)

// SyncStore implements cloudsync.Store (read side of sync).
type SyncStore struct{ pool *pgxpool.Pool }

func NewSyncStore(pool *pgxpool.Pool) *SyncStore { return &SyncStore{pool: pool} }

var _ cloudsync.Store = (*SyncStore)(nil)

func (s *SyncStore) InScope(ctx context.Context, scope tasks.Scope, fn func(cloudsync.Tx) error) error {
	return withMember(ctx, s.pool, scope, func(tx pgx.Tx, _ string) error { return fn(syncTx{tx: tx, scope: scope}) })
}

type syncTx struct {
	tx    pgx.Tx
	scope tasks.Scope
}

func (t syncTx) MaxSequence(ctx context.Context) (int64, error) {
	var max int64
	err := t.tx.QueryRow(ctx, `SELECT coalesce(max(sequence), 0) FROM change_log WHERE tenant_id = $1`, t.scope.TenantID).Scan(&max)
	return max, err
}

func (t syncTx) Floor(ctx context.Context) (int64, error) {
	var floor int64
	err := t.tx.QueryRow(ctx, `SELECT coalesce((SELECT floor_sequence FROM change_log_floor WHERE tenant_id = $1), 0)`, t.scope.TenantID).Scan(&floor)
	return floor, err
}

func (t syncTx) Changes(ctx context.Context, after, upTo int64, limit int) ([]cloudsync.Entry, error) {
	rows, err := t.tx.Query(ctx, `SELECT sequence, payload FROM change_log
		WHERE tenant_id = $1 AND sequence > $2 AND sequence <= $3 AND (visibility_user_id IS NULL OR visibility_user_id = $4)
		ORDER BY sequence LIMIT $5`, t.scope.TenantID, after, upTo, t.scope.UserID, limit)
	if err != nil {
		return nil, err
	}
	return pgx.CollectRows(rows, func(row pgx.CollectableRow) (cloudsync.Entry, error) {
		var e cloudsync.Entry
		var payload []byte
		err := row.Scan(&e.Sequence, &payload)
		e.Payload = payload
		return e, err
	})
}

func upsertItem(resourceType, id string, record any) (json.RawMessage, error) {
	return json.Marshal(map[string]any{"resource_type": resourceType, "action": "upsert", "resource_id": id, "record": record})
}

// Snapshot walks the stages in order with keyset pagination inside each stage.
func (t syncTx) Snapshot(ctx context.Context, pos cloudsync.Position, limit int) ([]json.RawMessage, *cloudsync.Position, error) {
	var items []json.RawMessage
	for stage := pos.Stage; stage <= cloudsync.StageTenantConfigs; stage++ {
		after := ""
		if stage == pos.Stage {
			after = pos.After
		}
		room := limit - len(items)
		page, last, err := t.stage(ctx, stage, after, room)
		if err != nil {
			return nil, nil, err
		}
		items = append(items, page...)
		if len(page) == room {
			return items, &cloudsync.Position{Stage: stage, After: last}, nil
		}
	}
	return items, nil, nil
}

func (t syncTx) stage(ctx context.Context, stage int, after string, limit int) ([]json.RawMessage, string, error) {
	var items []json.RawMessage
	last := after
	switch stage {
	case cloudsync.StageGroups, cloudsync.StageLists, cloudsync.StageTasks:
		kind := tasks.Kind(stage)
		rows, err := t.tx.Query(ctx, `SELECT `+recordColumns+` FROM `+table(kind)+` WHERE tenant_id = $1 AND owner_user_id = $2
			AND deleted_at IS NULL AND id > $3 ORDER BY id LIMIT $4`, t.scope.TenantID, t.scope.UserID, after, limit)
		if err != nil {
			return nil, "", err
		}
		records, err := pgx.CollectRows(rows, func(row pgx.CollectableRow) (tasks.Record, error) { return scanRecord(row) })
		if err != nil {
			return nil, "", err
		}
		for _, r := range records {
			item, err := upsertItem(kind.String(), r.ID, tasks.Envelope(kind, r))
			if err != nil {
				return nil, "", err
			}
			items, last = append(items, item), r.ID
		}
	case cloudsync.StageCompletions:
		rows, err := t.tx.Query(ctx, `SELECT id, data FROM task_completion_records WHERE tenant_id = $1 AND owner_user_id = $2 AND id > $3
			ORDER BY id LIMIT $4`, t.scope.TenantID, t.scope.UserID, after, limit)
		if err != nil {
			return nil, "", err
		}
		defer rows.Close()
		for rows.Next() {
			var id string
			var data []byte
			if err := rows.Scan(&id, &data); err != nil {
				return nil, "", err
			}
			item, err := upsertItem("completion", id, json.RawMessage(data))
			if err != nil {
				return nil, "", err
			}
			items, last = append(items, item), id
		}
		if err := rows.Err(); err != nil {
			return nil, "", err
		}
	case cloudsync.StageUserConfigs, cloudsync.StageTenantConfigs:
		level, sql := settings.User, `SELECT namespace, data, version, updated_at FROM user_configs WHERE tenant_id = $1 AND user_id = $2 AND namespace > $3 ORDER BY namespace LIMIT $4`
		args := []any{t.scope.TenantID, t.scope.UserID, after, limit}
		if stage == cloudsync.StageTenantConfigs {
			level, sql = settings.Space, `SELECT namespace, data, version, updated_at FROM tenant_configs WHERE tenant_id = $1 AND namespace > $2 ORDER BY namespace LIMIT $3`
			args = []any{t.scope.TenantID, after, limit}
		}
		rows, err := t.tx.Query(ctx, sql, args...)
		if err != nil {
			return nil, "", err
		}
		defer rows.Close()
		for rows.Next() {
			var d settings.Doc
			var data []byte
			var updated time.Time
			if err := rows.Scan(&d.Namespace, &data, &d.Version, &updated); err != nil {
				return nil, "", err
			}
			d.Data, d.UpdatedAt = data, updated
			item, err := upsertItem(settings.ResourceType(level), d.Namespace, settings.Record(d))
			if err != nil {
				return nil, "", err
			}
			items, last = append(items, item), d.Namespace
		}
		if err := rows.Err(); err != nil {
			return nil, "", err
		}
	}
	return items, last, nil
}
