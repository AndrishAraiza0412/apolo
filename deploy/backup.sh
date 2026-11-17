#!/usr/bin/env bash
# Compressed database backup (pg_dump custom format) and cleanup of those older than KEEP_DAYS days.
set -euo pipefail
file="/backups/apolo-$(date +%Y%m%d-%H%M%S).dump"
pg_dump -d apolo -Fc -f "$file.tmp"
mv "$file.tmp" "$file"
find /backups -name 'apolo-*.dump' -mtime +"${KEEP_DAYS:-30}" -delete
echo "Respaldo listo: $file"
