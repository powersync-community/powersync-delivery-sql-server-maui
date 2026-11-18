#!/usr/bin/env bash
# Waits for SQL Server to be healthy, then applies db/init.sql (idempotent).
set -euo pipefail
cd "$(dirname "$0")/.."

if [ -f .env ]; then
  . scripts/load-env.sh; load_env .env
fi
: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is not set (copy .env.template to .env)}"

echo "Waiting for SQL Server to become healthy..."
for _ in $(seq 1 60); do
  status=$(docker inspect --format '{{.State.Health.Status}}' "$(docker compose ps -q sqlserver)" 2>/dev/null || echo "missing")
  if [ "$status" = "healthy" ]; then break; fi
  sleep 2
done
if [ "$status" != "healthy" ]; then
  echo "SQL Server is not healthy (status: $status). Did you run 'docker compose up -d'?" >&2
  exit 1
fi

docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -b -i /db/init.sql
echo "Database initialized."
