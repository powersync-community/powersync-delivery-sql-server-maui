# Delivery Driver App: SQL Server + ASP.NET Core + .NET MAUI, with CommunityToolkit.Datasync

A small offline-capable delivery-driver app: SQL Server and an ASP.NET Core API in Docker, and a .NET MAUI iOS app that reads and writes a local SQLite database which [CommunityToolkit.Datasync](https://communitytoolkit.github.io/Datasync/) syncs with the API's table controllers. A driver sees today's stops and records each as **Delivered** or **Failed**, with or without a network. Dispatch (simulated by a script) adds and cancels stops by writing straight to SQL Server; the app picks those changes up the next time it syncs. See [SPEC.md](SPEC.md) (the online-only baseline), [SPEC_SYNC_COMPARISON.md](SPEC_SYNC_COMPARISON.md) and [SPEC_DATASYNC.md](SPEC_DATASYNC.md) for the details, and [PARITY.md](PARITY.md) for how this compares with the PowerSync version.

## What's new compared with the baseline

- **SQL Server:** four columns, two triggers and two indexes on the existing tables (`db/datasync.sql`, see below). No new login, no CDC, no SQL Server Agent.
- **API (.NET 10 now; Datasync 10.x requires it):** two Datasync table controllers, `/tables/stop` and `/tables/delivery`, behind the existing JWT auth. Access control providers limit each driver to their own rows (from the token's `sub`) and enforce the business rule (no outcome on a canceled or already delivered stop). The existing `/api/...` endpoints are still there, but the app no longer uses them.
- **App:** an EF Core `OfflineDbContext` (`delivery.db` in the app's data directory). Screens read it; recording an outcome writes a delivery and the stop's status to it, and Datasync's operations queue sends them later. The app syncs (push, then pull) when a page appears and when you pull to refresh, as the toolkit's MAUI sample does. There is no background sync and no timer.
- **No new containers.** Just one more one-shot setup step.

## Prerequisites

- Docker. On Apple Silicon, enable Rosetta (Docker Desktop → Settings → General → "Use Rosetta for x86_64/amd64 emulation"); SQL Server runs as `linux/amd64`.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the MAUI workload (`dotnet workload install maui`). Only the app needs it; the server builds in Docker.
- An Xcode version that matches the installed iOS workload (`dotnet workload list` shows it; the [release notes](https://github.com/dotnet/macios/releases) list the Xcode each version needs). Known good: .NET SDK 10.0.404 / workload 10.0.404 with Xcode 27.2.

## Run it

1. Configure:

   ```bash
   cp .env.template .env
   ```

2. Start everything:

   ```bash
   docker compose up -d --build
   ```

   | Container | What it does |
   |---|---|
   | `sqlserver` | SQL Server, a vanilla install |
   | `db-init` | One-shot: creates the database and tables (`db/init.sql`, idempotent) |
   | `db-datasync` | One-shot: adds what Datasync needs to those tables (`db/datasync.sql`, idempotent, see below) |
   | `server` | The API on http://localhost:7070, with the table controllers. On first start it generates its token signing key in `keys/` (gitignored) |

   `db/datasync.sql` adds, to `stops` and `deliveries`: a `version ROWVERSION` column (optimistic concurrency: changes on every write, by anyone), a `deleted BIT` column (Datasync's soft-delete flag; soft delete is off, but the entity base class maps it), an `updated_at` column on `deliveries` (`stops` already has one), an `AFTER INSERT, UPDATE` trigger on each table that sets `updated_at` (incremental pulls ask for "rows with `updated_at` later than the last one I saw", so every writer must keep it current; the trigger does that for dispatch and anything else that writes SQL directly), and an index on `(driver_id, updated_at)`. IDs stay `UNIQUEIDENTIFIER`; the API maps them to Datasync's string IDs. Safe to run again: `docker compose run --rm db-datasync`.

3. Build and run the app in the iOS simulator:

   ```bash
   dotnet build app/DeliveryApp -f net10.0-ios && dotnet build app/DeliveryApp -t:Run -f net10.0-ios
   ```

   Build first: on .NET 10, `-t:Run` on its own launches whatever was built last without compiling your changes.

   The app makes up a driver ID on first launch (shown under the title), and the server gives that driver 8 stops for today. The app reads everything from its local database, so it works offline. Outcomes recorded without a network stay queued (also across app restarts) and upload at the next sync once the API is reachable: go back to the list, open a stop, or pull to refresh. A line under the title says *"Offline."*, *"Offline. Changes pending."* or *"Changes pending."* (as of the last sync). If the server refuses a queued outcome (for example, dispatch canceled the stop meanwhile), a red banner gives the reason. Such a refused change stays in the queue, and the app stops pulling until it's gone; reinstall the app to clear it. See PARITY.md, row 8.

4. Play dispatch. The script defaults to the driver with the most recently created stops; pass `--driver driver-xxxxxxxx` to pick one.

   ```bash
   scripts/dispatch.sh list
   scripts/dispatch.sh add "Test Person" "1 Test St"
   scripts/dispatch.sh cancel 3
   ```

   The app shows these changes at its next sync, not by itself: pull to refresh, or open a page. After a pull to refresh, the change shows in under a second (measured from the start of the gesture: median 0.75 s, range 0.35–1.4 s).

## How to tell sync is working

- Server log, one line per request with request logging on (set `Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Information` on the `server` service), or the table controllers' own `CreateAsync: created {id}` / `ReplaceAsync: replaced {id}` lines and `Stop {id}: {outcome}`:

  ```bash
  docker compose logs -f server | grep -E 'tables/|created|replaced|Stop '
  ```

  A sync is `POST /tables/delivery` and `PUT /tables/stop/{id}` for anything queued, then `GET /tables/stop?$filter=(updatedAt gt …)` and `GET /tables/delivery?…`. A refused change is a `409` with `{"error": "…"}`.
- On the device, the status line: *"Changes pending."* means something is in the operations queue.
- In the database: `docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d DeliveryApp -Q "SELECT TOP 5 outcome, recorded_at, updated_at FROM deliveries ORDER BY updated_at DESC"`. `recorded_at` is when the driver tapped, `updated_at` when it reached the server.

To run the server outside Docker instead (e.g. while working on it): `docker compose stop server && scripts/run-server.sh`. It uses the same key in `keys/`.

To start over with an empty database: `docker compose down -v`. To start the app over, uninstall it (the local database and its queue go with it).
