# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY MarketDataClient/MarketDataClient.csproj ./MarketDataClient.csproj
RUN dotnet restore ./MarketDataClient.csproj

COPY . .
WORKDIR /src/MarketDataClient
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends netcat-openbsd \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

COPY MarketDataClient/docker/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENTRYPOINT ["/app/entrypoint.sh"]
