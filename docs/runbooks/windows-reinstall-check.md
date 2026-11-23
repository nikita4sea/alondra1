# Windows client: reinstall check

Every client milestone ends with this check on the developer PC. It proves that a fresh installer installs, imports the test setup, syncs, and sends mail. Tests that run in the repository cannot see these steps.

## Run it

```powershell
cd apps/windows
./scripts/reinstall-local.ps1              # clean install (default)
./scripts/reinstall-local.ps1 -KeepData    # upgrade check: keeps %LOCALAPPDATA%\alondra1 and skips the import
```

Requirements:
- the .NET 10 SDK (the script uses `%LOCALAPPDATA%\dotnet` when it is there);
- Inno Setup 6;
- the git-ignored test-account file `.test` at the repository root. It has `USER1`, `PASSWORD1`, `USER2`, `PASSWORD2`, `IMAP`, `POP3`, and `SMTP` lines.

## What it does

1. Closes Alondra1.
2. Copies `%LOCALAPPDATA%\alondra1` to `alondra1-backup-<time>` and keeps the last three copies.
3. Uninstalls through the uninstall entry, wherever the app was installed, and checks that the files, the Start menu shortcut, and the Settings → Apps entry are gone. Then it deletes the data folder, unless `-KeepData` is given.
4. Builds `artifacts/Alondra1-setup.exe` with `installer/publish.ps1`.
5. Installs silently. It checks the program folder, the shortcut, the Apps entry, and that the version matches the build.
6. Regenerates `.import/alondra1-test-config.json` from `.test`. The file has the accounts only, with every other setting at the app's defaults, and no passwords. Then it runs:
   `Alondra1.exe --import-config=<file> --import-passwords=<.test> --exit`
7. Runs `Alondra1.exe --sync --open=Mail --snapshot=… --exit`. Both accounts must sync, and the IMAP folder map must be learned.
8. Runs `Alondra1.exe --send-check --exit`, which sends from the first test account to the second and syncs until the message arrives. It then restarts the app to check that the chosen Mail account is kept, and takes snapshots of Mail, Calendar, To-do, Notes, and Contacts.

The summary and the snapshots go to `apps/windows/artifacts/reinstall/<time>/`. The script stops at the first failure. A milestone stays open until the check passes.

## Switches used

| Switch | Effect |
| --- | --- |
| `--import-config=<file>` | Imports a configuration in Replace mode, through the same validation and preview as Settings → Import configuration. It refuses files that contain secret fields. The result goes to `logs\setup.log`. |
| `--import-passwords=<file>` | Saves the `USERn`/`PASSWORDn` passwords with DPAPI for the accounts whose address or user name matches. Passwords are never logged or written to `config.json`. |
| `--sync`, `--send-check` | One sync of every account; send and receive between the first two manual accounts. The results go to `logs\check.log`. |
| `--account=<name>` | Selects the Mail account (it is remembered, like a choice in View settings). |
| `--exit` | Closes when done. The exit code is not 0 if a step failed: 2 for an invalid configuration, 3 for passwords, 4 for sync, 5 for send-check. |

## Related checks

| Script | What it checks |
| --- | --- |
| `scripts/check-reader.ps1 [-Exe <path>] [-Only M05]` | Renders the mail fixtures M01–M12 in the real WebView2 (`--open-eml`). It fails when a script ran, when a remote request was made while images were blocked, or when the format, blocked images, or attachments differ from what the fixture expects. The reinstall check runs it against the installed copy. |
| `scripts/snapshot-matrix.ps1 [-Quick]` | Demo snapshots at 1280 × 800, 1600 × 900, 2048 × 1080, and 800 × 900, in light and dark, at 100 % and 125 % scale (150 % and 200 % spot checks). It covers these states: inbox, plain text, rich newsletter, blocked images, no search results, and Settings. Every snapshot writes a geometry report (heights, insets, toolbar center line, overlaps, gutter, From column), and any `FAIL` line fails the run. Hover states are not captured, because snapshots cannot hold a mouse position. |

Diagnostic switches used by these scripts: `--open-eml=<file>`, `--load-images`, `--scale=<factor>`, `--search=<text>`, `--trace` (steps written to `logs\trace.log`).
