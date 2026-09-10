#!/usr/bin/env bash
set -euo pipefail

o0_mode="${FSGG_PG_MODE:-binary}"
o0_dotnet="${DOTNET_EXE:-/tmp/fsgg-dotnet-10.0.400/dotnet}"
o0_port="${FSGG_PG_PORT:-55439}"
o0_user="${FSGG_PG_USERNAME:-developer}"

if [[ "$o0_mode" == docker ]]; then
  command -v docker >/dev/null
  o0_container="${FSGG_PG_CONTAINER:-fsgg-o0-pg18-${RANDOM}-$$}"
  o0_image="postgres:18.6@sha256:4ef4dbc939d61acea57712655ddb4b4ab27419c913f94cca0cd57cb3ea3c2280"
  cleanup() {
    o0_status=$?
    if (( o0_status != 0 )); then
      docker inspect --format 'container={{.Name}} status={{.State.Status}} exit={{.State.ExitCode}} error={{.State.Error}}' "$o0_container" >&2 || true
      docker logs "$o0_container" >&2 || true
    fi
    docker rm -f "$o0_container" >/dev/null 2>&1 || true
    return "$o0_status"
  }
  trap cleanup EXIT
  docker run -d --name "$o0_container" \
    -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_USER="$o0_user" \
    -e POSTGRES_DB=orchestration_o0 -p "127.0.0.1:${o0_port}:5432" \
    "$o0_image" -c fsync=on -c synchronous_commit=on -c full_page_writes=on >/dev/null
  o0_ready=false
  o0_deadline=$((SECONDS + 90))
  while (( SECONDS < o0_deadline )); do
    if [[ "$(docker inspect --format '{{.State.Running}}' "$o0_container" 2>/dev/null || true)" != true ]]; then
      echo "PostgreSQL container exited before readiness" >&2
      docker inspect --format 'container={{.Name}} status={{.State.Status}} exit={{.State.ExitCode}} error={{.State.Error}}' "$o0_container" >&2 || true
      docker logs "$o0_container" >&2 || true
      exit 1
    fi
    if docker exec "$o0_container" pg_isready -h 127.0.0.1 -p 5432 -U "$o0_user" -d orchestration_o0 >/dev/null 2>&1; then
      o0_ready=true
      break
    fi
    sleep 1
  done
  if [[ "$o0_ready" != true ]]; then
    echo "PostgreSQL container was not ready within 90 seconds" >&2
    docker inspect --format 'container={{.Name}} status={{.State.Status}} exit={{.State.ExitCode}} error={{.State.Error}}' "$o0_container" >&2 || true
    docker logs "$o0_container" >&2 || true
    exit 1
  fi
  docker exec "$o0_container" pg_isready -h 127.0.0.1 -p 5432 -U "$o0_user" -d orchestration_o0 >/dev/null
  export FSGG_PG_MODE=docker FSGG_PG_HOST=127.0.0.1 FSGG_PG_PORT="$o0_port"
  export FSGG_PG_USERNAME="$o0_user" FSGG_PG_CONTAINER="$o0_container" FSGG_PG_ROOT=/tmp
else
  test -x /usr/bin/initdb
  test -x /usr/bin/pg_ctl
  test -x /usr/bin/createdb
  o0_pg_root="$(mktemp -d /tmp/fsgg-o0-postgresql-18.6.XXXXXX)"
  o0_pg_data="$o0_pg_root/data"
  o0_pg_socket="$o0_pg_root/socket"
  mkdir "$o0_pg_socket"
  cleanup() { /usr/bin/pg_ctl -D "$o0_pg_data" stop -m fast >/dev/null 2>&1 || true; }
  trap cleanup EXIT
  /usr/bin/initdb -D "$o0_pg_data" --auth-local=trust --auth-host=scram-sha-256 --no-locale --encoding=UTF8 >"$o0_pg_root/initdb.log"
  /usr/bin/pg_ctl -D "$o0_pg_data" -l "$o0_pg_root/postgres.log" -o "-k $o0_pg_socket -h '' -p $o0_port -c fsync=on -c synchronous_commit=on -c full_page_writes=on" start
  /usr/bin/createdb -h "$o0_pg_socket" -p "$o0_port" orchestration_o0
  printf '%s\n' "$o0_pg_root" > /tmp/o0-postgresql-current-path
  export FSGG_PG_MODE=binary FSGG_PG_ROOT="$o0_pg_root" FSGG_PG_PORT="$o0_port" FSGG_PG_USERNAME="$o0_user"
fi

"$o0_dotnet" test FS.GG.Coordination.Orchestration.PostgreSql.Tests.fsproj --configuration Release "$@"
