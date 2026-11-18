# Delivery Driver App: SQL Server + ASP.NET Core + .NET MAUI

A small online-only delivery-driver app: SQL Server and an ASP.NET Core minimal API in Docker, and a .NET MAUI iOS app. A driver sees today's stops and records each as **Delivered** or **Failed**. Dispatch (simulated by a script) adds and cancels stops by writing straight to SQL Server. See [SPEC.md](SPEC.md) for the details.

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

   On first start the server generates its token signing key in `keys/` (gitignored).

3. Build and run the app in the iOS simulator:

   ```bash
   dotnet build app/DeliveryApp -f net10.0-ios && dotnet build app/DeliveryApp -t:Run -f net10.0-ios
   ```

   Build first: on .NET 10, `-t:Run` on its own launches whatever was built last without compiling your changes.

   The app makes up a driver ID on first launch (shown under the title), and the server gives that driver 8 stops for today.

4. Play dispatch. The script defaults to the driver with the most recently created stops; pass `--driver driver-xxxxxxxx` to pick one.

   ```bash
   scripts/dispatch.sh list
   scripts/dispatch.sh add "Test Person" "1 Test St"
   scripts/dispatch.sh cancel 3
   ```

   The app only sees these changes when you pull to refresh. That's the point of this baseline.

To run the server outside Docker instead (e.g. while working on it): `docker compose stop server && scripts/run-server.sh`. It uses the same key in `keys/`.

To start over with an empty database: `docker compose down -v`.
