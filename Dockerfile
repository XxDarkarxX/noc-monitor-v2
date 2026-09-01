# syntax=docker/dockerfile:1

# --- Build ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the .csproj files first so `dotnet restore` gets its own Docker
# layer and only reruns when a project reference or package actually changes,
# not on every source edit.
COPY src/NocMonitor.Core/NocMonitor.Core.csproj src/NocMonitor.Core/
COPY src/NocMonitor.Data/NocMonitor.Data.csproj src/NocMonitor.Data/
COPY src/NocMonitor.Alerts/NocMonitor.Alerts.csproj src/NocMonitor.Alerts/
COPY src/NocMonitor.Web/NocMonitor.Web.csproj src/NocMonitor.Web/
RUN dotnet restore src/NocMonitor.Web/NocMonitor.Web.csproj

COPY src/ src/
RUN dotnet publish src/NocMonitor.Web/NocMonitor.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# --- Runtime ---------------------------------------------------------------
# ASP.NET runtime only - no SDK, no build tools - in the final image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The base image runs as a non-root user by default ($APP_UID). Docker
# creates a fresh named volume owned by root, so without this, SQLite would
# fail to create nocmonitor.db under the /app/data mount with "permission
# denied". Owning the directory here (as root, before switching back) makes
# sure it's writable regardless of how the volume gets initialized.
#
# iputils-ping: this minimal runtime image has no `ping` binary. .NET's
# System.Net.NetworkInformation.Ping on Linux tries a raw ICMP socket first
# (needs CAP_NET_RAW - see cap_add in docker-compose.yml) and falls back to
# shelling out to the OS `ping` command when that's unavailable; without this
# package that fallback has nothing to exec and throws
# PlatformNotSupportedException ("The system's ping utility could not be
# found"), which is exactly what took every host's checks down in
# production. Debian's iputils-ping sets CAP_NET_RAW as a *file* capability
# on /bin/ping (setcap, not setuid-root), so the fallback works via that
# binary's own privilege regardless of whether cap_add actually reaches this
# non-root process - keep cap_add too, since it lets the raw-socket path
# succeed directly without needing the subprocess fallback at all.
USER root
RUN mkdir -p /app/data && chown -R $APP_UID /app/data \
    && apt-get update \
    && apt-get install -y --no-install-recommends iputils-ping \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID

# Plain HTTP: this runs inside a trusted internal network, not exposed to
# the internet, so there's no HTTPS certificate to manage.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "NocMonitor.Web.dll"]
