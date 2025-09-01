# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY MarketDataServer/MarketDataServer.csproj ./MarketDataServer.csproj
RUN dotnet restore ./MarketDataServer.csproj

COPY . .
WORKDIR /src/MarketDataServer
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends netcat-openbsd \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

COPY MarketDataServer/docker/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["/app/entrypoint.sh"]
