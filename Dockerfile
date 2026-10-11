# syntax=docker/dockerfile:1

# ---- web: reproducible UI build from the lockfile ----
FROM node:24.18.0-bookworm-slim AS web
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/web/ ./
RUN npm run build

# ---- build: locked restore, Release publish ----
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /repo
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Kantar.Domain/Kantar.Domain.csproj src/Kantar.Domain/packages.lock.json src/Kantar.Domain/
COPY src/Kantar.Application/Kantar.Application.csproj src/Kantar.Application/packages.lock.json src/Kantar.Application/
COPY src/Kantar.Infrastructure/Kantar.Infrastructure.csproj src/Kantar.Infrastructure/packages.lock.json src/Kantar.Infrastructure/
COPY src/Kantar.Api/Kantar.Api.csproj src/Kantar.Api/packages.lock.json src/Kantar.Api/
RUN dotnet restore src/Kantar.Api/Kantar.Api.csproj --locked-mode
COPY db/ db/
COPY src/Kantar.Domain/ src/Kantar.Domain/
COPY src/Kantar.Application/ src/Kantar.Application/
COPY src/Kantar.Infrastructure/ src/Kantar.Infrastructure/
COPY src/Kantar.Api/ src/Kantar.Api/
RUN dotnet publish src/Kantar.Api/Kantar.Api.csproj -c Release -o /out --no-restore -p:UseAppHost=false

# ---- runtime: non-root, one image for web / worker / one-shot modes ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
LABEL org.opencontainers.image.title="Kantar" \
      org.opencontainers.image.authors="TCoderr" \
      org.opencontainers.image.description="Order, purchasing, stock and support centre for a wholesale business (local development build)"
WORKDIR /app
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=7
COPY --from=build /out/ ./
COPY --from=web /web/dist/ ./wwwroot/
USER $APP_UID
ENTRYPOINT ["dotnet", "Kantar.Api.dll"]
