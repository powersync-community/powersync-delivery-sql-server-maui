#!/usr/bin/env bash
# Simulated back office. Writes straight to SQL Server with sqlcmd (not through the API), the way
# dispatch and ERP systems usually do.
#
#   dispatch.sh list   [--driver ID]                          Today's stops
#   dispatch.sh add    [--driver ID] "<recipient>" "<address>"  Append a stop at the next sequence
#   dispatch.sh cancel [--driver ID] <sequence>                 Cancel a stop (not if it's delivered)
#
# The driver defaults to whoever has the most recently created stops for today.
set -euo pipefail
cd "$(dirname "$0")/.."

usage() {
  sed -n '5,7s/^#   /  /p' "$0" >&2
  exit 2
}

if [ -f .env ]; then
  . scripts/load-env.sh; load_env .env
fi
: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is not set (copy .env.template to .env)}"

[ $# -ge 1 ] || usage
command="$1"; shift
driver=""
args=()
while [ $# -gt 0 ]; do
  case "$1" in
    --driver) [ $# -ge 2 ] || usage; driver="$2"; shift 2 ;;
    --driver=*) driver="${1#--driver=}"; shift ;;
    -h|--help) usage ;;
    *) args+=("$1"); shift ;;
  esac
done

# sqlcmd substitutes -v variables into the script as text, so double any single quotes first.
# The SQL below always uses them inside N'...' literals.
quote() { local q="'"; printf '%s' "${1//$q/$q$q}"; }

# Runs a T-SQL batch in the sqlserver container. Extra arguments are passed to sqlcmd (e.g. -v name=value).
sql() {
  local query="$1"; shift
  docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" \
    -d DeliveryApp -b -W "$@" -Q "SET NOCOUNT ON; $query"
}

# Today's route date, as the server sees it: the UTC date (see db/init.sql).
TODAY="CAST(SYSUTCDATETIME() AS date)"

if [ -z "$driver" ]; then
  # IDs come from NEWSEQUENTIALID(), so the highest one is the most recently created stop.
  driver=$(sql "SELECT TOP 1 driver_id FROM dbo.stops WHERE route_date = $TODAY ORDER BY id DESC;" -h -1 | tr -d '\r')
  if [ -z "$driver" ]; then
    echo "No stops for today yet. Open the app first, or pass --driver <id>." >&2
    exit 1
  fi
fi
echo "Driver: $driver"

case "$command" in
  list)
    [ ${#args[@]} -eq 0 ] || usage
    sql "SELECT sequence AS seq, status, recipient, address
         FROM dbo.stops
         WHERE driver_id = N'\$(driver)' AND route_date = $TODAY
         ORDER BY sequence;" \
      -s '|' -v driver="$(quote "$driver")" \
      | sed '/^[-|]*$/d' | column -t -s '|'
    ;;

  add)
    [ ${#args[@]} -eq 2 ] || usage
    sql "SET XACT_ABORT ON;
         BEGIN TRANSACTION;
         -- Lock the driver's route for today so two adds can't take the same sequence number.
         DECLARE @seq INT = (SELECT ISNULL(MAX(sequence), 0) + 1 FROM dbo.stops WITH (UPDLOCK, HOLDLOCK)
                             WHERE driver_id = N'\$(driver)' AND route_date = $TODAY);
         INSERT INTO dbo.stops (driver_id, route_date, sequence, recipient, address)
         VALUES (N'\$(driver)', $TODAY, @seq, N'\$(recipient)', N'\$(address)');
         COMMIT;
         PRINT CONCAT('Added stop ', @seq, '.');" \
      -v driver="$(quote "$driver")" recipient="$(quote "${args[0]}")" address="$(quote "${args[1]}")"
    ;;

  cancel)
    [ ${#args[@]} -eq 1 ] || usage
    case "${args[0]}" in ''|*[!0-9]*) echo "Sequence must be a number: ${args[0]}" >&2; exit 2 ;; esac
    sql "SET XACT_ABORT ON;
         BEGIN TRANSACTION;
         DECLARE @status NVARCHAR(16) = (SELECT status FROM dbo.stops WITH (UPDLOCK)
                                         WHERE driver_id = N'\$(driver)' AND route_date = $TODAY AND sequence = \$(seq));
         IF @status IS NULL
           RAISERROR('No stop %d for this driver today.', 16, 1, \$(seq));
         ELSE IF @status = N'delivered'
           PRINT CONCAT('Warning: stop ', \$(seq), ' is already delivered. Nothing changed.');
         ELSE IF @status = N'canceled'
           PRINT CONCAT('Stop ', \$(seq), ' is already canceled.');
         ELSE
         BEGIN
           UPDATE dbo.stops SET status = N'canceled', updated_at = SYSUTCDATETIME()
           WHERE driver_id = N'\$(driver)' AND route_date = $TODAY AND sequence = \$(seq);
           PRINT CONCAT('Canceled stop ', \$(seq), '.');
         END
         COMMIT;" \
      -v driver="$(quote "$driver")" seq="${args[0]}"
    ;;

  *) usage ;;
esac
