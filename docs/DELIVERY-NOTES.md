# Delivery notes

<img src="images/alondra1-logo-64.png" alt="" width="48" align="right">

This is the release history, with what was verified for each release and what was not. Newest first.

**Built with:**
- .NET 10 SDK 10.0.404 (`net10.0` / `net10.0-windows`) and WPF;
- MailKit 5, Microsoft.Data.Sqlite 11, MSAL, and Google.Apis.Auth;
- Inno Setup 6.7 for the installer.

**Test machine:** one PC running Windows build 10.0.26300 (DisplayVersion 26H2). That is the only OS any release was launched on.

## Unreleased: monorepo and cloud backend, Milestones A and B (2026-11-21)

Work toward the optional cloud in [BACKEND_SPEC.md](BACKEND_SPEC.md). The Windows app does not use the backend yet, and nothing here is production-ready.

- **Monorepo (Milestone A):**
  - The Windows app moved to `apps/windows` with history-preserving moves. Its paths, the Inno Setup `AppId`, and the `%LOCALAPPDATA%\alondra1` data folder are unchanged.
  - Decisions: [0001 opt-in cloud](decisions/0001-opt-in-cloud.md) and [0002 local/cloud task boundary](decisions/0002-local-cloud-task-boundary.md).
  - Shared contract: `packages/api-contract/schemas/todo-v1.schema.json`, generated from `TodoModels.cs`, plus a golden fixture written by the Windows serializer. `TodoTask.SubtaskProgress` is no longer written to JSON exports. It was a derived value that serialized as an empty object and was ignored on import.
  - `compose.yaml` (API + PostgreSQL 17 on an internal network, no database port, localhost-only API, optional mailpit), `.env.example`, `secrets/` (ignored), `scripts/new-dev-secrets.ps1`, and CI workflows.
  - Android and iOS are placeholders only.
- **Backend (Milestone B):** Go API with secret-file configuration, database retry, forward-only migrations, SMTP delivery, and email one-time-code sign-in with rotating refresh tokens and reuse detection. See [backend/README.md](../backend/README.md).
- **Verified:**
  - Windows, from `apps/windows`: 119 passed, 1 skipped (the opt-in 10 GB mailbox test). This includes 16 new contract tests.
  - Backend: `go vet` clean. Unit tests pass for configuration, sign-in rules (expiry, attempts, replay, supersession, cooldown and hourly, per-IP and global limits, rotation and reuse, logout), the HTTP error contract, log redaction, OpenAPI routing, and SMTP against an in-process server.
  - Integration tests against a real PostgreSQL 17 (embedded) cover migrations, downgrade refusal, the store contract including concurrent one-time consumption, and the full sign-in flow.
  - The built API binary was started against PostgreSQL: it applied the migration, served `/v1/health/live` and `/v1/health/ready`, returned `503 EMAIL_DELIVERY_FAILED` with no reachable SMTP server, and logged the address only redacted.
- **Not verified:** Docker is not installed on the build machine, so `docker compose config`, the API image build, the CI workflows, and email delivery through mailpit or a real relay have not been run. The OpenAPI file has not been linted (no Node.js here). CI does both.

## Unreleased: spacing, alignment, and workspace tabs (2026-11-21)

- **Spacing contract:** the toolbar and content are inset 10 px from the window; header → toolbar → content gaps are 8 px; Mail panes have exactly one 8 px gutter (the splitter hit target) and 16 px inner padding. Values are tokens in `Themes/Controls.xaml`; see [UI-DESIGN.md](UI-DESIGN.md#spacing-contract).
- **Alignment:** account selector, status chip, and folder tabs share one 36 px height and center line, with 16 px between groups. The list's title row and the reader's status row are both 48 px. From / Subject / Received share their columns with the message rows, also when the scroll bar shows. Selected tabs no longer turn bold, so they keep their size.
- **Workspace tabs:** Mail, Calendar, To-do, Notes, Contacts, and Chat are rounded, browser-style tabs in the header. Labels hide below 1200 px, search collapses below 860 px, and modules overflow below 720 and 560 px.
- **Also in this build:** delete an account (Settings → Accounts → Delete) and the Ocean background.
- **Verified:**
  - `dotnet test Alondra1.slnx`: 103 passed, 1 skipped. New tests cover the spacing and tab tokens, the shell rows, shared mail columns, tab styles without geometry changes, and one 8 px gutter in all 25 dock combinations.
  - Demo snapshots of Mail at 1400 px (light and dark), and in dark at 1199, 1120, 860, 760, 700, 640, 560, and 470 px; To-do (light) and Calendar (dark) at 1400 px.
  - Not checked: display scaling other than 100%, maximize/restore, and a screen reader. At about 470 px the Mail folder selector is still partly covered by New.

## 1.3.0: Alondra1 everywhere (2026-11-21)

- **Naming:** every identifier now uses the Alondra1 name.
  - Projects and namespaces are `Alondra1.*`, the solution is `Alondra1.slnx`, and the setup script is `installer/alondra1.iss`.
  - The data folder is `%LOCALAPPDATA%\alondra1`.
  - Other identifiers: the DPAPI entropy and OAuth token keys, the Microsoft To Do list the adapter creates (`alondra1`), the task export format (`alondra1-todo`), generated Message-IDs (`@alondra1.local`), and the backup extension (`.atodo`).
- **Breaking change:** this release starts with a fresh data folder. Settings, local mail, tasks, and saved sign-ins from earlier builds are not carried over. Add accounts again, and import tasks from a JSON export if needed. The setup `AppId` is unchanged, so the installer still upgrades the installed program in place.
- **Docs:** reorganized into [FEATURES](FEATURES.md), [ARCHITECTURE](ARCHITECTURE.md), [CODE-GUIDE](CODE-GUIDE.md), and [GUIDELINES](GUIDELINES.md). Logo files are in `docs/images`, and every screenshot was retaken.
- **Verified:**
  - `dotnet test Alondra1.slnx`: 97 passed, 1 skipped (the opt-in 10 GB mailbox test).
  - The demo was rendered in Mail, Calendar, To-do, Notes, and Contacts, in light and dark, and at 560 px wide. Header 56 px, toolbar 48 px. No crash was logged.
  - The setup was rebuilt.

## 1.2.0: Alondra1 brand, logo, palette, and setup wizard (2026-11-21)

- **Brand and logo:**
  - The product name is Alondra1: window title, header wordmark, Start menu and desktop shortcuts, Settings → Apps, `Alondra1.exe`, `Alondra1-setup.exe`, dialogs, and notifications.
  - The logo is a lavender-blue bird, cut out onto a transparent background (`Assets/alondra1.ico` 16–256 px, `logo-64.png`).
- **Palette:** rebuilt from the logo's lavender and peach. See [UI-DESIGN.md](UI-DESIGN.md). Contrast is checked by `UiDesignTests`.
- **Backgrounds:** pictures are more visible by default (Misty mountains, 85% opacity, 2 px blur), and brightness adapts to the theme.
- **Setup wizard:**
  - A Welcome and Finish side panel shows the bird on a lavender gradient, the wordmark, and "Mail · Calendar · To-do", and the bird appears in the inner page headers.
  - Text uses the app's colors.
  - The images are rendered for every Windows scale from 100% to 250% by `installer/branding/make-branding.ps1`.
- **Verified:**
  - The Welcome and Destination pages of the built setup were opened at 200% scale and captured. Setup was then cancelled.
  - The Finish page was not opened, because that requires an install.
  - The wizard stays light in Windows dark mode, matching its artwork.

## 1.1.x: Local To-do and UI modernization (2026-11-21)

**Local To-do**

- **Automated tests:** 82 passed, 1 skipped, and 62 of them cover local tasks:
  - task and list operations with undo, and a failed write leaving memory unchanged;
  - steps, groups, bulk actions, and drag-and-drop ordering;
  - recurrence: month-end and leap-day clamping, weekday sets, intervals, end dates, and no duplication on completion;
  - smart views, search, filters, sorting, statistics, and natural-language dates;
  - reminders with snooze and dismiss, including the Eastern Time DST gap and overlap;
  - calendar ranges and rescheduling;
  - SQLite persistence across restarts, migration with a pre-migration copy, and refusal of newer files;
  - backup and restore, attachment path safety, and JSON import validation with id remapping;
  - CSV and iCalendar output;
  - task code not using network APIs;
  - 10,000 tasks.
- **Demo window, checked through UI Automation:**
  - every smart view, quick add ("Call Sam tomorrow at 3pm"), details, steps, undo, and reordering;
  - keyboard shortcuts, cross-list search, and the month, week, and day calendars;
  - the Summary view, Account tasks, and the narrow-window sidebar.
- **Not checked:** a Windows notification actually appearing, printing to a physical printer, and screen readers.

**UI modernization**

- **New layout and styling:**
  - two rows above the content: a header with modules and search, and a toolbar per module;
  - design tokens for radius, spacing, and control heights;
  - 62 custom icons;
  - built-in and custom backgrounds;
  - themed dialogs and title bars;
  - a responsive header for narrow windows.
- **Tests added:** the two-row shell, height tokens, matching palettes, WCAG contrast, icon validity, background defaults and file checks, and no network addresses in app markup.
- **Fixed while checking:** the reading pane clipped instead of wrapping, list text was black in the dark theme, and the toolbar did not fill its row.
- **Not checked:** menus and drop-downs on screen (popups are not part of snapshots), and screen readers.

## 1.0: Mail client

- `dotnet test` passed. The tests cover:
  - all 25 folder and reader dock combinations;
  - configuration rejection and recovery, and import rules;
  - plaintext and OAuth policy;
  - SMTP uncertainty, POP UIDL, and IMAP UIDVALIDITY;
  - HTML sanitizing and mode changes;
  - scoped full-text search, and Online mode not writing messages;
  - an uncertain outbox item surviving a database reopen;
  - 10,000 messages indexed and searched within limits.
- The demo window was confirmed through UI Automation: folders on top, compact list, unread state, the reading pane, and the toolbar commands.

## Not verified yet

These checks need environments that were not available. The client implements the behavior, and the automated tests cover the decision logic.

| Check | Why it is still open |
| --- | --- |
| Install, upgrade, and uninstall on Windows 10 21H2 LTSC and 22H2 | Those OS versions were not available. |
| Windows 11 25H2 specifically | The test PC runs build 26300 (26H2). |
| Microsoft 365 and Gmail sign-in on consented test accounts | No app registration or test mailbox was configured. Setup is in [OAUTH-SETUP.md](OAUTH-SETUP.md). |
| Plaintext confirmation, required STARTTLS failure, and certificate rejection against a controlled server | Needs a lab server. The client uses MailKit `SecureSocketOptions.StartTls` (not the optional upgrade) and the default OS trust check. |
| A real SMTP session that disconnects after an unknown acceptance | The state machine and outbox restart were tested with a fake gateway. |
| Provider read and write for Calendar, To Do, Tasks, People, and app-folder notes, including denied consent | No live tenant was available. |
| Scroll timing and memory with 10,000 messages in the window | `--perf` seeds the index and records `perf.txt`. An interactive scroll pass was not completed. |
| Upgrading an installed package with a queued outbox | The database keeps queued operations across restarts. An installed upgrade was not run on a second OS. |

## Packaging

```powershell
./installer/publish.ps1
```

This publishes a self-contained x64 build and compiles `artifacts/Alondra1-setup.exe` with Inno Setup.

The setup installs per user to `%LOCALAPPDATA%\Programs\Alondra1`, adds the Alondra1 Start menu shortcut, and registers uninstall in Settings → Apps. It never touches `%LOCALAPPDATA%\alondra1`, so updates and uninstalls keep settings, local mail, tasks, secrets, and attachment files.

Microsoft's Windows support for .NET is documented at https://learn.microsoft.com/dotnet/core/install/windows.
