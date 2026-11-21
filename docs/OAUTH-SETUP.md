# OAuth setup for Alondra

<img src="images/alondra-logo-64.png" alt="" width="48" align="right">

Alondra does not ship a client secret. Enter the public client id in Settings before signing in. Tokens are stored with Windows DPAPI for the current user and are kept separate for Exchange protocol access and Microsoft Graph.

## Microsoft 365 / Exchange Online

1. In Microsoft Entra, register an application. Supported account types: the tenants you need to sign in.
2. Add a public client / mobile and desktop platform. Redirect URI: `http://localhost`.
3. Do not create a client secret. Copy the Application (client) id into Settings → Microsoft client id. Leave the tenant as `common` unless you must pin a directory.
4. Under API permissions, add delegated Microsoft Graph permissions only for the features you turn on in Alondra:
   - Mail sign-in uses Exchange delegated scopes, not Graph: `IMAP.AccessAsUser.All` or `POP.AccessAsUser.All`, plus `SMTP.Send`, and `offline_access`.
   - Calendar: `Calendars.ReadWrite`
   - To-do: `Tasks.ReadWrite`
   - Contacts: `Contacts.ReadWrite`
   - Notes: `Files.ReadWrite.AppFolder` (the OneDrive app folder, not OneNote)
5. Grant admin consent when the tenant requires it. A consent or SMTP AUTH failure is shown on the account instead of a silent fallback.
6. Incoming mail uses `outlook.office365.com` (IMAP 993 or POP 995, implicit TLS). Sending uses `smtp.office365.com:587` with required STARTTLS. The mailbox must allow IMAP or POP, and SMTP AUTH, or the connection test explains which one is disabled.

## Gmail / Google Workspace

1. In Google Cloud, create an OAuth client of type Desktop. Copy the client id into Settings. Do not put the client secret in Alondra or in an exported configuration.
2. Enable the Gmail API, and the Calendar, Tasks, People, and Drive APIs for the features you turn on.
3. Add yourself as a test user while the app is unverified. The Gmail scope `https://mail.google.com/` is restricted; distribution needs Google's verification review.
4. IMAP uses `imap.gmail.com:993`. POP uses `pop.gmail.com:995` and must be enabled in Gmail settings. SMTP uses `smtp.gmail.com:587` with required STARTTLS.
5. Calendar, Tasks, and People contacts use the matching Google APIs. Notes use the Drive app data folder. They are not Google Keep notes.

## Manual accounts

IMAP or POP3 and SMTP are configured separately. Implicit TLS, required STARTTLS, and an explicit unencrypted option are available. Unencrypted connections must be confirmed again after import and after an endpoint is changed to no encryption. OAuth tokens are never sent to an unencrypted server.
