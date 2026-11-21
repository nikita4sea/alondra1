# Code guide

<img src="images/alondra1-logo-64.png" alt="" width="48" align="right">

This guide is a reference to the source tree: what each file does, where to make common changes, the command-line switches, and the tests. Read [ARCHITECTURE.md](ARCHITECTURE.md) first for the overall design.

## Getting started

```powershell
cd apps/windows
dotnet build Alondra1.slnx
dotnet test Alondra1.slnx
dotnet run --project src/Alondra1.App -- --demo
```

- **SDK:** .NET 10. If it is installed only under `%LOCALAPPDATA%\dotnet` and is not on `PATH`, set `DOTNET_ROOT` to that folder before starting a built `Alondra1.exe`.
- **Demo mode:** `--demo` uses labeled sample data in a temporary folder. Your real `%LOCALAPPDATA%\alondra1` is never touched.

## Repository layout

| Path | Contents |
| --- | --- |
| `apps/windows/Alondra1.slnx` | Windows solution (five source projects and the tests). Run `dotnet` commands from `apps/windows`. |
| `apps/windows/src/` | Source projects; see below. |
| `apps/windows/tests/Alondra1.Tests/` | xUnit tests. |
| `apps/windows/installer/` | `publish.ps1` (build plus setup), `alondra1.iss` (Inno Setup script), `branding/` (setup artwork and its generator). |
| `apps/windows/design/` | `backgrounds/` (generator for the built-in background pictures), `compact-mail-reference.html` (the mail layout reference). |
| `docs/` | Documentation, `images/` (logo and banner), `screenshots/`. |
| `apps/windows/artifacts/` | Build output: `win-x64/` and `Alondra1-setup.exe`. Ignored by git. |
| `backend/` | Go cloud API (opt-in, in progress); see [backend/README.md](../backend/README.md). |
| `packages/` | Shared API contract and task schema with fixtures; see [packages/README.md](../packages/README.md). |
| `compose.yaml`, `.env.example`, `secrets/`, `scripts/` | Docker Compose runtime for the backend and its local secret generator ([runbook](runbooks/compose.md)). |

## Alondra1.Core

Plain .NET with no I/O. Everything here can be unit-tested directly.

| File | Contents |
| --- | --- |
| `Config/Configuration.cs` | The configuration schema: `AppConfig`, `AppearanceSettings`, `BackgroundSettings` (with clamping and upgrades in `Normalized()`), `LayoutSettings`, `OAuthClientSettings`, `AccountConfig`, `IncomingSettings`, `OutgoingSettings`. Also `ProviderPresets` (Microsoft 365 and Gmail endpoints), `ConfigJson` (serializer options), `ConfigSchema` (version), and `ConfigWorkflow` (validation, import plans, plaintext and OAuth checks). |
| `Demo/SampleCatalog.cs` | Labeled sample mail, events, notes, and contacts for `--demo`. |
| `Html/HtmlMailSanitizer.cs` | `ToPlainText` (used for mail bodies) and `Sanitize` (strips scripts, event handlers, dangerous URLs, and remote images). |
| `Layout/LayoutArranger.cs` | Pure geometry for the dockable mail panes: a tree of `LayoutLeaf` and `LayoutSplit` nodes is turned into `LayoutFrames` and splitter hit areas. |
| `Mail/MailModels.cs` | `StoredMessage`, `StoredBody`, `StoredAttachment`, `QueuedOperation`, `OutgoingMail`, `RemoteFolder`, `RemoteSyncBatch`, `PersonalBatch`, `RemoteMailCommand`, results and failures, `MessageIdentity` (stable Message-ID), `MailText`. |
| `Organizer/OrganizerModels.cs` | `CalendarEntry`, `TaskEntry`, `ContactEntry`, `NoteEntry`, `CapabilityStatus`, `OrganizerFeature`. |
| `Ports/Ports.cs` | The interfaces implemented by the adapters, plus `SystemClock`, `SessionCache`, and `SyncReport`. |
| `Security/ConnectionPolicy.cs` | `SecretBundle`, `OAuthToken`, `TokenAudience`, and `ConnectionPolicy.Evaluate`, which decides whether an account may connect. |
| `Sync/MailPolicies.cs` | Small, tested decision tables: `ImapReconciler`, `Pop3Policy`, `SmtpStateMachine`, `SentCopyPolicy`, `ConflictPolicy`, `ModeTransition`, `TlsMapping`, `ProviderDiagnostics`. |
| `Sync/SyncCoordinator.cs` | Sync, send, retry, drafts, local commands, and the outbox for one account at a time. |
| `Tasks/TodoModels.cs` | Task entities (`TodoTask`, `Subtask`, `TaskList`, `ListGroup`, `RecurrenceRule`, `TaskReminder`, `TaskAttachment`, `CompletionRecord`, `TaskPreferences`) and `TodoChangeSet`. |
| `Tasks/TaskBoard.cs` | `ITaskStore` and `TaskBoard`, the only way to change tasks. Each change is a transaction with undo. |
| `Tasks/TaskQueries.cs` | Smart views, search (accent-insensitive), filters, and sorting. |
| `Tasks/Recurrence.cs` | Next-occurrence calculation on calendar dates. |
| `Tasks/Reminders.cs` | Wall-clock reminder times in the local zone, including the DST gap and overlap. |
| `Tasks/NaturalDateParser.cs` | Offline parser for quick add ("Call Sam tomorrow 3pm", "every month on the 1st"). |
| `Tasks/TodoTransfer.cs` | JSON export and import (`alondra1-todo` format, validated and with ids remapped), CSV, and iCalendar (`VTODO`). |

## Alondra1.Mail

| File | Contents |
| --- | --- |
| `MailKitGateway.cs` | `IMailGateway` over MailKit. Covers IMAP and POP3 probing and fetch (checkpointed by UID or UIDL), bodies converted to text, attachment streaming with progress, flag and move commands, IDLE, and SMTP send with classified failures. |

## Alondra1.Persistence

| File | Contents |
| --- | --- |
| `SqlitePersonalStore.cs` | `personal.db`: messages, bodies, FTS5 index (`messages_fts`), attachments, checkpoints, queued operations, organizer cache. Schema versions use `PRAGMA user_version`. |
| `SqliteTaskStore.cs` | `todo.db`: one JSON column per entity plus indexed key columns. Includes an ordered `Migrations` list, a pre-migration copy, refusal of newer files, online backup, and validated restore. |
| `DpapiSecretStore.cs` | One DPAPI-protected file per account. |
| `ConfigFileStore.cs` | Atomic `config.json` writes with a `.bak` of the last good file. A file from a newer version is refused, never overwritten. |
| `AttachmentFiles.cs` | Mail attachment files under `files\`. |
| `TaskFileStore.cs` | Task attachments under `task-files\<task id>\`. Checks every path and removes orphaned files at startup. |

## Alondra1.Providers

| File | Contents |
| --- | --- |
| `OAuthService.cs` | MSAL (Microsoft) and Google sign-in, separate token sets for mail protocols and for Graph or Google APIs, and refresh (`ITokenRefresher`). |
| `OrganizerGateway.cs` | `IOrganizerGateway` for Microsoft Graph and Google Calendar, Tasks, People, and Drive app data. |

## Alondra1.App

| File | Contents |
| --- | --- |
| `App.xaml(.cs)` | Startup, crash log, command line, demo and theme switches. |
| `MainWindow.xaml(.cs)` | The shell: header, toolbars, mail list and reader, calendar, notes, contacts, the reminder overlay, keyboard shortcuts, snapshot mode. |
| `ViewModels/MainViewModel.cs` | `MainViewModel` (all non-To-do workspaces, responsive breakpoints, sync status), `WorkspaceChoice`, `AppServices` (composition root). |
| `Dialogs.cs` | Shared dialog frame and builders, plus Welcome, Settings (Appearance, Background, Accounts, Connections, Data), Import, Account, Compose, palette editor, and `Alert`. |
| `ThemeService.cs` | Applies Light or Dark tokens and custom colors, and makes panels translucent when a background is on. |
| `Converters.cs` | Value converters used by the XAML. |
| `Controls/Alondra1Icon.cs` | The icon element (`Kind`, `IconSize`). |
| `Controls/MailColumns.cs` | The one column layout shared by the mail column header and message rows (`ui:MailColumns.Apply`). |
| `Shell/MailDockPanel.cs` | The panel that hosts the folders, list, and reader, using `LayoutArranger`. |
| `Shell/BackgroundService.cs` | Loads, processes, imports, and deletes background pictures. |
| `Shell/BackgroundEditor.cs` | The Background section of Settings. |
| `Shell/ShellWindow.cs` | Themes native title bars with DWM. |
| `Shell/Snapshot.cs` | Renders windows and dialogs to PNG and measures control heights. |
| `Tasks/TodoViewModel.cs` | To-do state: sidebar, rows, details, calendar, summary, preferences, commands. |
| `Tasks/TodoView.xaml(.cs)` | To-do layout, drag and drop, keyboard. |
| `Tasks/TodoToolbar.xaml(.cs)` | Sort, filter, Important, undo, and More menu. |
| `Tasks/TodoItems.cs` | Row and cell view models (`TaskItemVm`, `SidebarItem`, `CalendarCellVm`, and others), and `TodoFormat`. |
| `Tasks/TodoDialogs.cs` | List, group, recurrence, and reminder dialogs. |
| `Tasks/TodoDataActions.cs` | Export, import, backup, restore, automatic daily backup. |
| `Tasks/TodoPrinter.cs` | Print-friendly checklist. |
| `Tasks/ReminderNotifier.cs` | 15-second reminder check, the reminder bar, and the Windows notification. |
| `Tasks/TodoSamples.cs` | Sample tasks for demo mode. |
| `Themes/Light.xaml`, `Dark.xaml` | Color tokens. Both files must define the same keys. |
| `Themes/Controls.xaml` | Size, radius, and font tokens and every control style. |
| `Themes/Icons.xaml` | Icon path data (24 × 24). |
| `Assets/` | `alondra1.ico`, `logo-64.png`, `Backgrounds/*.jpg`. |

## Common changes

### Add a setting

1. Add the property with a default to the right class in `Config/Configuration.cs`. If old files need a different value, handle it in `Normalized()`.
2. Add the control in `Dialogs.ShowSettings` and copy its value back in the Save handler.
3. Add a test in `AcceptanceTests` or `UiDesignTests` for the default and for loading an older file.

### Add a color or brush

1. Add the key to **both** `Themes/Light.xaml` and `Themes/Dark.xaml`.
2. Use it with `{DynamicResource Key}` so theme changes apply live.
3. Run the tests. `UiDesignTests` fails if the palettes differ or text contrast is too low.

### Add an icon

1. Add a `Geometry` with the icon name to `Themes/Icons.xaml`. Draw it on a 24 × 24 grid as a stroke, not a fill.
2. Use it with `<ui:Alondra1Icon Kind="Name" IconSize="16"/>` inside a button that has `AutomationProperties.Name` and a tooltip.

### Change the task schema

1. Add a field to the entity in `TodoModels.cs`. Fields are stored as JSON, so a new optional field needs no migration.
2. If a new indexed column or table is needed, **append** a step to `SqliteTaskStore.Migrations`. Never edit an existing step. Add a migration test next to `Migration_upgrades_old_files_and_keeps_a_backup`.
3. Make the change through a new `TaskBoard` method that builds a `TodoChangeSet` and its inverse.

### Add a smart view or sort

1. Extend the enum and the `SmartViews.Includes` or sort switch in `TaskQueries.cs`.
2. Add the sidebar entry in `TodoViewModel`.
3. Add a test in `TodoTests`.

### Add a dialog

Build it with `Dialogs.Create(title, owner, width, height)` and the shared helpers: `Field`, `Row`, `Check`, `Footer`, `Primary`, and `Cancel`. That gives it the themed frame, title bar, and footer. Use `Dialogs.Alert` instead of `MessageBox`.

### Change the mail protocol code

Put decisions in a policy in `Sync/MailPolicies.cs` (pure and testable). Keep `MailKitGateway` limited to the protocol calls, and keep `SyncCoordinator` limited to the order of steps.

### Rebuild generated artwork

- Background pictures: `apps/windows/design/backgrounds/generate.ps1`.
- Setup wizard images and the README banner: `powershell -ExecutionPolicy Bypass -File installer/branding/make-branding.ps1`.

## Command-line switches

| Switch | Effect |
| --- | --- |
| `--demo` | Labeled sample data in a temporary data folder. No provider is contacted. |
| `--home=<folder>` | Use another data folder instead of `%LOCALAPPDATA%\alondra1`. |
| `--perf` | Seed 10,000 local messages and write timings to `perf.txt`. |
| `--theme=light\|dark` | Force a theme (demo only). |
| `--snapshot=<file.png>` | Render the window to a PNG after start-up. A `.txt` with measured heights is written next to it. |
| `--open=<workspace>` | Open `Mail`, `Calendar`, `To-do`, `Notes`, `Contacts`, or `Chat`. |
| `--width=<px>`, `--height=<px>` | Window size for a snapshot. |
| `--select=<task title>`, `--detail` | In To-do, select a task, or open the first task's details. |
| `--dialog=<name>` | Open `compose`, `settings`, `background`, `account`, `welcome`, `list`, or `alert` and capture it. |
| `--exit` | Close after the snapshot. |

Example: `Alondra1.exe --demo --open=To-do --theme=dark --snapshot=todo.png --exit`.

## Tests

`dotnet test Alondra1.slnx` (from `apps/windows`) runs 104 tests. One test, the 10 GB mailbox, is skipped unless `ALONDRA1_SCALE_TEST_ROOT` names a scratch folder.

| File | Covers |
| --- | --- |
| `AcceptanceTests.cs` | Config recovery and newer-file refusal; export without secrets; import rules; plaintext and OAuth blocking; provider presets; IMAP UIDVALIDITY; POP UIDL and deletion consent; SMTP uncertainty and outbox across restart; mode switch with pending sends; Online accounts not storing mail; HTML sanitizing; all 25 dock layouts; 10,000 messages indexed and searched. |
| `MailboxScaleTests.cs` | Search index rebuild, flags refresh keeping bodies searchable, optional 10 GB mailbox. |
| `TodoTests.cs` | Task engine, recurrence, DST reminders, natural-language dates, smart views, calendar, persistence and migration, backup and restore, import and export, attachments, 10,000 tasks, and a check that task code uses no network APIs. |
| `UiDesignTests.cs` | Two-row shell, header and toolbar heights, shared control styles, matching palettes, WCAG contrast, icon validity, background defaults and checks, themed dialogs, no network addresses in app markup. |

Tests use temporary folders (`Directory.CreateTempSubdirectory("alondra1-…")`) and a fixed clock where time matters. They never touch the real data folder or the network.
