# Development guidelines

<img src="images/alondra1-logo-64.png" alt="" width="48" align="right">

These are the rules for changing Alondra1. They protect the users' data and privacy and keep the app consistent. If a change needs to break one of them, say so in the pull request and explain why.

## Product principles

1. **Local-first.** The app must open, show local mail and tasks, and accept edits with no network connection. Online features fail gracefully: they show a status such as "Offline" and never block the window.
2. **No Alondra1 service.** Do not add servers, telemetry, analytics, crash upload, update checks, ads, or remote configuration. Network calls go only to the accounts the user added, using the endpoints shown in their settings.
3. **The user's data stays theirs.** Everything Alondra1 writes is in `%LOCALAPPDATA%\alondra1`, in documented formats (SQLite, JSON, CSV, iCalendar). Exports never contain secrets.
4. **Never lose data silently.** Before replacing data, validate the input, make a safety copy where it matters, and ask for confirmation. Prefer undo over a confirmation dialog for everyday actions.
5. **Be honest in the UI.** Say what happened in plain words, such as "Nothing was changed" or "Sent, but the server did not confirm". Sample data is always labeled.
6. **Respect the user's machine.** No administrator rights, no background services, no startup entries, no changes outside the user's own folders.

## Naming and branding

- The product name is **Alondra1**: capital A in text and UI, `alondra1` in file names, folders, and identifiers (`%LOCALAPPDATA%\alondra1`, `alondra1-todo`, `@alondra1.local`). Namespaces and projects are `Alondra1.*`.
- Use the logo files in `docs/images` and `src/Alondra1.App/Assets`. Do not redraw, recolor, or stretch the bird.
- Brand colors come from the logo: lavender `#6C6DB0` for actions, wing lavender for accents, peach `#F1C2AA` for warm details. Use them through theme tokens, never as literal colors (see below).

## Code style

- C# with nullable reference types enabled, file-scoped namespaces, and `sealed` classes by default. Expression-bodied members are fine where the body is short.
- Names describe the user-facing concept: `TaskBoard`, `ReminderNotifier`, `ConnectionPolicy`. Avoid abbreviations except well-known ones (IMAP, SMTP, UID, DPAPI).
- Comments say **why**, or state an invariant, not what the next line does. Public types that hold a rule get a `<summary>`.
- Keep files focused. A policy or algorithm that can be tested on its own goes into `Alondra1.Core`.
- Do not catch `Exception` broadly. Catch the specific types (`IOException`, `SqliteException`, `UnauthorizedAccessException`) and let real bugs reach the crash handler.

## Architecture rules

- `Alondra1.Core` has no file, database, network, or UI code. It defines ports, and the adapters implement them.
- Adapters (`Mail`, `Persistence`, `Providers`) depend only on `Core`, never on each other or on `App`.
- Wire new services in `AppServices.CreateAsync`, the only composition root.
- Every change to local tasks goes through `TaskBoard` as a change set with an inverse. Do not write to `todo.db` from anywhere else.
- Mail decisions (retry, dedupe, conflict, mode change) live in `MailPolicies`, and `SyncCoordinator` only orders the steps.

## Async and threading

- Library code uses `ConfigureAwait(false)`. App code awaits on the UI thread.
- Pass a `CancellationToken` through every I/O method.
- Never block on a task (`.Result`, `.Wait()`) in the UI.
- Long work (sync, import, export, backup) runs asynchronously and reports progress or status, so the window stays responsive.

## Data and storage

- **SQLite schema changes:** append a migration step and never edit one that has shipped. Make a copy of the file before migrating. Refuse files from a newer version without changing them.
- **One logical change, one transaction.** Update memory only after the commit succeeds.
- **Store paths relative** to their folder, and check that they stay inside it before opening.
- **Validate imports completely** before writing anything. Imported items get new ids. Never overwrite existing items.
- **`config.json`** is written atomically (temporary file, then replace) with a `.bak`. Add new settings with defaults so older files still load.

## Security

- Secrets go only through `ISecretStore` (DPAPI). Never log them, put them in `config.json`, or show them in an error.
- **OAuth:** public clients only. Never add a client secret to the code or to an export.
- **Connections:** TLS by default. Plaintext only after explicit confirmation, and never together with OAuth tokens.
- Treat everything from a mail server or an imported file as untrusted. HTML is converted to text, file names are sanitized, and sizes are limited.
- The app markup must not load anything from the network (checked by `App_markup_loads_nothing_from_the_network`).

## UI and design

- **Use tokens, not literals.** Colors: `{DynamicResource TextBrush}`, `BrandBrush`, and the rest. Sizes and radii: `ControlMd`, `RadiusMd`, and so on. The list is in [UI-DESIGN.md](UI-DESIGN.md). New colors go into **both** `Light.xaml` and `Dark.xaml`.
- **Use the shared styles** in `Themes/Controls.xaml` (`PrimaryButton`, `IconButton`, `QuietButton`, `Card`, `Pane`, `FieldLabel`, and others) instead of local styling.
- **Icons** come from `Themes/Icons.xaml` through `Alondra1Icon`: 24 × 24 grid, 1.75 px stroke.
- **Contrast:** at least WCAG AA (4.5:1 for text, 3:1 for large text and icons) in both themes. `UiDesignTests` checks the main tokens.
- **Accessibility:** every icon-only button has `AutomationProperties.Name` and a tooltip. Everything works with the keyboard, focus is visible, and nothing relies on color alone, so overdue items also show text.
- **Layout:** the header is 56 px and the toolbar 48 px. Never add a third row above the content. Narrow windows hide labels and move items into More menus (see the breakpoints in UI-DESIGN.md).
- **Dialogs** use `Dialogs.Create` and `Dialogs.Alert`, never a raw `MessageBox` or a hand-made window.
- **Copy:** short, specific, sentence case. Name the action on buttons ("Save account", not "OK").

## Testing

- Every rule or bug fix gets a test. Pure logic goes into the Core tests, and storage tests use temporary folders.
- Tests never touch `%LOCALAPPDATA%\alondra1` or the network. Use `Directory.CreateTempSubdirectory("alondra1-…")` and a fixed `IClock`.
- For a UI change, also check it visually: render light and dark snapshots (`--demo --snapshot=… --theme=dark --exit`), including a narrow width.
- `dotnet test Alondra1.slnx` must pass before a change is merged.

## Documentation

- Update the docs in the same change as the code:
  - [FEATURES.md](FEATURES.md) for user-visible behavior;
  - [ARCHITECTURE.md](ARCHITECTURE.md) and [CODE-GUIDE.md](CODE-GUIDE.md) for structure;
  - [UI-DESIGN.md](UI-DESIGN.md) for tokens and visuals;
  - [TODO.md](TODO.md) for task behavior.
- Regenerate screenshots in `docs/screenshots` when the look changes, using `--snapshot`.
- Record what was verified and what was not in [DELIVERY-NOTES.md](DELIVERY-NOTES.md). Do not claim a check you did not run.

## Releases

1. Bump the version in `src/Alondra1.App/Alondra1.App.csproj`, `src/Alondra1.App/app.manifest`, and `installer/alondra1.iss` (`MyAppVersion` and the `VersionInfo*` lines).
2. Run `dotnet test Alondra1.slnx`.
3. Run `./installer/publish.ps1`. It cleans `artifacts\win-x64`, publishes a self-contained build, and compiles `artifacts\Alondra1-setup.exe`.
4. Install over the previous version and check that settings, mail, and tasks are still there, then uninstall and check that the data folder remains.
5. Add a section to DELIVERY-NOTES.md.

Keep the setup `AppId` unchanged. It is what lets a new setup upgrade an existing installation.

## Git

- Branch from `main`. Keep commits focused, with an imperative subject line ("Add weekly recurrence end date").
- Never commit `artifacts/`, `bin/`, `obj/`, personal config files, or anything with secrets.
