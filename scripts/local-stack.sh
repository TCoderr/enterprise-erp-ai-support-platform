#!/usr/bin/env bash
# Kantar local stack for Linux/macOS shells and CI. Same commands and guards as local-stack.ps1.
#
#   ./scripts/local-stack.sh <init|up|seed|down|status|logs|reset> [--project kantar] [--local-dir .local]
#        [--port 8943] [--min-free-mb 1200] [--ai-provider Demo] [--no-build] [--relax-rate-limits]
#        [--confirm-reset <project>]
#
# "down" keeps the database volume. "reset" deletes it and needs --confirm-reset <project>; it only
# removes volumes labelled dev.kantar.stack=true. No password is ever printed.
set -euo pipefail

command="${1:-}"; shift || true
project="kantar"; local_dir=".local"; port="8943"; min_free_mb="1200"; ai_provider="Demo"
no_build="false"; relax="false"; confirm_reset=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --project) project="$2"; shift 2 ;;
    --local-dir) local_dir="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --min-free-mb) min_free_mb="$2"; shift 2 ;;
    --ai-provider) ai_provider="$2"; shift 2 ;;
    --confirm-reset) confirm_reset="$2"; shift 2 ;;
    --no-build) no_build="true"; shift ;;
    --relax-rate-limits) relax="true"; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
case "$project" in kantar|kantar-[a-z0-9]*) ;; *) echo "project name must match ^kantar(-[a-z0-9]+)?$ ; nothing was changed" >&2; exit 2 ;; esac
if ! printf '%s' "$project" | grep -Eq '^kantar(-[a-z0-9]+)?$'; then echo "invalid project name; nothing was changed" >&2; exit 2; fi
if ! printf '%s' "$local_dir" | grep -Eq '^\.local(-[a-z0-9]+)?$'; then echo "local directory must be .local or .local-<suffix>; nothing was changed" >&2; exit 2; fi
local_path="$root/$local_dir"
secrets="$local_path/secrets"
demo_users="satis1 satis2 satis.mudur satinalma1 satinalma.onay depo1 depo.sorumlu depo.izmir destek1 destek.mudur admin"

new_secret() { head -c "${1:-24}" /dev/urandom | base64 | tr '+/' 'AB' | tr -d '=\n'; }
new_password() { printf 'Kt!%saA9' "$(new_secret 18)"; }
# Written once; an existing file is never overwritten.
private_file() { if [ ! -e "$1" ]; then mkdir -p "$(dirname "$1")"; (umask 077; printf '%s' "$2" > "$1"); fi; }
free_mb() { awk '/MemAvailable:/ { printf "%d", $2 / 1024 }' /proc/meminfo 2>/dev/null || echo 999999; }

owned() {
  [ -f "$local_path/manifest.json" ] \
    && grep -q '"product": *"kantar"' "$local_path/manifest.json" \
    && grep -q "\"project\": *\"$project\"" "$local_path/manifest.json"
}
require_owned() { owned || { echo "no initialised local directory for project '$project' at $local_dir; run init first" >&2; exit 1; }; }

compose() {
  KANTAR_LOCAL_DIR="$local_path" KANTAR_HTTPS_PORT="$port" KANTAR_AI_PROVIDER="$ai_provider" \
  KANTAR_RATE_ANONYMOUS="$([ "$relax" = true ] && echo 100000 || echo 30)" \
  KANTAR_RATE_USER="$([ "$relax" = true ] && echo 100000 || echo 600)" \
  KANTAR_RATE_ASSIST="$([ "$relax" = true ] && echo 100000 || echo 6)" \
  docker compose -p "$project" -f "$root/compose.yaml" --project-directory "$root" "$@"
}

case "$command" in
  init)
    if [ -e "$local_path" ] && ! owned; then echo "$local_dir exists but was not created for project '$project'; left untouched" >&2; exit 1; fi
    mkdir -p "$secrets" "$local_path/seed" "$local_path/certs"
    private_file "$local_path/manifest.json" "{ \"product\": \"kantar\", \"project\": \"$project\", \"createdUtc\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\" }"
    private_file "$secrets/sa_password" "$(new_password)"
    private_file "$secrets/migrator_password" "$(new_password)"
    private_file "$secrets/app_password" "$(new_password)"
    sa="$(cat "$secrets/sa_password")"; migrator="$(cat "$secrets/migrator_password")"; app="$(cat "$secrets/app_password")"
    common='Server=sqlserver,1433;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15'
    private_file "$local_path/sqlserver.env" "MSSQL_SA_PASSWORD=$sa
"
    private_file "$secrets/bootstrap_connection" "$common;Database=master;User Id=sa;Password=$sa;Application Name=kantar-bootstrap"
    private_file "$secrets/migrator_connection" "$common;Database=kantar;User Id=kantar_migrator;Password=$migrator;Application Name=kantar-migrator"
    private_file "$secrets/app_connection" "$common;Database=kantar;User Id=kantar_app;Password=$app;Application Name=kantar-app"
    private_file "$secrets/csrf_key" "$(head -c 32 /dev/urandom | base64 | tr -d '\n')"
    private_file "$secrets/https_certificate_password" "$(new_secret 24)"
    for user in $demo_users; do private_file "$local_path/seed/Seed__Passwords__$user" "$(new_password)"; done
    if [ ! -f "$local_path/certs/kantar-dev.pfx" ]; then
      dotnet dev-certs https --export-path "$local_path/certs/kantar-dev.pfx" --password "$(cat "$secrets/https_certificate_password")" --format Pfx > /dev/null
    fi
    # The containers run as a non-root user and must be able to read the mounted files.
    chmod -R a+rX "$local_path"
    echo "Initialised $local_dir for project '$project'. Demo passwords: $local_dir/seed/Seed__Passwords__<user> (not printed)"
    ;;
  up)
    require_owned
    free="$(free_mb)"; echo "Free memory: ${free} MB (required: ${min_free_mb} MB)"
    if [ "$free" -lt "$min_free_mb" ]; then echo "not enough free memory to start the stack safely; nothing was started" >&2; exit 3; fi
    if [ "$no_build" = true ]; then compose up -d --wait; else compose up -d --wait --build; fi
    echo "Kantar is running at https://localhost:$port (self-signed development certificate)"
    ;;
  seed) require_owned; compose --profile seed run --rm seed; echo "Seed finished." ;;
  down) require_owned; compose --profile seed down --remove-orphans; echo "Containers removed. Data volume kept." ;;
  status)
    require_owned; compose ps --all
    for path in /health/live /health/ready; do echo "$path -> HTTP $(curl -k -s -o /dev/null -w '%{http_code}' --max-time 5 "https://localhost:$port$path" || true)"; done
    ;;
  logs) require_owned; compose logs --tail 200 --no-color ;;
  reset)
    if [ "$confirm_reset" != "$project" ]; then echo "destructive: pass --confirm-reset $project ; nothing was deleted" >&2; exit 2; fi
    require_owned
    for volume in $(docker volume ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}'); do
      label="$(docker volume inspect "$volume" --format '{{ index .Labels "dev.kantar.stack" }}')"
      if [ "$label" != "true" ]; then echo "volume '$volume' is not labelled dev.kantar.stack=true; it is not Kantar's. Nothing was deleted." >&2; exit 1; fi
    done
    compose --profile seed down -v --remove-orphans
    rm -rf "$local_path"
    echo "Stack '$project' removed with its volume and $local_dir."
    ;;
  *) echo "usage: local-stack.sh <init|up|seed|down|status|logs|reset> [options]" >&2; exit 2 ;;
esac
