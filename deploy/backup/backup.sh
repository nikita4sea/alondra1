#!/bin/sh
# Encrypted PostgreSQL backups for Alondra1 (docs/runbooks/backup-restore.md).
#
#   backup.sh          run forever: one backup every BACKUP_INTERVAL_SECONDS
#   backup.sh once     one backup, then exit (CI, cron)
#
# Each run: pg_dump (custom format) into a private temp dir -> check the archive (pg_restore --list) -> restore it into
# a scratch database and run sanity queries -> encrypt with age to the PUBLIC key(s) in AGE_RECIPIENTS_FILE -> move the
# .age file into BACKUP_DIR atomically with a .sha256 -> delete plaintext -> prune old files -> update status.json.
# The server never holds the private key: only an operator with the identity file can decrypt (restore.sh).
set -eu

: "${PGHOST:=db}"
: "${PGPORT:=5432}"
: "${PGDATABASE:=alondra1}"
: "${PGUSER:=alondra1}"
: "${PGPASSWORD_FILE:=/run/secrets/postgres_password}"
: "${AGE_RECIPIENTS_FILE:=/run/secrets/backup_recipients}"
: "${BACKUP_DIR:=/backups}"
: "${BACKUP_INTERVAL_SECONDS:=86400}"
: "${BACKUP_RETENTION_DAYS:=35}"
: "${RESTORE_CHECK:=1}"
: "${RESTORE_CHECK_DATABASE:=alondra1_restore_check}"
export PGHOST PGPORT PGDATABASE PGUSER

STATUS_FILE="$BACKUP_DIR/status.json"

log() { printf '%s backup: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

# libpq reads the password from a pgpass file; never from the command line or a plain environment variable.
PGPASSFILE="$(mktemp)"
export PGPASSFILE
chmod 0600 "$PGPASSFILE"
printf '*:*:*:%s:%s\n' "$PGUSER" "$(cat "$PGPASSWORD_FILE")" > "$PGPASSFILE"

status_value() {
	# Reads a number from the previous status.json (no jq in the image).
	[ -f "$STATUS_FILE" ] && sed -n "s/.*\"$1\": *\([0-9]*\).*/\1/p" "$STATUS_FILE" | head -n 1 || true
}

write_status() {
	tmp_status="$STATUS_FILE.tmp"
	printf '{"last_success_unix": %s, "last_restore_check_unix": %s, "last_file": "%s"}\n' "$1" "$2" "$3" > "$tmp_status"
	mv "$tmp_status" "$STATUS_FILE"
}

run_once() {
	if ! grep -q '^age1' "$AGE_RECIPIENTS_FILE" 2>/dev/null; then
		log "no age recipient (public key) in $AGE_RECIPIENTS_FILE; refusing to write an unencrypted or unreadable backup"
		return 1
	fi
	mkdir -p "$BACKUP_DIR"
	work="$(mktemp -d)"
	trap 'rm -rf "$work"' EXIT INT TERM
	stamp="$(date -u +%Y%m%dT%H%M%SZ)"
	name="alondra1-$stamp.dump.age"

	log "dumping $PGDATABASE"
	pg_dump --format=custom --no-owner --no-privileges --file="$work/db.dump"
	pg_restore --list "$work/db.dump" > /dev/null

	restore_checked="$(status_value last_restore_check_unix)"
	if [ "$RESTORE_CHECK" = "1" ]; then
		log "restore check into $RESTORE_CHECK_DATABASE"
		dropdb --if-exists "$RESTORE_CHECK_DATABASE"
		createdb "$RESTORE_CHECK_DATABASE"
		pg_restore --no-owner --no-privileges --exit-on-error --dbname="$RESTORE_CHECK_DATABASE" "$work/db.dump"
		migrations="$(psql --dbname="$RESTORE_CHECK_DATABASE" --tuples-only --no-align --command='SELECT count(*) FROM schema_migrations')"
		users="$(psql --dbname="$RESTORE_CHECK_DATABASE" --tuples-only --no-align --command='SELECT count(*) FROM users')"
		dropdb "$RESTORE_CHECK_DATABASE"
		if [ "${migrations:-0}" -lt 1 ]; then
			log "restore check failed: no migrations in the restored copy"
			return 1
		fi
		log "restore check ok: $migrations migrations, $users users"
		restore_checked="$(date +%s)"
	fi

	age --encrypt --recipients-file "$AGE_RECIPIENTS_FILE" --output "$BACKUP_DIR/$name.partial" "$work/db.dump"
	(cd "$BACKUP_DIR" && mv "$name.partial" "$name" && sha256sum "$name" > "$name.sha256")
	rm -rf "$work"
	trap - EXIT INT TERM

	find "$BACKUP_DIR" -maxdepth 1 -name 'alondra1-*.dump.age*' -mtime +"$BACKUP_RETENTION_DAYS" -print -delete | while read -r old; do log "pruned $old"; done
	write_status "$(date +%s)" "${restore_checked:-0}" "$name"
	log "wrote $name ($(wc -c < "$BACKUP_DIR/$name") bytes, encrypted)"
}

if [ "${1:-}" = "once" ]; then
	run_once
	exit $?
fi

while true; do
	# A failed run leaves status.json unchanged; the BackupStale alert fires when it gets too old.
	run_once || log "backup FAILED"
	sleep "$BACKUP_INTERVAL_SECONDS"
done
