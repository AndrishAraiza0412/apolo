#!/usr/bin/env bash
# Respaldo comprimido de la base (formato custom de pg_dump) y limpieza de los anteriores a KEEP_DAYS días.
set -euo pipefail
file="/backups/apolo-$(date +%Y%m%d-%H%M%S).dump"
pg_dump -d apolo -Fc -f "$file.tmp"
mv "$file.tmp" "$file"
find /backups -name 'apolo-*.dump' -mtime +"${KEEP_DAYS:-30}" -delete
echo "Respaldo listo: $file"
