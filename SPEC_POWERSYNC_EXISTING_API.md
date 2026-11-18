# Delivery Driver App: adding PowerSync, keeping the existing REST API (PR 1)

## Purpose

Take the baseline app (`main`, see `SPEC.md`) and make it work offline with PowerSync, while:

- keeping SQL Server as the source of truth, with the same tables
- keeping the ASP.NET Core API as the only write path: `UploadData` calls the existing `POST /api/stops/{id}/deliveries`
- keeping the business rule (no outcome on a canceled or already delivered stop) on the server, unchanged
- keeping `dispatch.sh` writing straight to SQL Server. Its changes now reach the device through CDC, with no refresh

The diff between `main` and this branch is what the article walks through. **Aim for the least code that shows the idea.** This is a demo, not a production app. Every change should be one that a reader with their own SQL Server + MAUI app would also make. Leave production hardening out of the code and add a one-line `// Production: …` comment where it would go; the article lists those separately. Don't add test-only code to the app or server; drive tests from outside (curl, sqlcmd, `simctl`, `axe`).

Work on a branch `powersync-existing-api` off `main`. **Don't commit.** Leave the changes for review.

**Out of scope for this PR:** PowerSync's reference write API (PR 2, stacked on this one, replaces the upload path). Don't build toward it, apart from keeping all upload code in one class.

### Reuse the chat example

The chat example (`/Users/carl/code/powersync/sql_server_maui_example`, branch `powersync`, tag `powersync-rest`) already did this migration once. **Read its `NOTES.md` before starting.** It records the problems below, which you shouldn't have to rediscover. Copy from it rather than writing from scratch:

- `db/powersync.sql`: swap the table names, otherwise keep it as is. It has the final fixes for the `cdc_reader` role, `ALTER LOGIN` on re-run, and stopping and restarting the capture job safely by checking `msdb.dbo.sysjobactivity`.
- `docker-compose.yml`: the `db-powersync`, `powersync`, `mongo` and `mongo-rs-init` services, and the `powersync` healthcheck.
- `powersync/service.yaml`: change the auth section, see section 2. **Don't copy `start.sh`.** It only existed to base64-encode the HS256 secret, and RS256 with a JWKS URL makes it unnecessary.
- `app/ChatApp/Data/Database.cs` (the pattern).

Problems already known (from the chat example's `NOTES.md`):

- PowerSync sends `UNIQUEIDENTIFIER` as **upper-case** text. IDs created on the device must be upper case too, or each delivery shows up twice after sync.
- `DATETIME2` arrives as ISO 8601 with **9** fractional digits and `Z` (`2026-11-17T16:02:42.850000000Z`). The docs say 3 digits. Check this again here, and write local timestamps in exactly the format sync delivers, or `ORDER BY recorded_at` sorts local and synced rows inconsistently.
- `DATE` should arrive as `YYYY-MM-DD` (per the docs). Check it, because the "today's route" query depends on it.
- iOS build quirks: Xcode 27.2, `net10.0-ios`, no `Simulator.app`. Use `simctl` and `axe` as in `SPEC.md`.

## What changes, in one table

| | Baseline (`main`) | After |
|---|---|---|
| Reads | `ApiClient.GetStops` / `GetStop` over HTTP | Watched queries on local SQLite |
| Writes | `POST` to the API; fails offline | Local write → upload queue → `UploadData` → the same `POST` |
| Dispatch changes | Seen only after a refresh | CDC → PowerSync → device, without the driver doing anything |
| Delivery IDs | Server `NEWSEQUENTIALID()` | Generated on the device; the server accepts them |
| `recorded_at` | Server time | Device time when the driver tapped (the server accepts it) |
| Auth | RS256 + JWKS | **Unchanged.** PowerSync reads the same JWKS |
| SQL Server | Plain tables | Plus CDC, a `powersync_user` login and `_powersync_checkpoints` |
| Infra | SQL Server + API | Plus the PowerSync Service and its bucket storage (MongoDB) |

## 1. SQL Server

### docker-compose.yml

- `sqlserver`: add `MSSQL_AGENT_ENABLED: "true"`. CDC capture runs as a SQL Server Agent job, and without the Agent nothing is captured.
- New one-shot `db-powersync` service that runs `db/powersync.sql` after `db-init` (copy it from the chat example).

### db/powersync.sql (idempotent)

Copy it from the chat example. For `DeliveryApp`, in order:

1. Fail loudly if SQL Server Agent isn't running.
2. Enable CDC on the database.
3. Enable CDC on `dbo.stops` and `dbo.deliveries` with `@role_name = N'cdc_reader'`.
4. Create the `powersync_user` login and user (password from `POWERSYNC_DB_PASSWORD`), with `SELECT` on both tables and on schema `cdc`, membership of `cdc_reader`, `VIEW DATABASE PERFORMANCE STATE`, and `VIEW SERVER PERFORMANCE STATE` in `master`.
5. Create `dbo._powersync_checkpoints`, enable CDC on it, and grant `SELECT, INSERT, UPDATE` on it.
6. Set the capture job to `pollinginterval 1, maxtrans 5000, maxscans 10`, then restart the job safely.

Keep the comment at the top of each block: the article quotes this file as the checklist for the DBA.

`.env.template`: add `POWERSYNC_DB_PASSWORD` (URL-safe, because it goes into an `mssql://` URI).

**Acceptance criteria**
- `docker compose up -d` runs `db-powersync` successfully. Running it again succeeds and changes nothing.
- `SELECT name, is_tracked_by_cdc FROM DeliveryApp.sys.tables` shows `stops`, `deliveries` and `_powersync_checkpoints` as tracked.

## 2. PowerSync Service

### docker-compose.yml

- `powersync`: `journeyapps/powersync-service`, pinned to the latest stable tag (the chat example used `1.28.3`; check for something newer and log what you pick). Mount `./powersync` read-only, publish port 8080, and depend on `db-powersync` completing successfully, `mongo-rs-init` completing successfully and `server` having started. Use the normal entrypoint, since there's no `start.sh`.
- `mongo` and `mongo-rs-init`: copy them from the chat example.

### powersync/service.yaml

```yaml
replication:
  connections:
    - type: mssql
      uri: !env PS_DATA_SOURCE_URI          # mssql://powersync_user:…@sqlserver:1433/DeliveryApp, built in compose
      schema: dbo
      additionalConfig:
        trustServerCertificate: true         # local container with a self-signed certificate

storage:
  type: mongodb
  uri: !env PS_MONGO_URI

port: 8080

sync_config:
  path: sync-config.yaml

client_auth:
  # The API's own JWKS. PowerSync fetches the public key from it and checks the API's tokens directly.
  jwks_uri: http://server:7070/.well-known/jwks.json
  audience: ["delivery-api"]
```

This is the payoff of using RS256 in the baseline: **the API's auth doesn't change at all.** Fetching a JWKS from a private network address is allowed by default when self-hosting (`block_local_jwks` and `jwks_reject_ip_ranges` are unset). Mention that, with a link, in the article.

### powersync/sync-config.yaml

```yaml
config:
  edition: 3

streams:
  my_route:
    auto_subscribe: true
    queries:
      - SELECT * FROM stops WHERE driver_id = auth.user_id()
      - SELECT * FROM deliveries WHERE driver_id = auth.user_id()
```

- One auto-subscribed stream containing all of the driver's stops and deliveries. The app filters to today locally. For the demo there's only ever one day per driver. Add a comment that a real app would limit this to a date window, and the article will cover it.
- List both tables by name: SQL Server doesn't support wildcard table names, and both must have CDC enabled (section 1).

**Acceptance criteria**
- `powersync` becomes healthy. Its logs show the initial snapshot, then `CDC polling started with interval of 1000ms`. Paste the exact snapshot log line into `NOTES.md`.
- A token from `POST /api/auth/token` is accepted by `GET http://localhost:8080/sync/stream` (with a JSON body, as in the chat example's `NOTES.md`). A garbage token gets 401.

## 3. Server changes (`server/DeliveryApi`)

Keep these to the minimum. Each is a change a reader with an existing API would have to make, so each gets a sentence in the article. **No auth changes, no new endpoints.**

### `POST /api/stops/{id}/deliveries` accepts a client ID and is idempotent

Uploads are retried. For example, the network drops after the server committed but before the app heard back, so the same delivery can arrive twice. The [existing-API docs](https://docs.powersync.com/handling-writes/write-path/existing-api) say a create must not be rejected on retry just because the earlier attempt succeeded.

- The request body gets two optional fields: `{ "id": "<guid>", "outcome": "…", "note": "…", "recorded_at": "<ISO>" }`. Old clients that don't send them still work.
- Order inside the transaction:
  1. Lock the stop with the existing `UPDATE … updated_at`, returning `404` if it isn't found.
  2. **If `id` is given and a delivery with that ID exists for this driver and stop**, return `200` with the current stop and that delivery, and don't run the status rule. A retry of an accepted "delivered" must not get *"This stop is already delivered."*
  3. The status rule (`409`) and the insert, unchanged, except that the insert uses `id` when given (EF inserts a non-empty `Guid` as is; `Guid.Empty` still uses the database default).
- `recorded_at`: if present, store it. That's the time the driver tapped, which can be hours before the upload. If missing, use server time as before.
- Leave out, each with a `// Production: …` comment: an ID reused by another driver (today a primary-key error and a 500, which the queue retries forever), two identical requests at once (the second gets a 500, and its retry gets the 200), and checking `recorded_at` against a wrong device clock.

### Unchanged

`GET` endpoints (the new app doesn't use them; keep them, because the article says "your API stays"), the token endpoint, route seeding, the JWKS, `dispatch.sh`, and the schema.

**Acceptance criteria** (curl, with a driver token):
- Posting the same `id` twice returns `201`, then `200` with the same delivery. One row in SQL Server.
- Posting a new `id` "delivered" after the stop is delivered returns `409`. Retrying the accepted one still returns `200`.
- `recorded_at` 3 hours ago is stored as given.
- A request without `id` or `recorded_at` behaves exactly as on `main`.

## 4. App changes (`app/DeliveryApp`)

### Packages

Add `PowerSync.Common` 1.0.1. It bundles the native PowerSync extension for iOS itself, so `PowerSync.Maui` isn't needed.

`AppConfig.PowerSyncUrl = "http://localhost:8080"`, as a constant next to `ApiBaseUrl`. Don't add it to the token response: keeping the server untouched matters more than avoiding one constant.

### New: `Data/AppSchema.cs`

Attribute-based schema, one class per table, also used as the query result type (implement `IFromRow<T>`: the SDK doesn't fill `[Column]` properties from query rows):

- `stops`: `id`, `driver_id`, `route_date`, `sequence` (integer), `recipient`, `address`, `status`, `updated_at`
- `deliveries`: `id`, `stop_id`, `driver_id`, `outcome`, `note`, `recorded_at`
- Index `deliveries(stop_id, recorded_at)` and `stops(route_date, sequence)`.

### New: `Data/DeliveryConnector.cs` (`IPowerSyncBackendConnector`)

The only class that knows about REST writes. PR 2 replaces its body.

- `FetchCredentials`: reuse `ApiClient`'s token code. Return `new PowerSyncCredentials(AppConfig.PowerSyncUrl, token, expiresAt)`.
- `UploadData`: take one `CrudTransaction` at a time. Recording an outcome is one local transaction containing `PUT deliveries` and `PATCH stops (status)`.

| Op in the transaction | Action |
|---|---|
| `PUT deliveries` | `POST /api/stops/{stop_id}/deliveries { id, outcome, note, recorded_at }` |
| `PATCH stops` (`status` only) | Skip. The server sets the status itself as part of the POST; the local patch is only there so the UI updates immediately |
| anything else | Log it, then skip. The app writes nothing else |

- Classify each response as the [existing-API docs](https://docs.powersync.com/handling-writes/write-path/existing-api) describe:
  - **2xx:** applied.
  - **400, 404, 409:** a rejected write. The server will never accept it. Log it, set `LastRejection` (see "Rejected writes"), and carry on. Don't throw: that would block every later upload behind it.
  - **401:** clear the cached token and throw. The SDK retries, and `FetchCredentials` gets a new token.
  - **Network error, timeout, 5xx:** throw. The SDK retries with backoff. Never complete these.
- After every op is applied or rejected: `transaction.Complete()`.
- A rejected write disappears locally at the next sync, because the server never stored it: the delivery row goes and the stop's status comes back from the server. **Check this actually happens** and log what you see (the chat example never tested it).

### New: `Data/Database.cs`

A singleton that owns the `PowerSyncDatabase` (the file in `FileSystem.AppDataDirectory`), calls `Init()` and `Connect(connector)` once at startup, and exposes the database and sync status. Copy the pattern from the chat example.

### Writing a delivery (`StopViewModel`)

One `WriteTransaction`:

1. `INSERT INTO deliveries (id, stop_id, driver_id, outcome, note, recorded_at)`, with `id = Guid.NewGuid().ToString().ToUpperInvariant()` and `recorded_at` = device UTC now, in the format sync delivers.
2. `UPDATE stops SET status = ? WHERE id = ?`

That's all. No HTTP call, no error path for the network, and the banner never says "couldn't reach the server" when recording an outcome. Clear the note and close the keyboard as now.

### Reads

- `StopsViewModel`: watched query `SELECT * FROM stops WHERE route_date = ? ORDER BY sequence` (today's UTC date, as `YYYY-MM-DD`). Update `Stops` on the main thread.
- `StopViewModel`: watched queries for the stop (`WHERE id = ?`) and its deliveries (`WHERE stop_id = ? ORDER BY recorded_at`).
- Remove pull-to-refresh from both pages (nothing to refresh), and the reload-after-write code in `StopViewModel`. Note for the article what got deleted.
- Watched queries return the whole list each time. With 8–10 rows, just replace the list. If that makes the `CollectionView` flicker or lose its scroll position, diff it instead, and note which you did.

### Sync state (one line, both pages)

Replace the connection-error banner with a small status line under the title:

- **Online, nothing waiting:** hidden.
- **Offline, or uploads waiting:** *"Offline. 2 changes will upload when you reconnect."* Use `GetUploadQueueStats()` for the count, refreshed whenever sync status changes and after each local write.
- **Connected but uploads still waiting** (the server is rejecting or erroring): *"2 changes waiting to upload."*

Keep it factual and small. A driver needs to know their taps are safe, not see a spinner.

### Rejected writes

- `DeliveryConnector` holds the latest rejection in memory (`LastRejection`: the server's `error` text, the stop's sequence number from the local stop, and the outcome) and raises a change event.
- Both pages show it in the existing red banner: *"Stop 4: your 'delivered' wasn't saved. Dispatch canceled this stop."* Dismissing it clears it. Only the latest one is kept.
- `// Production:` the message is lost if the app restarts before the driver sees it. Keep it in a local-only table, or have the server record rejections in a table that syncs back.

### ApiClient

Shrinks to fetching tokens plus `RecordDeliveryAsync` (now with `id` and `recorded_at`), used only by `DeliveryConnector`. `GetStopsAsync` and `GetStopAsync` go.

### MauiProgram

Register `Database` and `DeliveryConnector` as singletons. Connect at startup, not on first page load.

### AutomationIds

Keep the existing ones. Add `SyncStatus` for the status line.

## 5. Acceptance checks

In the iOS simulator, driven with `simctl` and `axe` as in `SPEC.md`, with everything running in Docker. Start from a clean database (`docker compose down -v`, then `up`) and a fresh app install. "No network" means stopping both the `server` and `powersync` containers. Record the latency figures in `NOTES.md`.

1. **Online basics.** First launch shows 8 stops. Record Failed, then Delivered, on stop 1. Both appear immediately, and SQL Server has both rows with the device's `recorded_at` and upper-case IDs. No `GET /api/stops` in the server logs.
2. **Dispatch reaches the device without a refresh.** With the app open on the list, `dispatch.sh cancel 2`: stop 2 shows canceled with no driver action. `dispatch.sh add …`: the new stop appears. Measure the time from the command to the screen change (5 runs; record min, median and max).
3. **Cold start with no network.** Stop both containers, kill and relaunch the app: the full route shows, with each stop's status and history.
4. **Record offline.** With no network, record Failed with a note on stop 3, then Delivered. Both show immediately, and the status line says 2 changes are waiting. Bring the network back: they upload in order, the status line goes away, and SQL Server has 2 rows with `recorded_at` from when they were tapped, not from when they were uploaded.
5. **Restart while offline.** As in 4, but kill and relaunch the app before reconnecting. The changes are still there and still upload.
6. **Retry safety.** Covered by the curl checks in section 3 (the same `id` twice gives one row). Don't add server code to simulate it.
7. **Conflict while offline.** With no network, record Delivered on stop 4. Then `dispatch.sh cancel 4` (it writes to SQL Server directly, so it works with the API down). Reconnect: the upload gets a 409, the banner says stop 4's delivery wasn't saved because dispatch canceled it, the local delivery row disappears, and stop 4 shows canceled.
8. **Two devices.** A second simulator with the same driver ID, set without app code: `xcrun simctl spawn <udid> defaults write com.example.deliveryapp <key> <driver-id>` (find the key MAUI's `Preferences` uses with `defaults read` on the first simulator). A delivery recorded on one appears on the other.
9. **Screenshots for the article.** The same scenes as `assets/sql-server-delivery/baseline/` in the vault (`/Users/carl/notes/notes/projects/PowerSync/AgentRel/assets/sql-server-delivery/`), saved to a sibling `powersync-existing-api/` folder with matching file names where a scene has a counterpart. Status bar at 9:41. For no-network shots, set the status bar to show no signal. At least:
   - `01-home`: a mid-route state like the baseline's
   - `05-dispatch-live`: before and after a `dispatch.sh cancel`, with no refresh
   - `06-offline-record`: Delivered recorded offline, with the status line showing it's queued
   - `07-offline-cold-start`: the full route with no network
   - `04-rejected`: the banner from check 7
10. **Optional, if time allows:** rehearse a schema change for the article's "living with it" section. Add a nullable `signed_by` column to `deliveries`, follow the docs (new capture instance, sync config redeploy), and log every step and how long it took.

## 6. README.md

Add: the extra containers, `POWERSYNC_DB_PASSWORD`, what `db/powersync.sql` does and why it needs sysadmin, how to tell PowerSync is replicating (service logs), and the expected delay for dispatch changes (with the measured figures from check 2 and why: CDC capture job plus PowerSync polling).

## Snags log

Keep a running `NOTES.md` while building: the step, what happened, how long it took, and the fix. Include anything the docs got wrong or left out. It feeds the article directly, so don't tidy it.

## Out of scope (deliberately)

- PowerSync's reference write API and its .NET connector (PR 2).
- Limiting sync to a date window, and multi-day routes.
- Conflict resolution beyond "the server's rule wins and the driver is told".
- Production hardening of any kind (see the `// Production:` comments): keeping rejections across restarts, checking duplicate or foreign IDs, checking the device clock, debug and test switches.
- Real sign-in, Android, Mac Catalyst, Windows, PowerSync Cloud.
- Photos or signatures (attachments).
