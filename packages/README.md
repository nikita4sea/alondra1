# Shared contract

What every Alondra1 client and the backend agree on. UI code is not shared; the API and the sync semantics are.

| Path | Contents |
| --- | --- |
| `api-contract/openapi.yaml` | The `/v1` HTTP API (OpenAPI 3.1). Source of truth; generated clients will live next to it. |
| `api-contract/schemas/todo-v1.schema.json` | The task model of Alondra1 1.3.0 as JSON Schema, generated from and tested against `apps/windows/src/Alondra1.Core/Tasks/TodoModels.cs`. |
| `shared-models/fixtures/todo-v1/full-document.json` | Golden document written by the Windows serializer: month-end recurrence with anchor, weekly weekday sets, a reminder at a non-existent DST wall-clock time, ordered steps, attachments, grouped and tinted lists with emoji, and completion history for two occurrences of one recurring task. |

Checks: `Alondra1.Tests.ContractSchemaTests` (Windows) fails when the model, the schema, and the fixture disagree; set `ALONDRA1_UPDATE_FIXTURES=1` to rewrite the fixture after an intended model change. The backend's `httpapi` tests fail when the router and `openapi.yaml` disagree.
