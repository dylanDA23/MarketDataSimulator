# Stage 1: build the app using the SDK
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy csproj(s) and restore as a separate layer for caching
COPY MarketDataServer/MarketDataServer.csproj MarketDataServer/
# If the server references other project folders, copy them here first (client doesn't need to be copied)
# COPY MarketDataShared/*.csproj MarketDataShared/
RUN dotnet restore MarketDataServer/MarketDataServer.csproj

# copy everything and publish
COPY . .
WORKDIR /src/MarketDataServer
RUN dotnet publish -c Release -o /app/publish /p:PublishTrimmed=false

# Stage 2: runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# Install netcat so entrypoint can nc -z the Postgres host
USER root
RUN apt-get update \
 && apt-get install -y netcat \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy published app from build stage
COPY --from=build /app/publish ./

# Copy entrypoint script into /app and ensure it is executable
COPY MarketDataServer/docker/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Set environment variables for Kestrel
# Use HTTP (plaintext) on port 5000; if you want TLS for gRPC enable/modify accordingly.
ENV ASPNETCORE_URLS=http://+:5000

# Expose the port the app listens on (gRPC over HTTP/2 can use 5000 for dev)
EXPOSE 5000

# Run the shell script as PID 1 (it will exec dotnet and keep signals intact)
ENTRYPOINT ["/app/entrypoint.sh"]
