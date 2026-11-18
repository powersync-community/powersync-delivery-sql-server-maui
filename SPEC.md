# Delivery Driver App: SQL Server + ASP.NET Core + .NET MAUI

## Purpose

A small, realistic **online-only** delivery-driver app built on the Microsoft stack:

- **Database:** SQL Server 2022 (Developer edition, in Docker)
- **Backend:** ASP.NET Core minimal APIs (C#, .NET 9) with EF Core
- **App:** .NET MAUI (.NET 10, `net10.0-ios`), running in the iOS simulator

A driver opens the app and sees today's stops in order. At each stop they record the outcome, **Delivered** or **Failed**, with an optional note. Dispatch (a back-office system, simulated by a script that writes straight to SQL Server) can add or cancel stops during the day.

It is deliberately a plain REST app. It has no local cache and no offline support. When the network is down, requests fail and the driver sees an error. A stop canceled by dispatch only shows up when the driver refreshes. Keep it that way: simple, conventional, and easy to read. These gaps are what the follow-up PRs fix.

This spec is written for a coding agent. Each section ends with acceptance criteria. Build in this order: database, server, dispatch script, app.

### Where this goes next (context only; don't build any of it)

This repo is the `main` branch of a three-step example for an article:

1. **`main` (this spec):** the online-only REST app.
2. **PR 1:** add PowerSync. CDC on SQL Server, the PowerSync Service, a local SQLite database in the app, and `UploadData` calling *this* REST API.
3. **PR 2 (stacked on PR 1):** replace the upload path with PowerSync's reference write API (a Node container on port 6060) and its .NET connector.

Some baseline choices exist only so those PRs stay small. They are marked **(for later)** below. Follow them, but don't add anything else in anticipation of PowerSync.

## Start from the chat example

Most of the plumbing already exists in `/Users/carl/code/powersync/sql_server_maui_example`. Copy from commit `67b222a` (the online-only baseline with the Xcode fixes), **not** from the `powersync` branch.

**Copy, then rename `Chat` → `Delivery` (namespaces, project names, `ChatApp` database → `DeliveryApp`):**

- `.gitignore`. Add `keys/`.
- `scripts/db-init.sh`, `scripts/load-env.sh`, `scripts/run-server.sh`
- `server/ChatApi/Dockerfile`, `.dockerignore`, `Program.cs` (as a skeleton), `Settings.cs`, `Endpoints/AuthEndpoints.cs`, `Auth/TokenService.cs` (rewritten for RS256, see below)
- `app/ChatApp`: `App.xaml(.cs)`, `AppShell.xaml(.cs)`, `MauiProgram.cs`, `Platforms/`, `Resources/`, the csproj, `Services/ApiClient.cs` and `UserIdentity.cs` (as patterns), `Views/ErrorBanner.cs`, `ViewModels/ErrorMessages.cs`, `Converters/RelativeTimeConverter.cs`
- csproj: set `TargetFrameworks` to `net10.0-ios`, as on the chat repo's `powersync` branch, **without** the PowerSync package references or the `_DropPowerSyncDeviceSqlite` target. Keep the `Info.plist` and `SceneDelegate.cs` changes from `67b222a`.

**Don't copy:** `server/ChatApi/Replies/*` (OpenRouter, the queue, the worker, startup recovery), `MessageTemplateSelector.cs`, anything under `db/powersync.sql` or `powersync/`, `POWERSYNC_SPEC.md`, `NOTES.md`.

**Rewrite:** `db/init.sql`, entities, `DbContext`, endpoints, DTOs, view models, pages, `docker-compose.yml`, `.env.template`, `README.md`.

## Repository layout

```
/
├── SPEC.md
├── README.md               # How to run it (short; written last)
├── docker-compose.yml      # SQL Server, schema init and the API server
├── .env.template           # Copy to .env
├── db/
│   └── init.sql            # Creates the database and tables (idempotent)
├── keys/                   # Generated RSA signing key (gitignored)
├── scripts/
│   ├── db-init.sh          # Applies db/init.sql with sqlcmd inside the container
│   ├── dispatch.sh         # Simulated back office: add / cancel / list stops via sqlcmd
│   ├── load-env.sh
│   └── run-server.sh       # Loads .env and runs the server outside Docker (optional)
├── server/
│   └── DeliveryApi/         # ASP.NET Core project (with a Dockerfile)
└── app/
    └── DeliveryApp/         # .NET MAUI project
```

Run `git init` at the root. **Don't commit.** Leave everything uncommitted for review.

## Environment

`.env.template`:

```bash
# SQL Server (docker-compose.yml and scripts read this)
MSSQL_SA_PASSWORD=Your_strong_password1

# Server
CONNECTION_STRING=Server=localhost,1433;Database=DeliveryApp;User Id=sa;Password=Your_strong_password1;TrustServerCertificate=True
# RSA key that signs the API's tokens. Generated on first start if missing. For local development only.
JWT_KEY_PATH=keys/jwt-signing.pem
```

The server reads these settings from environment variables. In Docker, `docker-compose.yml` passes them from `.env`, points the connection string at the `sqlserver` service, and sets `JWT_KEY_PATH=/keys/jwt-signing.pem` with `./keys` mounted at `/keys`. Outside Docker, `scripts/run-server.sh` exports `.env` and runs `dotnet run --project server/DeliveryApi`.

**Port: the API listens on 7070**, in and out of Docker. **(for later)** PowerSync uses 8080 and the reference write API fixes itself to 6060, so 7070 keeps all three apart without editing this app later.

## 1. Database

### docker-compose.yml

Same as the chat example:

- `sqlserver` from `mcr.microsoft.com/mssql/server:2022-latest`, `platform: linux/amd64`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD` from `.env`, `MSSQL_PID=Developer`. Nothing else: this is a vanilla install.
- Port `1433:1433`, a named volume for `/var/opt/mssql`, `./db` mounted read-only at `/db`, and the `sqlcmd -Q "SELECT 1"` healthcheck.
- One-shot `db-init` (same image) that waits for `sqlserver` to be healthy, applies `db/init.sql`, and exits.
- `server` built from `server/DeliveryApi/Dockerfile`, listening on `http://+:7070`, publishing `7070:7070`, with `./keys:/keys`. It starts after `db-init` completes successfully.

### db/init.sql

Idempotent: it creates the `DeliveryApp` database if missing, switches to it, and creates the tables if missing (wrap each `CREATE` in an `IF OBJECT_ID(...) IS NULL` guard).

```sql
-- One row per package drop. Dispatch creates and cancels stops; drivers only record outcomes.
CREATE TABLE dbo.stops (
  id          UNIQUEIDENTIFIER NOT NULL CONSTRAINT pk_stops PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
  driver_id   NVARCHAR(64)     NOT NULL,
  route_date  DATE             NOT NULL,
  sequence    INT              NOT NULL,
  recipient   NVARCHAR(100)    NOT NULL,
  address     NVARCHAR(300)    NOT NULL,
  -- 'assigned' until the driver records an outcome. 'canceled' is set only by dispatch.
  status      NVARCHAR(16)     NOT NULL DEFAULT N'assigned'
              CONSTRAINT ck_stops_status CHECK (status IN ('assigned', 'delivered', 'failed', 'canceled')),
  updated_at  DATETIME2(7)     NOT NULL DEFAULT SYSUTCDATETIME(),
  -- For the foreign key in deliveries, which ties each delivery to its stop's driver.
  CONSTRAINT uq_stops_id_driver UNIQUE (id, driver_id)
);

CREATE INDEX ix_stops_driver_date ON dbo.stops (driver_id, route_date, sequence);

-- One row per delivery attempt. A failed attempt can be followed by another attempt.
CREATE TABLE dbo.deliveries (
  id           UNIQUEIDENTIFIER NOT NULL CONSTRAINT pk_deliveries PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
  stop_id      UNIQUEIDENTIFIER NOT NULL,
  driver_id    NVARCHAR(64)     NOT NULL,
  outcome      NVARCHAR(16)     NOT NULL CONSTRAINT ck_deliveries_outcome CHECK (outcome IN ('delivered', 'failed')),
  note         NVARCHAR(500)    NULL,
  recorded_at  DATETIME2(7)     NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT fk_deliveries_stop FOREIGN KEY (stop_id, driver_id)
    REFERENCES dbo.stops (id, driver_id) ON DELETE CASCADE
);

CREATE INDEX ix_deliveries_stop ON dbo.deliveries (stop_id, recorded_at);
```

Notes:
- IDs are generated by the server (`NEWSEQUENTIALID()`), as in a typical existing app. The client never invents IDs. (PR 1 changes this for deliveries.)
- `recorded_at` is server time. (PR 1 lets the device supply it, so an offline delivery keeps the time it actually happened.)
- `route_date` is the UTC date. Good enough for a demo; say so in a comment.

**Acceptance criteria**
- `docker compose up -d` creates the database and both tables, then starts the server. Running `scripts/db-init.sh` a second time succeeds and changes nothing.
- `sqlcmd ... -Q "SELECT name FROM DeliveryApp.sys.tables"` lists `stops` and `deliveries`.

## 2. Server (`server/DeliveryApi`)

ASP.NET Core minimal APIs on .NET 9, listening on `http://localhost:7070`. HTTP only, because this is local development.

- **Data access:** EF Core (`Microsoft.EntityFrameworkCore.SqlServer`), with entities mapped to the existing tables (snake_case column names). **No EF migrations**: `db/init.sql` owns the schema. Use raw SQL where it reads more clearly, such as the locking update below.
- **JSON:** `System.Text.Json`, snake_case property names, ISO-8601 UTC timestamps.
- **Logging:** the default console logger. Log each recorded outcome as `Stop {id}: {outcome}`.

### Auth

This stands in for real sign-in, as in the chat example. The app makes up a driver ID and the server issues a token for it without asking any questions. Put a comment in the code saying this, and that a real app would only issue tokens to signed-in drivers.

**Tokens are RS256, and the public key is published as a JWKS. (for later)** This is what an identity provider such as Entra ID does, and it's what PowerSync and the reference write API need, so neither PR has to touch auth.

- On startup, load the RSA private key (PEM) from `JWT_KEY_PATH`. If the file doesn't exist, generate a 2048-bit key, write it there (create the directory), and log `Generated JWT signing key at {path}`.
- Key ID (`kid`): `delivery-api-1`.
- Claims: `sub` = driver ID, `iss` = `delivery-api`, `aud` = `delivery-api`, expiry 1 hour.
- `GET /.well-known/jwks.json` (anonymous) returns the public key as a JWKS: `{ "keys": [{ "kty": "RSA", "use": "sig", "alg": "RS256", "kid": "delivery-api-1", "n": "…", "e": "…" }] }`.
- The API validates its own tokens with `Microsoft.AspNetCore.Authentication.JwtBearer` using the same key directly (not by fetching its own JWKS), checking signature, issuer, audience and lifetime.
- Every route except the token and JWKS routes requires `Authorization: Bearer <token>` and responds `401` without it.
- Every query is scoped to the token's `sub`. A stop that belongs to another driver returns `404`, not `403`.

### Seeding a route

Demo convenience, with a comment saying so (in reality, dispatch plans routes): when `POST /api/auth/token` is called for a driver who has **no stops for today**, insert 8 stops for today in one transaction, `sequence` 1–8, with made-up recipients and street addresses from a fixed list in code. Calling it again the same day inserts nothing.

### API

All request and response bodies are JSON. Errors use the shape `{ "error": "<message>" }`.

| Method & path | Request | Response |
|---|---|---|
| `POST /api/auth/token` | `{ "driver_id": "…" }` | `200 { "token": "…", "expires_at": "<ISO>" }`. Seeds today's route if needed. `400` if `driver_id` is missing or longer than 64 characters |
| `GET /.well-known/jwks.json` | n/a | `200` JWKS, as above |
| `GET /api/stops` | n/a | `200 [Stop]`: today's stops for the driver, ordered by `sequence` |
| `GET /api/stops/{id}` | n/a | `200 { "stop": Stop, "deliveries": [Delivery] }`, deliveries ordered by `recorded_at`. `404` if not found |
| `POST /api/stops/{id}/deliveries` | `{ "outcome": "delivered" \| "failed", "note": "…" }` | `201 { "stop": Stop, "delivery": Delivery }`. `400` for an unknown outcome or a note over 500 characters (after trimming; empty becomes `null`). `404` if not found. `409` per the rule below |

```jsonc
// Stop
{ "id": "…", "route_date": "2026-11-18", "sequence": 3, "recipient": "…", "address": "…", "status": "assigned" | "delivered" | "failed" | "canceled", "updated_at": "…" }
// Delivery
{ "id": "…", "stop_id": "…", "outcome": "delivered" | "failed", "note": "…" | null, "recorded_at": "…" }
```

### The business rule

`POST /api/stops/{id}/deliveries`, in one transaction:

1. `UPDATE dbo.stops SET updated_at = SYSUTCDATETIME() WHERE id = @id AND driver_id = @driver`. If no row is updated, return `404`. This also locks the stop row until commit, so a concurrent cancel from dispatch can't slip in between the check and the insert. (Same pattern as the chat example's send route; keep the comment explaining it.)
2. Read the stop's `status`:
   - `canceled` → `409 { "error": "Dispatch canceled this stop." }`
   - `delivered` → `409 { "error": "This stop is already delivered." }`
   - `assigned` or `failed` → continue. A failed stop can be attempted again.
3. Insert the delivery, set `stops.status` to the outcome, commit, and return `201`.

This rule must survive both PRs. It's the article's example of business logic that stays on the server.

**Acceptance criteria** (server running against an initialized database):

```bash
API=localhost:7070
TOKEN=$(curl -s $API/api/auth/token -H 'content-type: application/json' -d '{"driver_id":"d1"}' | jq -r .token)
AUTH="Authorization: Bearer $TOKEN"

curl -s -o /dev/null -w '%{http_code}\n' $API/api/stops                                  # 401
curl -s $API/.well-known/jwks.json | jq '.keys[0].kid'                                   # "delivery-api-1"
curl -s $API/api/stops -H "$AUTH" | jq 'length'                                          # 8
STOP=$(curl -s $API/api/stops -H "$AUTH" | jq -r '.[0].id')
curl -s $API/api/stops/$STOP/deliveries -H "$AUTH" -H 'content-type: application/json' \
  -d '{"outcome":"failed","note":"No one home"}' | jq '.stop.status'                     # "failed"
curl -s $API/api/stops/$STOP/deliveries -H "$AUTH" -H 'content-type: application/json' \
  -d '{"outcome":"delivered"}' | jq '.stop.status'                                       # "delivered"
curl -s -o /dev/null -w '%{http_code}\n' $API/api/stops/$STOP/deliveries -H "$AUTH" \
  -H 'content-type: application/json' -d '{"outcome":"delivered"}'                       # 409
curl -s $API/api/stops/$STOP -H "$AUTH" | jq '.deliveries | length'                      # 2
```

- Getting a token for `d1` a second time doesn't add stops (still 8).
- A token for `d2` gets `404` on `d1`'s stop, and `d2` gets its own 8 stops.
- After `scripts/dispatch.sh cancel 2`, recording an outcome for stop 2 returns `409` with *"Dispatch canceled this stop."*
- The token header has `"alg":"RS256","kid":"delivery-api-1"`, and the payload has `iss` and `aud` = `delivery-api`. The signature verifies against the JWKS (check with any JWT tool or a short script).
- Restarting the server keeps the same key (old tokens still work). Deleting `keys/` and restarting generates a new one.

## 3. Dispatch script (`scripts/dispatch.sh`)

Simulates the back office. It writes **straight to SQL Server with sqlcmd**, not through the API, because that's how dispatch and ERP systems usually work. (for later: PR 1 shows these direct writes reaching the device through CDC.)

Runs `sqlcmd` inside the `sqlserver` container, as `db-init.sh` does. The driver defaults to whoever has the most recently created stops for today. `--driver <id>` overrides that.

| Command | Effect |
|---|---|
| `dispatch.sh list [--driver ID]` | Prints today's stops: sequence, status, recipient, address |
| `dispatch.sh add [--driver ID] "<recipient>" "<address>"` | Appends a stop at the next sequence number |
| `dispatch.sh cancel [--driver ID] <sequence>` | Sets that stop to `canceled` (and bumps `updated_at`). Prints a warning, and changes nothing, if it's already `delivered` |

Pass values to sqlcmd as `-v` variables, not by pasting them into the SQL text.

**Acceptance criteria**
- With `d1` seeded: `list` shows 8 stops, `add "Test Person" "1 Test St"` makes it 9, `cancel 3` shows stop 3 as `canceled`.
- `cancel` on a delivered stop leaves it `delivered`.

## 4. App (`app/DeliveryApp`)

.NET MAUI, `net10.0-ios` only. Shell navigation and MVVM with `CommunityToolkit.Mvvm`, as in the chat example. No local database and no caching: every screen shows what the API last returned.

### Configuration and networking

- `ApiBaseUrl = "http://localhost:7070"`, as one constant.
- `Info.plist`: keep `NSAppTransportSecurity` → `NSAllowsLocalNetworking = true`.
- `ApiClient`: one `HttpClient` wrapper in DI, typed methods per route, 15 s timeout. Keep all write calls in this one class. (for later: PR 1 calls them from `UploadData`.)

### Identity

- On first launch, generate a driver ID and store it in `Preferences`. Use a short readable form, `driver-` plus the first 8 hex characters of a GUID, so it's easy to pass to `dispatch.sh --driver`.
- `ApiClient` fetches a token on first use and keeps it in memory. On `401`, it fetches a new token and retries the request once.

### Screens

**Stops page** (the Shell root)
- Title: *"Today's route"*. Below the title, a small label with the driver ID.
- A list of today's stops: sequence number, recipient, address, and a status badge (assigned: neutral; delivered: green; failed: amber; canceled: gray, with the row text struck through).
- Loaded each time the page appears, and with pull-to-refresh. **No polling.** A stop canceled by dispatch only appears after a refresh. That's the baseline's main pain point, so don't fix it.
- Tapping a stop opens the Stop page.
- Empty state: *"No stops today."*

**Stop page**
- Recipient, address and status at the top.
- Earlier attempts for this stop: outcome, note, and `recorded_at` as relative time.
- When the status is `assigned` or `failed`: a single-line note `Entry` and two buttons, **Delivered** and **Failed**. Both are disabled while a request is in progress. When the status is `delivered` or `canceled`, hide them and show the status instead.
- On success, update the stop from the response, clear the note, close the keyboard, then reload the stop (`GET /api/stops/{id}`) so attempts recorded elsewhere also show. Don't just append the new delivery. Going back shows the new status, because the list reloads on appear.
- Pull-to-refresh reloads the stop. No polling.

### Errors and offline

These are deliberate baseline behavior, not bugs to fix:
- A network error, timeout or 5xx: show a dismissable banner (*"Couldn't reach the server. Check your connection and try again."*). **Keep the note** so the driver can press the button again. Don't queue anything and don't retry automatically.
- `409`: show the server's `error` text in the banner, then reload the stop so the page shows its real status.
- With the server stopped, the Stops page shows the banner and an empty list.

**Driving the simulator:** this Mac's Xcode has no `Simulator.app`, so use `xcrun simctl` (boot, install, launch, `io screenshot`) and [`axe`](https://github.com/cameroncooke/AXe) (installed with Homebrew) to tap, type and read the accessibility tree, e.g. `axe describe-ui --udid <udid>`, `axe tap --id DeliveredButton --udid <udid>`. Give the main controls `AutomationId`s (`StopsList`, `DeliveredButton`, `FailedButton`, `NoteEntry`, `ErrorBanner`) so they're easy to target. Take a screenshot as evidence for each check below.

**Acceptance criteria** (iOS simulator, server running):
- First launch shows 8 stops for today and the driver ID.
- Open stop 1, tap **Failed** with a note, then **Delivered**. Both attempts are listed, the buttons disappear, and the list shows stop 1 as delivered.
- Run `dispatch.sh cancel 2`. The list still shows stop 2 as assigned until you pull to refresh.
- Open stop 3, run `dispatch.sh cancel 3`, then tap **Delivered**. The banner says *"Dispatch canceled this stop."* and the page shows it as canceled.
- Kill and relaunch the app. Same driver ID, same stops.
- Stop the server and tap **Delivered** on an open stop. The banner appears and the note stays. Start the server and tap again: it works.

## 5. README.md

Write it last. Keep it short:
1. Prerequisites: Docker with Rosetta on Apple Silicon, the .NET 10 SDK with the MAUI workload, and the Xcode version that workload needs. (The server builds in Docker, so the SDK is only needed for the app.)
2. `cp .env.template .env`
3. `docker compose up -d --build`
4. Run the app (the `net10.0-ios` build and run command, as in the chat example's README).
5. Try dispatch: `scripts/dispatch.sh list`, `add`, `cancel`.
6. Optional: run the server outside Docker with `scripts/run-server.sh` (stop the `server` container first).

## Out of scope (deliberately)

- Offline support of any kind: a local database, a cache, an outbox, retries, background sync, polling.
- Client-generated IDs, and device timestamps for deliveries.
- Real sign-in, driver accounts, and a least-privilege database login. The server connects as `sa`.
- Photos or signatures as proof of delivery, maps, navigation, barcode scanning, reordering stops, multi-day routes.
- A dispatch UI. The script is the back office.
- Android, Mac Catalyst and Windows targets.
- Unit tests, CI, and production configuration (HTTPS, secrets management, key rotation).
- Any SQL Server feature beyond plain tables: no change tracking, CDC, triggers or stored procedures.
