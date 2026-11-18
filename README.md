# Delivery Driver App: SQL Server + ASP.NET Core + .NET MAUI

A small offline-first delivery-driver app: SQL Server, an ASP.NET Core minimal API and the PowerSync Service in Docker, and a .NET MAUI iOS app that reads and writes a local SQLite database PowerSync keeps in sync. A driver sees today's stops and records each as **Delivered** or **Failed**, with or without a network. Dispatch (simulated by a script) adds and cancels stops by writing straight to SQL Server, and those changes reach the device on their own. See [SPEC.md](SPEC.md) (the online-only baseline) and [SPEC_POWERSYNC_EXISTING_API.md](SPEC_POWERSYNC_EXISTING_API.md) (adding PowerSync) for the details.

## Prerequisites

- Docker. On Apple Silicon, enable Rosetta (Docker Desktop → Settings → General → "Use Rosetta for x86_64/amd64 emulation"); SQL Server runs as `linux/amd64`.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the MAUI workload (`dotnet workload install maui`). Only the app needs it; the server builds in Docker.
- An Xcode version that matches the installed iOS workload (`dotnet workload list` shows it; the [release notes](https://github.com/dotnet/macios/releases) list the Xcode each version needs). Known good: .NET SDK 10.0.404 / workload 10.0.404 with Xcode 27.2.

## Run it

1. Configure:

   ```bash
   cp .env.template .env
   ```

   `POWERSYNC_DB_PASSWORD` is the password of the SQL login PowerSync replicates with. It must meet SQL Server's password policy and be URL-safe, because it goes into an `mssql://` URI.

2. Start everything:

   ```bash
   docker compose up -d --build
   ```

   | Container | What it does |
   |---|---|
   | `sqlserver` | SQL Server, with SQL Server Agent enabled (CDC capture runs as an Agent job) |
   | `db-init` | One-shot: creates the database and tables (`db/init.sql`, idempotent) |
   | `db-powersync` | One-shot: prepares the database for PowerSync (`db/powersync.sql`, idempotent, see below) |
   | `server` | The API on http://localhost:7070. On first start it generates its token signing key in `keys/` (gitignored) |
   | `mongo`, `mongo-rs-init` | PowerSync's bucket storage, a single-node MongoDB replica set |
   | `powersync` | The PowerSync Service on http://localhost:8080. It checks the app's tokens against the API's JWKS (`/.well-known/jwks.json`), so the API's auth is unchanged |

   `db/powersync.sql` runs as `sa`, because it needs sysadmin: it enables CDC on the database and on `stops`, `deliveries` and `_powersync_checkpoints` (PowerSync writes checkpoints there), creates the `powersync_user` login with read access to the tables and the `cdc` schema, and sets the CDC capture job to scan the log every second (then restarts it). It stops with an error if SQL Server Agent isn't running, because CDC would then capture nothing. It's safe to run again: `docker compose run --rm db-powersync`.

   To check PowerSync is replicating, look for these lines in its log:

   ```bash
   docker compose logs powersync | grep -E 'Snapshot of|CDC polling started|Processed'
   ```

   `CDC polling started with interval of 1000ms` means it's following SQL Server's changes, and each change shows up as `Processed INSERT` / `Processed UPDATE`.

3. Build and run the app in the iOS simulator:

   ```bash
   dotnet build app/DeliveryApp -f net10.0-ios && dotnet build app/DeliveryApp -t:Run -f net10.0-ios
   ```

   Build first: on .NET 10, `-t:Run` on its own launches whatever was built last without compiling your changes.

   The app makes up a driver ID on first launch (shown under the title), and the server gives that driver 8 stops for today. The app reads everything from its local database, so it works offline: outcomes recorded without a network upload (through the same `POST /api/stops/{id}/deliveries`) when it's back. A line under the title says when it's offline or has changes waiting.

4. Play dispatch. The script defaults to the driver with the most recently created stops; pass `--driver driver-xxxxxxxx` to pick one.

   ```bash
   scripts/dispatch.sh list
   scripts/dispatch.sh add "Test Person" "1 Test St"
   scripts/dispatch.sh cancel 3
   ```

   The app shows these changes without a refresh, usually within 1 to 3 seconds. Measured on an M-series Mac, from running `dispatch.sh cancel` (which itself takes about 0.5 s) to the change on screen: 0.7 s, 0.8 s, 1.8 s, 2.1 s, 2.7 s (median 1.8 s). The delay is two polling loops in a row, each up to a second: SQL Server's CDC capture job copies the change from the transaction log into the change tables, then PowerSync polls the change tables and streams the change to the device.

To run the server outside Docker instead (e.g. while working on it): `docker compose stop server && scripts/run-server.sh`. It uses the same key in `keys/`. PowerSync fetches the JWKS from `http://server:7070` inside Docker, so it can't refresh the key while the `server` container is stopped (point `jwks_uri` in `powersync/service.yaml` at `http://host.docker.internal:7070/.well-known/jwks.json` if you need both).

To start over with an empty database: `docker compose down -v`.
