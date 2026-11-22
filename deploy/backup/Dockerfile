# Backup sidecar: pg_dump 17 + age. Build: docker build -t alondra1-backup:dev deploy/backup
# Pin the base image digest before production (docs/runbooks/release.md).
FROM postgres:17-bookworm
RUN apt-get update \
 && apt-get install -y --no-install-recommends age \
 && rm -rf /var/lib/apt/lists/*
COPY backup.sh restore.sh /usr/local/bin/
# /backups is owned by postgres so a fresh named volume mounted there is writable by the non-root user.
RUN chmod 0755 /usr/local/bin/backup.sh /usr/local/bin/restore.sh \
 && mkdir -p /backups && chown postgres:postgres /backups
USER postgres
ENTRYPOINT ["/usr/local/bin/backup.sh"]
