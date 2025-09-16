# MarketDataSimulator

MarketDataSimulator is a small example application that demonstrates a gRPC market data server and a console UI client.  
The server simulates (or proxies live) order book snapshots and updates. The client can run a realtime console UI and optionally persist incoming messages to a local PostgreSQL database.

This README shows how to run the app cross-platform (Windows / macOS / Linux) either with `dotnet` locally or inside Docker using `docker-compose`.

---

## Table of contents

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Quickstart (Docker / docker-compose) — recommended](#quickstart-docker--docker-compose---recommended)
- [Run locally with dotnet (dev)](#run-locally-with-dotnet-dev)
  - [Start Server](#start-server)
  - [Start Client (UI + persistence)](#start-client-ui--persistence)
- [Environment variables & configuration](#environment-variables--configuration)
- [Verifying persistence (snapshots & updates)](#verifying-persistence-snapshots--updates)
- [Logs & troubleshooting](#logs--troubleshooting)
- [Graceful shutdown (Ctrl+C / SIGINT)](#graceful-shutdown-ctrlc--sigint)
- [Advanced: migrations and EF tools](#advanced-migrations-and-ef-tools)
- [Development notes & file layout](#development-notes--file-layout)


---

## Features

- gRPC bidirectional streaming server with simulated or live Binance feed.
- Console UI client (Spectre.Console) that subscribes and renders orderbooks.
- Optional client-side persistence to PostgreSQL (snapshots + updates).
- Graceful cancellation/cleanup on Ctrl+C (SIGINT).
- Dockerfiles + `docker-compose.yml` for easy cross-platform startup.

---

## Prerequisites

### For running via Docker (recommended)
- Docker (Engine) installed and running.
- `docker-compose` (v1 or v2; most modern Docker installs include compose).

### For running locally with `dotnet run`
- .NET 8 SDK (matches `TargetFramework` net8.0).
- A local PostgreSQL server (or a Docker container with Postgres).

> Note: You can use Docker for Postgres even when running the server and client locally.

---

## Quickstart (Docker / docker-compose) — recommended

The repository includes a `docker/docker-compose.yml` which:

- runs a Postgres server container (`postgres_server`),
- runs `marketdata_server` (API),
- runs `marketdata_client` (console app).

**Start everything:**

```bash
# from repo root: MarketDataSimulator/
docker compose -f Docker/docker-compose.yml up --build

# Run Detached
docker compose -f Docker/docker-compose.yml up --build -d

# Stop and Remove
docker compose -f Docker/docker-compose.yml down
```

## Run locally with dotnet (dev)
Below are direct dotnet run instructions for each OS. Use whichever host/port matches your environment.
  ```
  Important: The client and server use environment variables for DB connection strings. See Environment variables & configuration
  ```
**Start Server**

---

***Start Server Simulation mode:***

> Default server behaviour is Simulation mode.

(macOS / Linux / WSL)
```
cd MarketDataSimulator/MarketDataServer
export SERVER_POSTGRES_CONN="Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=marketdb"
dotnet run
```
(Windows, PowerShell)
```
cd .\MarketDataServer
$env:SERVER_POSTGRES_CONN = "Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=marketdb"
dotnet run
```
Server defaults to listening on http://localhost:5000 (check console logs).

***Start Server (Live / Binance):***

> Set feed mode to Live, this will attempt to connect to Binance APIs.

(macOS / Linux / WSL)
```
cd MarketDataSimulator/MarketDataServer
export MARKET_FEED_MODE="Live"
export SERVER_POSTGRES_CONN="Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=marketdb"
dotnet run
```
(Windows, PowerShell)
```
cd .\MarketDataServer
$env:MARKET_FEED_MODE = "Live"
$env:SERVER_POSTGRES_CONN = "Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=marketdb"
dotnet run
```
> Note: Live mode requires network connectivity to Binance. If you are behind a corporate proxy or firewall, Live mode may fail.

**Start Client (UI only)**

---

> Client connects directly to the server and renders orderbooks.


  



