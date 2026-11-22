# Runbook: backups and restores

The `backup` profile runs `deploy/backup/backup.sh` in its own container. Once a day (`ALONDRA1_BACKUP_INTERVAL_SECONDS`) it:

1. dumps the database with `pg_dump` (custom format) into a private temporary folder;
2. checks the archive (`pg_restore --list`), **restores it into a scratch database** (`alondra1_restore_check`), counts migrations and users, and drops the scratch database;
3. encrypts the dump with [age](https://age-encryption.org) to the **public** key in `secrets/backup_recipients`, writes `alondra1-<UTC time>.dump.age` and its `.sha256` into the `backups` volume, and deletes the plaintext;
4. deletes backups older than `ALONDRA1_BACKUP_RETENTION_DAYS` (35, the limit in [ADR 0003](../decisions/0003-retention.md));
5. updates `status.json`. The API reads this file read-only and publishes `alondra1_backup_last_success_timestamp_seconds` and `alondra1_backup_last_restore_check_timestamp_seconds`.

A failed run leaves `status.json` unchanged, and the alerts fire after 26 hours.

## Keys

Generate the key pair on a machine other than the server, and keep the identity in a password manager or offline:

```sh
age-keygen -o alondra1-backup-identity.txt     # private: never on the server
grep -o 'age1[0-9a-z]*' alondra1-backup-identity.txt > secrets/backup_recipients   # public: on the server
```

Add a second recipient line (a second key held by another person) to survive the loss of one key. The backup refuses to run without a recipient.

## Off-host copies (required for production)

The `backups` volume is on the same host as the database, so it is **not** enough on its own. Copy the encrypted files off the host, for example with a daily cron job on the host:

```sh
docker run --rm -v alondra1_backups:/backups:ro -v "$HOME/.config/rclone:/config/rclone:ro" rclone/rclone \
  copy /backups remote:alondra1-backups --include 'alondra1-*.dump.age*'
```

The files are encrypted, so the destination does not need to be trusted with plaintext. Keep the destination's retention at 35 days or less.

## Restore

Run on any machine that has the identity file and network access to the target PostgreSQL (in Compose, on the `internal` network):

```sh
# 1. A fresh database to restore into (never the live one first):
docker compose exec db createdb -U alondra1 alondra1_restored
# 2. Decrypt, verify the checksum, restore:
docker compose run --rm --entrypoint restore.sh -v "$PWD/alondra1-backup-identity.txt:/identity.txt:ro" backup \
  /backups/alondra1-20261122T030000Z.dump.age /identity.txt alondra1_restored
```

To **replace the live data** (disaster recovery):

1. `docker compose stop api caddy`.
2. Restore into `alondra1` with `FORCE=1 I_UNDERSTAND_THIS_REPLACES_LIVE_DATA=1`. The script refuses otherwise.
3. `docker compose up -d`.

Clients keep their outboxes. Sync cursors from after the backup point are newer than the restored change log, so clients see no new entries until writes resume. Writes made after the backup and not in any outbox are lost, which is why backups run daily.

## Restore drills

Every backup is already restore-checked automatically, without the private key, before it is encrypted. In addition, **once a quarter** an operator restores the newest off-host copy with the real identity into a scratch database, as above, and records the date. This proves the off-host copy and the key work. CI runs the same backup → decrypt → restore path on every change to `deploy/` (job `backup-restore`).

## Backup alert

`AlondraBackupStale` or `AlondraRestoreCheckStale`:

1. `docker compose logs --tail 200 backup`.
2. Common causes:
   - `secrets/backup_recipients` has no `age1…` line;
   - the disk is full (`df -h`);
   - the database is down;
   - the scratch-database restore failed (look for `pg_restore` errors; it means the dump itself is bad, so fix this first).
3. Run one backup now: `docker compose run --rm backup once`.
