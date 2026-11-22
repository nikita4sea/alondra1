#!/bin/sh
# Restores an encrypted Alondra1 backup (docs/runbooks/backup-restore.md). Run by an operator who holds the age
# identity (private key); the server never has it.
#
#   restore.sh <backup.dump.age> <age-identity-file> <target-database>
#
# The target database must exist and be empty, or FORCE=1 replaces its objects. Restoring into the live database
# (PGDATABASE, default alondra1) additionally requires I_UNDERSTAND_THIS_REPLACES_LIVE_DATA=1; stop the API first.
set -eu

if [ "$#" -ne 3 ]; then
	echo "usage: restore.sh <backup.dump.age> <age-identity-file> <target-database>" >&2
	exit 2
fi
backup="$1"
identity="$2"
target="$3"

: "${PGHOST:=db}"
: "${PGPORT:=5432}"
: "${PGUSER:=alondra1}"
: "${PGPASSWORD_FILE:=/run/secrets/postgres_password}"
export PGHOST PGPORT PGUSER

if [ "$target" = "${PGDATABASE:-alondra1}" ] && [ "${I_UNDERSTAND_THIS_REPLACES_LIVE_DATA:-0}" != "1" ]; then
	echo "refusing to restore into the live database $target without I_UNDERSTAND_THIS_REPLACES_LIVE_DATA=1" >&2
	exit 3
fi

if [ -f "$backup.sha256" ]; then
	(cd "$(dirname "$backup")" && sha256sum -c "$(basename "$backup").sha256")
fi

PGPASSFILE="$(mktemp)"
export PGPASSFILE
chmod 0600 "$PGPASSFILE"
printf '*:*:*:%s:%s\n' "$PGUSER" "$(cat "$PGPASSWORD_FILE")" > "$PGPASSFILE"
work="$(mktemp -d)"
trap 'rm -rf "$work" "$PGPASSFILE"' EXIT INT TERM

age --decrypt --identity "$identity" --output "$work/db.dump" "$backup"
pg_restore --list "$work/db.dump" > /dev/null

clean=""
if [ "${FORCE:-0}" = "1" ]; then
	clean="--clean --if-exists"
fi
# shellcheck disable=SC2086 # $clean is intentionally split into options
pg_restore --no-owner --no-privileges --exit-on-error $clean --dbname="$target" "$work/db.dump"
echo "restored $(basename "$backup") into $target: $(psql --dbname="$target" --tuples-only --no-align --command='SELECT count(*) FROM schema_migrations') migrations"
