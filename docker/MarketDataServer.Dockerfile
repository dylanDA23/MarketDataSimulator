# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy csproj and restore
COPY ../MarketDataServer/MarketDataServer.csproj ./MarketDataServer.csproj
RUN dotnet restore ./MarketDataServer.csproj

# copy everything and publish
COPY .. .
WORKDIR /src/MarketDataServer
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Need root to install packages
USER root

# Install netcat provider explicitly (netcat-openbsd); keep image slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends netcat-openbsd \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

# copy entrypoint script (make sure file path matches your repo)
COPY ../MarketDataServer/docker/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["/app/entrypoint.sh"]
