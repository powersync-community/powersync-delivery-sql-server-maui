# Delivery Driver App: SQL Server + ASP.NET Core + .NET MAUI

A small offline-capable delivery-driver app: SQL Server and an ASP.NET Core minimal API in Docker, and a .NET MAUI iOS app with a local SQLite database that [Dotmim.Sync](https://dotmimsync.readthedocs.io/) keeps in sync with SQL Server, through a sync endpoint in the same API. A driver sees today's stops and records each as **Delivered** or **Failed**, with or without a network. Dispatch (simulated by a script) adds and cancels stops by writing straight to SQL Server; the app sees those changes at its next sync. See [SPEC.md](SPEC.md) (the online-only baseline) and [SPEC_DOTMIM_SYNC.md](SPEC_DOTMIM_SYNC.md) for the details, [PARITY.md](PARITY.md) for how it compares with the PowerSync version, and [NOTES.md](NOTES.md) for the build log.

## Prerequisites

- Docker. On Apple Silicon, enable Rosetta (Docker Desktop → Settings → General → "Use Rosetta for x86_64/amd64 emulation"); SQL Server runs as `linux/amd64`.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the MAUI workload (`dotnet workload install maui`). Only the app needs it; the server builds in Docker.
- An Xcode version that matches the installed iOS workload (`dotnet workload list` shows it; the [release notes](https://github.com/dotnet/macios/releases) list the Xcode each version needs). Known good: .NET SDK 10.0.404 / workload 10.0.404 with Xcode 27.2.

## Run it

1. Configure:

   ```bash
   cp .env.template .env
   ```

2. Start SQL Server, create the database (idempotent) and start the API on http://localhost:7070:

   ```bash
   docker compose up -d --build
   ```

   | Container | What it does |
   |---|---|
   | `sqlserver` | SQL Server |
   | `db-init` | One-shot: creates the database and tables (`db/init.sql`, idempotent) |
   | `db-dotmim` | One-shot: turns on change tracking for the database (`db/dotmim.sql`, idempotent) |
   | `server` | The API on http://localhost:7070, now also hosting the sync endpoint `POST /api/sync`. On first start it generates its token signing key in `keys/` (gitignored) |

   There's no extra sync service. `db/dotmim.sql` is the only manual database step: `ALTER DATABASE DeliveryApp SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 14 DAYS, AUTO_CLEANUP = ON)`. On the first sync, Dotmim.Sync (running in the API, which connects as `sa`) does the rest: it enables change tracking on `stops` and `deliveries` and creates two scope tables (`scope_info`, `scope_info_client`), 16 stored procedures (`stops_*`, `deliveries_*`) and two table types (`*_BulkType`). The tables themselves don't change. A device that hasn't synced for longer than the retention period has to reinitialize.

   The sync endpoint uses the API's existing JWT auth (`[Authorize]`), filters rows to the token's driver, and applies the business rule (no outcome on a canceled or already delivered stop) to every uploaded row (`server/DeliveryApi/Sync/`). Rows that break it are dropped and logged (`docker compose logs server | grep Rejected`); the device isn't told (see PARITY.md, row 8). `POST /api/stops/{id}/deliveries` and the `GET`s are still there, but the app no longer uses them.

3. Build and run the app in the iOS simulator:

   ```bash
   dotnet build app/DeliveryApp -f net10.0-ios && dotnet build app/DeliveryApp -t:Run -f net10.0-ios
   ```

   Build first: on .NET 10, `-t:Run` on its own launches whatever was built last without compiling your changes.

   The app makes up a driver ID on first launch (shown under the title), and the server gives that driver 8 stops for today. The first sync creates the app's local tables and downloads them.

   The app reads and writes only its local database, so it works offline. It syncs (uploads its changes, then downloads the server's) **at startup, on pull-to-refresh and after each outcome you record**, and at no other time. A line under the title says *"Offline."* when the last sync couldn't reach the server and *"Changes pending."* while recorded outcomes haven't been uploaded. After the network comes back, pull to refresh to upload them.

4. Play dispatch. The script defaults to the driver with the most recently created stops; pass `--driver driver-xxxxxxxx` to pick one.

   ```bash
   scripts/dispatch.sh list
   scripts/dispatch.sh add "Test Person" "1 Test St"
   scripts/dispatch.sh cancel 3
   ```

   The app sees these changes at its next sync: pull to refresh. Measured from the pull to the change on screen: 0.87-1.37 s (median 0.93 s), most of it the 5 or so HTTP round trips one sync takes.

   To see sync working: `docker compose logs -f server` shows `Stop <id>: delivered` for each uploaded outcome and `Rejected …` for refused ones; after a pull, `scripts/dispatch.sh list` and the app agree.

To run the server outside Docker instead (e.g. while working on it): `docker compose stop server && scripts/run-server.sh`. It uses the same key in `keys/`.

To start over with an empty database: `docker compose down -v`.
