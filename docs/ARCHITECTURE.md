# Architecture

<img src="images/alondra1-logo-64.png" alt="" width="48" align="right">

Alondra1 is a Windows desktop app (WPF, .NET 10) for mail, calendar, tasks, notes, and contacts. It is **local-first**:

- Settings, the local mail copy, local tasks, and credentials live in one folder in the user's profile.
- Nothing is sent anywhere except to the mail and organizer accounts the user adds.
- There is no telemetry. The Windows app does not contact any Alondra1 server.

An **optional cloud** is being built in the same repository ([ADR 0001](decisions/0001-opt-in-cloud.md), [backend spec](BACKEND_SPEC.md)): a Go API with PostgreSQL in [`backend/`](../backend), sharing the task contract in [`packages/`](../packages). So far it provides email one-time-code sign-in (Milestone B), spaces with roles and invitations (Milestone C), private storage of tasks, lists, groups, and settings (Milestone D), and multi-device sync with conflicts (Milestone E); the Windows app is not connected to it. See [Repository layout](#repository-layout) and [Cloud backend](#cloud-backend-in-progress).

This page explains how the code is organized and how data moves through it. [CODE-GUIDE.md](CODE-GUIDE.md) is the file-by-file reference, and [GUIDELINES.md](GUIDELINES.md) has the rules for changing it.

## Repository layout

| Path | Contents |
| --- | --- |
| `apps/windows/` | The Windows app: `Alondra1.slnx`, `src/`, `tests/`, `installer/`, `design/`. Everything below this section is about it. |
| `apps/android/`, `apps/ios/` | Placeholders; not started. |
| `backend/` | Go API (cloud, opt-in). |
| `packages/api-contract/` | OpenAPI contract and the task JSON Schema shared by all clients and the backend. |
| `packages/shared-models/` | Golden fixtures written by the Windows serializer. |
| `compose.yaml`, `.env.example`, `secrets/` | Docker Compose runtime for the backend (development); secrets are local files, never committed. |
| `docs/` | Documentation, decisions (`decisions/`), runbooks (`runbooks/`). |

## Projects

```mermaid
flowchart TD
    App["Alondra1.App<br/>WPF shell, view models, dialogs, themes"]
    Core["Alondra1.Core<br/>models, rules, ports (no I/O)"]
    Mail["Alondra1.Mail<br/>IMAP / POP3 / SMTP via MailKit"]
    Persistence["Alondra1.Persistence<br/>SQLite, DPAPI, files"]
    Providers["Alondra1.Providers<br/>OAuth, Microsoft Graph, Google APIs"]
    Tests["Alondra1.Tests<br/>xUnit"]
    App --> Core
    App --> Mail
    App --> Persistence
    App --> Providers
    Mail --> Core
    Persistence --> Core
    Providers --> Core
    Tests --> Core
    Tests --> Mail
    Tests --> Persistence
    Tests --> Providers
```

| Project | Target | Depends on | Responsibility |
| --- | --- | --- | --- |
| `Alondra1.Core` | `net10.0` | HtmlSanitizer (AngleSharp), for cleaning message HTML | Domain models, configuration schema, sync and security policies, the local task engine, and the **ports** (interfaces) that the other projects implement. No UI, file, database, or network code. |
| `Alondra1.Mail` | `net10.0` | Core, MailKit | `MailKitGateway` implements `IMailGateway`: probing, folder listing, incremental fetch, bodies, attachments, flags and moves, IDLE, POP deletion, SMTP send. |
| `Alondra1.Persistence` | `net10.0-windows` | Core, Microsoft.Data.Sqlite, ProtectedData | `SqlitePersonalStore` (local mail copy with FTS5 search, outbox, organizer cache), `SqliteTaskStore` (local tasks), `DpapiSecretStore`, `ConfigFileStore`, `AttachmentFiles`, `TaskFileStore`. |
| `Alondra1.Providers` | `net10.0` | Core, MSAL, Google.Apis.Auth | `OAuthService` (sign-in and token refresh for Microsoft 365 and Google) and `OrganizerGateway` (calendar, tasks, contacts, notes over Microsoft Graph and Google APIs). |
| `Alondra1.App` | `net10.0-windows10.0.19041.0` (WPF) | all of the above, CommunityToolkit.Mvvm, Microsoft.Web.WebView2 | Window shell, workspaces, view models, dialogs, themes, icons, backgrounds, reminders, and the composition root. Builds `Alondra1.exe`. |
| `Alondra1.Tests` | `net10.0-windows` | Core, Mail, Persistence, Providers | Acceptance, To-do, scale, and UI design tests. The UI tests read the XAML from the repository instead of referencing the App project. |

**The dependency rule:** only `Alondra1.App` knows every project. Adapters depend on `Alondra1.Core`, never on each other, and `Alondra1.Core` depends on nothing. Mail, storage, and providers can each be replaced or faked in tests without touching the rest.

## Ports and adapters

Ports are declared in `Alondra1.Core/Ports/Ports.cs`, except `ITaskStore`, which is in `Alondra1.Core/Tasks/TaskBoard.cs`.

| Port | Adapter | Notes |
| --- | --- | --- |
| `IMailGateway` | `Alondra1.Mail.MailKitGateway` | Stateless: every call gets the account and its secrets. |
| `IPersonalStore` | `Alondra1.Persistence.SqlitePersonalStore` | `personal.db`. Writes arrive as one `PersonalBatch` per transaction. |
| `ISessionCache` | `Alondra1.Core.Ports.SessionCache` | In-memory messages and bodies for Online accounts. |
| `ISecretStore` | `Alondra1.Persistence.DpapiSecretStore` | One DPAPI-protected file per account under `secrets\`. |
| `ITokenRefresher` | `Alondra1.Providers.OAuthService` | Refreshes Exchange and Graph tokens separately (`TokenAudience`). |
| `IOrganizerGateway` | `Alondra1.Providers.OrganizerGateway` | Calendar, tasks, contacts, and notes in the user's Microsoft or Google account. |
| `ITaskStore` | `Alondra1.Persistence.SqliteTaskStore` | `todo.db`. |
| `IClock` | `Alondra1.Core.Ports.SystemClock` | Tests pass a fixed clock. |
| `IConflictSink` | `NullConflicts` (App) | Reports when both copies of a conflicting item were kept. |

## Composition and startup

1. `App.OnStartup` (`App.xaml.cs`) reads the command line and installs a crash handler that writes `logs\crash.txt`. It then calls `AppServices.CreateAsync(home)`.
2. `AppServices` (at the end of `ViewModels/MainViewModel.cs`) is the **composition root**. It creates the data folder, loads `config.json`, opens `personal.db` and `todo.db`, and wires every adapter into `SyncCoordinator` and `TaskBoard`.
   - If `todo.db` cannot be opened (for example, a file written by a newer version), only the To-do workspace is disabled. Mail and the other workspaces keep working.
3. `MainWindow` creates the `MainViewModel` and applies the theme and background. On first launch with no accounts it shows the welcome dialog: sample data, import, or add an account.
4. `--demo` (and `--perf`) point `home` at a temporary folder filled with labeled sample data, so a demo never touches real settings.

## Accounts and modes

Each `AccountConfig` has a kind (`Microsoft365`, `Gmail`, `Manual`), incoming settings (IMAP or POP3), outgoing SMTP settings, an authentication kind (password or OAuth), and a **mode**:

| Mode | Where messages live | Typical use |
| --- | --- | --- |
| **Online** | Only in memory (`SessionCache`) while the app runs. Nothing from the mailbox is written to disk. | Light use: the server remains the only copy. |
| **Personal** | In `personal.db`, with full-text search, attachments on disk, and a durable outbox. | Offline reading, POP3 accounts, large mailboxes. |

Switching modes goes through `ModeTransition.Evaluate`, which refuses to drop queued operations unless the user confirms. Leaving Personal mode deletes that account's local rows.

## Mail synchronization

`SyncCoordinator` (`Alondra1.Core/Sync`) runs all mail work for one account at a time behind a per-account `SemaphoreSlim`:

1. Load the secrets and ask `ConnectionPolicy` whether connecting is allowed.
   - Plaintext needs explicit confirmation.
   - OAuth tokens are never sent to an unencrypted server.
   - A missing password or token stops the sync with a readable reason.
2. Refresh tokens through `ITokenRefresher`. Provider errors are translated by `ProviderDiagnostics`.
3. Fetch changes per folder with a stored checkpoint.
   - **IMAP:** `ImapReconciler` resets the local folder when `UIDVALIDITY` changes.
   - **POP3:** `Pop3Policy` deduplicates by UIDL. It deletes from the server only when the user enabled that, consented, and the local commit is durable.
4. Online accounts write into the `SessionCache`. Personal accounts commit one `PersonalBatch` per transaction.

**Sending** goes through `SmtpStateMachine`:
- A failure before the server accepted the message is retried automatically, with backoff.
- A connection lost after the message was handed over is **Uncertain**. It is kept in the outbox and never resent automatically, because that could send the same mail twice.
- `SentCopyPolicy` decides whether Alondra1 appends a copy to Sent or the provider already does.
- `MessageIdentity` gives every message a stable `Message-ID` (`…@alondra1.local`) so a retry can be recognized.

Local actions (read or unread, archive, delete, move) are applied locally first. For Personal accounts they are queued as `QueuedOperation`s and replayed against the server. `ConflictPolicy` resolves an item changed on both sides:
- a draft body changed in both places keeps both versions;
- flags changed in both places are merged;
- otherwise the side that changed wins.

## Organizer: calendar, notes, contacts, account tasks

`OrganizerGateway` talks to Microsoft Graph and to the Google Calendar, Tasks, People, and Drive APIs:
- Features are turned on per installation (Settings → Connections → Features).
- `GetStatusAsync` reports a `CapabilityStatus`, for example "consent missing", instead of failing silently.
- Notes are stored as files in the provider's **app folder** (OneDrive app folder or Drive app data), not OneNote or Keep.
- For Personal accounts, organizer items are cached in `personal.db`.

The Calendar workspace shows account events. Local task dates appear in the To-do workspace's own calendar.

## Local To-do

The To-do workspace works without any account or network access.

| Layer | Types |
| --- | --- |
| Domain (`Alondra1.Core/Tasks`) | `TaskBoard` (the single entry point for changes), `TodoModels`, `Recurrence`, `Reminders`, `TaskQueries` (smart views, search, sort), `NaturalDateParser`, `TodoTransfer` (JSON, CSV, iCalendar) |
| Persistence | `SqliteTaskStore` (versioned migrations, online backup and restore), `TaskFileStore` (copied attachments) |
| Presentation (`Alondra1.App/Tasks`) | `TodoViewModel`, `TodoView`, `TodoToolbar`, `TodoDialogs`, `TodoDataActions`, `TodoPrinter`, `ReminderNotifier` |

A change runs in this order:
1. `TaskBoard` builds a `TodoChangeSet` and its inverse.
2. It writes the change set in **one SQLite transaction**.
3. Only after that succeeds does it update memory and raise `Changed`.

If the write fails, memory stays as it was. The inverse goes onto a 50-step undo stack. [TODO.md](TODO.md) has the full design.

## User interface

| Part | Where | What it does |
| --- | --- | --- |
| Shell | `MainWindow.xaml(.cs)` | Two rows above the content: the header (logo, workspaces, search, Settings, window buttons) and a toolbar for the open workspace. The window is borderless (`WindowStyle="None"`) and draws its own caption and window buttons. |
| View model | `ViewModels/MainViewModel.cs` | Workspace selection, accounts, folders, message list, reading pane, calendar, notes, contacts, sync status, responsive breakpoints. |
| Mail layout | `Shell/MailDockPanel.cs`, `Core/Layout/LayoutArranger.cs` | Docks the folders, list, and reader on any edge, giving 25 combinations, with resizable splitters. `LayoutArranger` holds the geometry and is unit-tested. |
| Theme | `ThemeService.cs`, `Themes/Light.xaml`, `Themes/Dark.xaml`, `Themes/Controls.xaml` | Color tokens per theme, shape and size tokens, and the control styles. The user can override colors. |
| Title bars | `Shell/ShellWindow.cs` | Colors native dialog title bars (DWM) to match the theme. |
| Icons | `Themes/Icons.xaml`, `Controls/Alondra1Icon.cs` | 62 stroke icons drawn from SVG path data. |
| Backgrounds | `Shell/BackgroundService.cs`, `Shell/BackgroundEditor.cs` | Built-in and custom pictures, processed locally (scale, blur, tone). |
| Dialogs | `Dialogs.cs`, `Tasks/TodoDialogs.cs` | Built in code on one shared frame: welcome, settings, account, compose, import, palette editor, alerts. |
| Snapshots | `Shell/Snapshot.cs` | `--snapshot` renders the window to a PNG for docs and visual checks. |

[UI-DESIGN.md](UI-DESIGN.md) covers the design system.

## Data folder

Everything Alondra1 stores is under `%LOCALAPPDATA%\alondra1`:

| Path | Contents |
| --- | --- |
| `config.json`, `config.json.bak` | Settings and accounts (no secrets). The previous good file is kept for recovery. |
| `secrets\` | Passwords and OAuth tokens, protected with Windows DPAPI for the current user. |
| `personal.db` | Local mail for Personal accounts, FTS5 search index, outbox, organizer cache. |
| `files\` | Downloaded mail attachments (pinned or cache). |
| `todo.db` | Local tasks, lists, groups, history, preferences. |
| `task-files\` | Files attached to local tasks (copies). |
| `backups\` | Daily to-do backups (`*.atodo`, last 7 kept) and safety copies made before a restore. |
| `backgrounds\` | The user's own background picture, re-encoded. |
| `logs\crash.txt`, `perf.txt` | Last unhandled error and `--perf` timing output. |

The installer puts the program in `%LOCALAPPDATA%\Programs\Alondra1`. It never touches the data folder, so updating or uninstalling keeps all data.

## Security model

- **Secrets:** stored only in `secrets\`, encrypted with DPAPI (`CurrentUser` scope plus an app-specific entropy). They are never written to `config.json` or included in an export.
- **OAuth:** public clients only. Alondra1 never stores a client secret. The user enters their own client ids ([OAUTH-SETUP.md](OAUTH-SETUP.md)).
- **Transport:** implicit TLS or required STARTTLS by default, verified with the operating system's certificate check. Unencrypted connections need explicit, repeated confirmation (`ConnectionPolicy`).
- **Mail content:** `Alondra1.Mail.MimeBody` picks the HTML and text bodies from the MIME tree, embeds `cid:` pictures as `data:` URIs (so they show offline), and lists attachments. `Core/Html/MailHtml` cleans the HTML with an allow-list sanitizer (HtmlSanitizer on AngleSharp) and wraps it in a document with a strict Content Security Policy; `App/Controls/MailBodyView` shows it in an isolated WebView2 with scripts off. Remote images load only by the user's choice (`ReaderPolicy`), never through a proxy. Bodies and attachment bytes are never logged.
- **Files:** stored attachment paths are relative and checked to stay inside their folder before they are opened. Imported data is validated before anything is written.

## Threading and errors

- Libraries use `async` with `ConfigureAwait(false)`. The App resumes on the UI thread, so view-model state, `TaskBoard` changes, and `Changed` events all happen on the dispatcher.
- Sync for each account is serialized. Different accounts can sync in parallel.
- Expected failures (network, authentication, storage) become a status, such as "Offline" or "Sync failed", or a readable alert. An unhandled exception is written to `logs\crash.txt` and shown once before the app closes.

## Cloud backend (in progress)

The backend is a stateless Go HTTP API with PostgreSQL, run with Docker Compose for now ([runbook](runbooks/compose.md)). It is a separate implementation; the Windows app stays C#/WPF and keeps working without it.

- **Implemented (Milestone B):** configuration from secret files only; PostgreSQL connection with retry and forward-only migrations (advisory lock, downgrade refusal); SMTP delivery; email one-time-code sign-in; 15-minute access tokens; rotating 30-day refresh tokens per device with reuse detection; logout and logout-all; audit log. 
- **Implemented (Milestone C):** Personal, Family, and Organization spaces; owner, admin, and member roles; emailed invitations bound to the invited address; member removal, leaving, and ownership transfer with a last-owner guarantee; device list and revocation; account deletion that releases the address ([retention](decisions/0003-retention.md)). Details: [backend/README.md](../backend/README.md).
- **Implemented (Milestone D):** tasks, lists, groups, and completion history in the todo-v1 format, private per user within a space; versions with `If-Match` and conflict copies; reference checks and delete cascades; allowlisted portable settings; account export. Decisions: [ADR 0004](decisions/0004-cloud-task-storage.md).
- **Implemented (Milestone E):** change log written in the same transaction as every write (commit-ordered per space), paged bootstrap with a watermark, signed change cursors bound to user and space with `410` below the retention floor, idempotent push with per-device receipts, visible conflicts including delete-versus-edit, and a reference offline-first client used by the multi-device tests. Client rules: [ADR 0005](decisions/0005-sync-protocol.md).
- **Implemented (Milestone F):** retention job enforcing ADR 0003 (with cursor floors), Prometheus metrics on an internal port, alert rules, zero-downtime token-key rotation, readiness draining, a Caddy TLS proxy profile, encrypted backups (age, public key only on the server) with an automatic restore check of every dump, and a release pipeline that tests, scans, signs, and publishes images by digest behind a human approval. Runbooks: [production](runbooks/production.md), [backups](runbooks/backup-restore.md), [monitoring](runbooks/monitoring.md), [release](runbooks/release.md).
- **Next:** the Windows cloud adapter (outbox in `todo.db`, remote apply through `TaskBoard`, Connect Cloud and space UI, conflict queue).
- **Boundary with local tasks:** [ADR 0002](decisions/0002-local-cloud-task-boundary.md). `TaskBoard` stays the only task writer; a separate cloud adapter project will talk to the API; mail, calendar, contacts, provider notes, account tasks, and secrets are never uploaded.
- **Contract:** [openapi.yaml](../packages/api-contract/openapi.yaml) and [todo-v1.schema.json](../packages/api-contract/schemas/todo-v1.schema.json). Tests on both sides fail when code and contract disagree.

## Build and packaging

- From `apps/windows`: `dotnet build Alondra1.slnx` and `dotnet test Alondra1.slnx`.
- `apps/windows/installer/publish.ps1` publishes a self-contained, single-file win-x64 build to `apps\windows\artifacts\win-x64`, then compiles `apps/windows/installer/alondra1.iss` with Inno Setup 6 into `apps\windows\artifacts\Alondra1-setup.exe`.
- The setup is per-user and needs no administrator rights. Its branded wizard artwork comes from `apps/windows/installer/branding/make-branding.ps1`.
