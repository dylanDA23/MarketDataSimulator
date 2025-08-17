# Stage 1: build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY MarketDataClient/MarketDataClient.csproj MarketDataClient/
RUN dotnet restore MarketDataClient/MarketDataClient.csproj

COPY . .
WORKDIR /src/MarketDataClient
RUN dotnet publish -c Release -o /app/publish /p:PublishTrimmed=false

# Stage 2: runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
USER root
RUN apt-get update \
 && apt-get install -y netcat \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./
COPY MarketDataClient/docker/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# No need to expose a port for the worker; it's a background process
ENTRYPOINT ["/app/entrypoint.sh"]
