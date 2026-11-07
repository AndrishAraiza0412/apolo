#!/usr/bin/env bash
# Revisión de seguridad antes de cada versión (gestión de vulnerabilidades y cambios): pruebas, dependencias y secretos.
# Uso: scripts/security-check.sh  (desde la raíz del repositorio). Sale con error si algo falla.
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET=$([ -x .dotnet/dotnet ] && echo .dotnet/dotnet || echo dotnet)

echo "== Pruebas"
"$DOTNET" test backend/Apolo.Tests --nologo -v q

echo "== Paquetes .NET vulnerables"
out=$("$DOTNET" list backend/Apolo.Api package --vulnerable --include-transitive)
echo "$out" | grep -q "no tiene paquetes vulnerables\|has no vulnerable packages" || { echo "$out"; exit 1; }

echo "== Dependencias npm de producción"
(cd frontend && npm audit --omit=dev --audit-level=moderate)

echo "== Secretos en el código"
if grep -rInE "(sk-ant-[A-Za-z0-9_-]{10,}|BEGIN (RSA |EC )?PRIVATE KEY|Password=[^;\"]{6,};)" \
    --exclude-dir={node_modules,bin,obj,dist,.dotnet,backups} --exclude=appsettings.Development.json --exclude=docker-compose.yml . ; then
  echo "Posibles secretos encontrados arriba."; exit 1
fi
echo "Todo en orden."
