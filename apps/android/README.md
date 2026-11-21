# Alondra1 for Android (not started)

Planned location of the Android Studio project ([BACKEND_SPEC.md](../../docs/BACKEND_SPEC.md) §11). Nothing is implemented yet.

Scope when it starts: local tasks and portable settings in Room/SQLite with an outbox, email-code sign-in, Personal/Family/Organization space selection, and sync against the same API contract ([packages/api-contract](../../packages/api-contract)) and fixtures ([packages/shared-models](../../packages/shared-models)) as Windows. Credentials go to the Android Keystore; background sync uses WorkManager and is best effort. The mail, calendar, and organizer integrations of the Windows app are not part of this scope.
