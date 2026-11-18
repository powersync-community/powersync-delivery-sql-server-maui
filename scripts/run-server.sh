#!/usr/bin/env bash
# Loads .env and runs the API on http://localhost:7070 (stop the `server` container first).
set -euo pipefail
cd "$(dirname "$0")/.."

if [ ! -f .env ]; then
  echo ".env not found. Run: cp .env.template .env" >&2
  exit 1
fi
. scripts/load-env.sh; load_env .env

# dotnet run starts the app in the project directory, so resolve the key path against the repo root.
# That way the server uses the same key as the container (./keys is mounted at /keys there).
JWT_KEY_PATH="${JWT_KEY_PATH:-keys/jwt-signing.pem}"
case "$JWT_KEY_PATH" in
  /*) ;;
  *) export JWT_KEY_PATH="$PWD/$JWT_KEY_PATH" ;;
esac

exec dotnet run --project server/DeliveryApi
