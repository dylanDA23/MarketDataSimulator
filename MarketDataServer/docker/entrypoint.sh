#!/usr/bin/env bash
set -e
PGHOST=${PGHOST:-postgres_server}
PGPORT=${PGPORT:-5432}
attempt=0
until nc -z "$PGHOST" "$PGPORT" || [ $attempt -ge 60 ]; do
  attempt=$((attempt+1))
  echo "Waiting for Postgres at $PGHOST:$PGPORT... ($attempt)"
  sleep 1
done
exec dotnet MarketDataServer.dll
