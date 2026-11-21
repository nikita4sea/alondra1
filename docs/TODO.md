# Local To-do

<img src="images/alondra-logo-64.png" alt="" width="48" align="right">

The To-do workspace (Ctrl+3) manages personal tasks stored on this PC. It works without a network connection, an account, or any service. Tasks from a Microsoft 365 or Google account are still available under **Account tasks** in the To-do sidebar; they are separate from local tasks and are loaded only when that view is opened.

## Architecture

| Layer | Files | Responsibility |
| --- | --- | --- |
| Domain | `src/Alondra.Core/Tasks/` | Entities, recurrence, smart views, search and sort, natural-language dates, reminders, import/export, and `TaskBoard` (all task rules). No UI, storage, or network code. |
| Persistence | `src/Alondra.Persistence/SqliteTaskStore.cs`, `TaskFileStore.cs` | SQLite storage with migrations, backups, and copied attachment files. |
| Presentation | `src/Alondra.App/Tasks/` | `TodoViewModel` (rows, preferences, commands), `TodoView` (layout, drag and drop, keyboard), dialogs, printing, reminder notifications. |

`TaskBoard` is the single entry point for changes. Each change builds a `TodoChangeSet` and its inverse, writes the change set to the store in **one transaction**, and only then updates memory. If the write fails, memory is untouched and the user sees "nothing was changed". The inverse is kept for undo (50 steps). Awaits resume on the caller's context, so in the app every change and every `Changed` event happens on the UI thread.

Smart views are filters over the one stored set of tasks (`SmartViews.Includes`); they never copy a task.

### Entities

| Entity | Notes |
| --- | --- |
| `TodoTask` | id, list id, title, notes, completed, important, position, created / modified / completed timestamps, start date, due date, optional time slot, My Day date, recurrence, reminders, steps, attachments |
| `Subtask` | "steps" in the UI; own completion state and order |
| `TaskList` | name (emoji allowed), group, color, background tint, position, default flag |
| `ListGroup` | name, position, collapsed |
| `RecurrenceRule` | frequency (daily, weekly, monthly, yearly), interval, weekdays, end date, anchor date |
| `TaskReminder` | local wall-clock time, delivered-for marker, dismissed, snoozed-until |
| `TaskAttachment` | original name, size, path relative to the task-file folder |
| `CompletionRecord` | one row per completion, kept when a task is reopened, deleted, or repeats |
| `TaskPreferences` | selected view, sort per view, filters, counts, calendar mode, notifications, snooze length |

### Storage

Local tasks live in `%LOCALAPPDATA%\alondra\todo.db`, next to `personal.db`, using the same library (Microsoft.Data.Sqlite) and the same `PRAGMA user_version` versioning. They use a separate file because `personal.db` belongs to mail accounts: removing an account or leaving Personal mode deletes that account's rows, and to-do data must not depend on that. A separate file can also be backed up and restored without touching mail.

Rows store each entity as JSON, plus indexed key columns (`list_id`, `due_date`, `completed`). New fields can be added to the JSON without a migration.

- **Migrations** are an ordered list in `SqliteTaskStore.Migrations`. Each runs in its own transaction and sets `user_version`.
- **Before migrating** an existing file, a consistent copy is written to `todo.db.pre-v<N>.bak`.
- **A newer file** (higher `user_version`) is refused and left unchanged. Only the To-do workspace is disabled; mail keeps working.
- A damaged JSON row is skipped instead of blocking every other task from loading.

Attached files are **copied** into `%LOCALAPPDATA%\alondra\task-files\<task id>\<random name>`. Stored paths are relative and checked to stay inside that folder before a file is opened. Files that no task refers to are removed once at startup, not when a task is deleted, so undo can restore an attachment.

## Behavior decisions

- **Deleting a list** deletes its tasks after a confirmation that names the task count. Undo restores both. The default **Tasks** list cannot be deleted.
- **Deleting a group** keeps its lists; they become ungrouped.
- **Completing a repeating task** keeps one task record: its due date moves to the next occurrence, its start date and reminders move by the same number of days, steps reset, and a `CompletionRecord` is added. Completing it twice moves it two occurrences, never duplicates it. When the series has ended (end date passed), the task completes normally.
- **Recurrence is calculated on calendar dates**, so it does not drift with time zones or DST. Monthly and yearly repeats count from the anchor date, so Jan 31 → Feb 28 → Mar 31 (not Mar 28).
- **My Day** shows tasks whose My Day date is today. It starts empty each day; tasks are not moved or copied.
- **Quick add** creates the task in the selected list, or in Tasks when a smart view is selected. In My Day it is added to My Day, in Important it is starred, and in the Calendar it is due on the selected day. A typed time also adds a reminder at that time.
- **Import** never overwrites: every imported group, list, task, step, reminder, and history entry gets a new id. A list whose name already exists is imported as "Name (imported)". Unknown list references go to a new "Imported tasks" list, and unsafe attachment paths are dropped, with a warning in each case.
- **Restore** replaces all local tasks in one transaction, after a confirmation that shows both counts. A safety backup of the current data is written first (`backups\before-restore-*.atodo`).

## Reminders and notifications

Reminders are stored as local wall-clock times and converted with the PC's current time zone each time they are checked:

- A reminder follows the wall clock across a DST change (09:00 stays 09:00).
- A time in the spring-forward gap (02:30 on the change day) fires at the first valid minute (03:00).
- A time that happens twice in the fall-back hour fires once, at its first occurrence.

`ReminderNotifier` checks every 15 seconds while Alondra runs. A due reminder is marked delivered **before** it is shown, so it is never shown twice. It appears in a reminder bar under the workspace tabs (visible in every workspace) with **Open**, **Snooze**, and **Dismiss**, and, unless turned off in the To-do **⋯** menu, as a Windows notification from the notification-area icon.

**Limitations:** Alondra has no background service or scheduled task, so reminders are not delivered while it is closed. A reminder that came due while it was closed is shown the next time Alondra starts. Snooze and Dismiss are in the reminder bar; the Windows notification has no buttons (clicking it opens the task).

## Import, export, backup

All of these are in the To-do **⋯** menu and read or write only local files.

| Action | Format |
| --- | --- |
| Export JSON | `{"format":"alondra-todo","version":1,...}` with groups, lists, tasks, and history. Attachment metadata is included; the files themselves are not. |
| Import JSON | The same format. Validated (format name, version, duplicate ids, missing ids, size limits) before anything is written. A confirmation shows what will be added. |
| Export CSV | One row per task, UTF-8 with BOM for Excel. Cells starting with `= + - @` are prefixed with `'` so spreadsheets do not run them as formulas. |
| Export iCalendar | `VTODO` items with `DUE`, `DTSTART`, `PRIORITY`, `STATUS`, `RRULE`, and `VALARM` (RFC 5545, folded lines). |
| Back up now | A consistent SQLite copy (`.atodo`) made with SQLite's online backup. |
| Automatic backup | Once a day at startup, when there are tasks; the last 7 are kept in `%LOCALAPPDATA%\alondra\backups`. |
| Restore | Opens a copy of the backup, checks integrity and tables, migrates the copy if it is older, then replaces current data. |

## Keyboard

| Keys | Action |
| --- | --- |
| Ctrl+N | Add a task |
| Ctrl+Shift+N | New list |
| Ctrl+F or Ctrl+E | Search all tasks |
| Space or Ctrl+Enter | Complete or reopen selected tasks |
| Ctrl+I / Ctrl+T / Ctrl+D | Important / My Day / Duplicate |
| Del, then Ctrl+Z | Delete, undo |
| Enter, F2 | Open details, rename |
| Alt+Up / Alt+Down | Move a task when sorted by "My order" |
| Shift/Ctrl+click, Ctrl+A | Select several tasks (bulk bar appears) |
| Alt+Left / Alt+Right | Previous / next in the calendar; arrow keys move between days |
| Ctrl+P | Print the current view |
| Esc | Hide details |
| Menu key or Shift+F10 | Context menu for the selected tasks |

While To-do is open, mail shortcuts (Ctrl+R, Ctrl+Shift+F, Del on a message) do not apply, and Ctrl+N adds a task instead of a message. Ctrl+1 … Ctrl+6 still switch workspaces.

## Feature checklist

Statuses: **Already supported**, **Implemented**, **Partially implemented**, **Not implemented**, **Not applicable**.

### Personal task management

| Feature | Status | Notes |
| --- | --- | --- |
| Create, edit, delete tasks | Implemented | Tasks from connected accounts are listed separately under Account tasks (add and check off). |
| Complete and reopen | Implemented | |
| Multiple lists | Implemented | |
| Rename, delete, duplicate lists | Implemented | Duplicating copies the tasks with new ids. |
| List groups | Implemented | Collapsible; lists can be dragged between groups. |
| Descriptions and notes | Implemented | |
| Subtasks with own completion | Implemented | "Steps"; progress bar; a step can be turned into a task. |
| Importance (star) | Implemented | |
| Due dates | Implemented | Typed dates accept culture formats, ISO, and words (tomorrow, fri, next week). |
| Reminders | Implemented | Delivered while Alondra runs; see Limitations. |
| Recurrence: daily, weekly, monthly, yearly, custom | Implemented | Custom: every N days/weeks/months/years, chosen weekdays, end date. |
| Attach local files | Implemented | Copied into the data folder; drag files onto the details pane. |
| Move between lists | Implemented | Details pane, context menu, bulk bar, or drag onto a sidebar list. |
| Manual reorder of tasks | Implemented | Drag and drop or Alt+Up/Down when sorted by "My order". |
| Manual reorder of subtasks | Partially implemented | Up/down buttons; no drag and drop for steps. |
| Sort by due date, importance, created, alphabetical | Implemented | Plus "My order"; each view remembers its sort and direction. |
| Search across all lists | Implemented | Title, notes, steps, list name; accent-insensitive; updates as you type. |
| Natural-language dates (offline) | Implemented | English keywords only. |
| Keyboard shortcuts | Implemented | See table above. |
| Drag and drop | Implemented | Reorder, move to list, add to My Day / Important / Completed, reschedule on the calendar, reorder lists. |
| Confirmation or undo for destructive actions | Implemented | Undo for task changes and deletes; confirmation plus undo for list deletion; confirmation plus safety backup for restore. |
| Smart views: My Day, Important, Planned, Upcoming, Overdue, All, Completed, Unscheduled | Implemented | Filters over stored tasks; no duplicate records. |

### Organization and productivity

| Feature | Status | Notes |
| --- | --- | --- |
| Collapsible list groups | Implemented | |
| Custom list colors | Implemented | Eight-color palette that reads in both themes. |
| Custom list backgrounds | Partially implemented | A light tint from the list color. Picture backgrounds apply to the whole window (Settings → Background), not per list. |
| Emojis in list names | Implemented | WPF draws emoji as monochrome glyphs. |
| Collapsible task details | Implemented | Hide with Esc or the button; the setting is remembered. |
| Quick task creation | Implemented | Shows a live preview of the parsed date, time, and repeat. |
| Task duplication | Implemented | |
| Bulk selection, completion, deletion, move | Implemented | Also bulk Important and My Day. |
| Task filtering | Implemented | Open / all / completed, important only. |
| Persistent view, sort, and filter preferences | Implemented | Stored in `todo.db`. |
| Completion history | Implemented | Summary view; one entry per completion, including each occurrence of a repeating task. |
| Print-friendly lists | Implemented | Black-on-white checklist with steps and notes. Not checked on a physical printer. |

### Calendar and reminders

| Feature | Status | Notes |
| --- | --- | --- |
| Local task calendar: day, week, month | Implemented | Inside To-do. The Calendar workspace still shows account events only. |
| Tasks by date, overdue display | Implemented | Overdue chips are red; a side panel lists overdue and unscheduled tasks. |
| Drag-and-drop rescheduling | Implemented | Onto a day, or onto an hour in Day view to set the time. Reminders and start date move with the task. |
| Start dates | Implemented | Shown on the calendar as an outlined chip. |
| Optional time slots | Implemented | |
| Multiple reminders | Implemented | Duplicates at the same time are merged. |
| System notifications | Implemented | Notification-area balloon, which Windows 10/11 shows as a notification. Not triggered during the check on this PC. |
| Dismiss and snooze | Implemented | In the reminder bar; snooze length 10 minutes by default. |
| Recurring reminders | Implemented | Reminders move with each occurrence, keeping their wall-clock time. |
| Time zone and DST | Implemented | Covered by tests in Eastern Time across both 2026–2027 changes. |
| No duplicate reminders | Implemented | Marked delivered before display. |
| Reminders while Alondra is closed | Not implemented | No background service; see Limitations. |

### Local data

| Feature | Status | Notes |
| --- | --- | --- |
| Persistent tasks, lists, groups, history, preferences | Implemented | |
| Offline operation | Implemented | No task code refers to network APIs (checked by a test). |
| Transaction-safe updates | Implemented | One SQLite transaction per change; memory changes only after commit. |
| Backup creation and manual restore | Implemented | Manual plus a daily automatic backup (7 kept). |
| JSON export and import with validation | Implemented | |
| CSV export | Implemented | |
| No duplicate identifiers on import; relationships preserved | Implemented | |
| Versioned schema and migrations; upgrade protection | Implemented | Pre-migration copy; newer files refused unchanged. |
| iCalendar export | Implemented | Includes recurrence (`RRULE`) and reminders (`VALARM`). |
| iCalendar import | Not implemented | |
| Encrypted backups | Not implemented | DPAPI would tie a backup to this Windows account, so it could not be restored on another PC. Backups are plain SQLite files in the user's profile. |

### Excluded by the request

Cloud sync, remote databases, accounts, sharing, collaboration, push services, telemetry, and external integrations: **Not applicable**. None were added.

## Limitations

- Reminders are delivered only while Alondra is running (see above).
- Natural-language parsing understands English words; numeric dates follow the Windows date format (day-first or month-first).
- Steps are reordered with buttons, not by dragging.
- The task calendar is in To-do; tasks do not appear in the Calendar workspace next to account events.
- Very large views (tens of thousands of tasks) are rebuilt after each change. 10,000 tasks were checked: every smart view plus a search runs well under 2 seconds in total.
