# 0001: Optional first-party cloud, off by default

- **Status:** accepted (2026-11-21)
- **Supersedes:** the "No Alondra1 service" rule in [GUIDELINES.md](../GUIDELINES.md), for users who turn the cloud on
- **Spec:** [BACKEND_SPEC.md](../BACKEND_SPEC.md) §1, §3, §17

## Context

Alondra1 1.3.0 promises no Alondra1 servers, no telemetry, and network access only to the mail and organizer accounts the user adds. The To-do workspace works with no account and no network. Users now want their tasks and some settings on more than one device (Windows, later Android and iOS), and families or teams want shared spaces.

## Decision

1. Add an **optional** Alondra1 cloud: a Go API with PostgreSQL that stores a synchronized copy of tasks and selected portable settings, per space (Personal, Family, or Organization).
2. **Off by default.** Without an explicit "Connect Alondra1 Cloud", the app makes no request to any Alondra1 server. A fresh install, demo mode, and the To-do workspace keep working with no account and no network.
3. **Local stays the source of immediate truth.** Every read and write happens on the device; sync runs opportunistically from an outbox. A cloud outage never blocks local work.
4. **Sign-in is email one-time codes only.** No Alondra1 passwords, magic links, or social login. Provider sign-ins (Microsoft, Google, manual IMAP/SMTP passwords) stay separate and are never sent to Alondra1.
5. **Still no telemetry, analytics, ads, crash upload, or update checks**, with or without the cloud. The backend logs operational metrics about itself, not about user content.
6. Users can disconnect, delete a space, and delete their account; each has a documented retention and tombstone policy.

## Consequences

- [GUIDELINES.md](../GUIDELINES.md), [ARCHITECTURE.md](../ARCHITECTURE.md), and [FEATURES.md](../FEATURES.md) change when the cloud features ship, not before. FEATURES lists only what is implemented.
- The project now operates a service: secrets handling, backups, TLS, email deliverability, retention, and privacy obligations apply (spec §12 to §13). Until Milestone F these are development-only.
- Tests must prove that local-only mode and demo mode make no cloud requests (spec §14, items 1, 13, 14).
