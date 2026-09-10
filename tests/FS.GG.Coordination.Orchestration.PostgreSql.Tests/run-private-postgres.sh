#!/usr/bin/env bash
set -euo pipefail

test -x /usr/bin/initdb
test -x /usr/bin/pg_ctl
test -x /usr/bin/createdb

o0_pg_root="$(mktemp -d /tmp/fsgg-o0-postgresql-18.6.XXXXXX)"
o0_pg_data="$o0_pg_root/data"
o0_pg_socket="$o0_pg_root/socket"
mkdir "$o0_pg_socket"

cleanup() {
  /usr/bin/pg_ctl -D "$o0_pg_data" stop -m fast >/dev/null 2>&1 || true
}
trap cleanup EXIT

/usr/bin/initdb -D "$o0_pg_data" --auth-local=trust --auth-host=scram-sha-256 --no-locale --encoding=UTF8 >"$o0_pg_root/initdb.log"
/usr/bin/pg_ctl -D "$o0_pg_data" -l "$o0_pg_root/postgres.log" -o "-k $o0_pg_socket -h '' -p 55439 -c fsync=on -c synchronous_commit=on -c full_page_writes=on" start
/usr/bin/createdb -h "$o0_pg_socket" -p 55439 orchestration_o0
printf '%s\n' "$o0_pg_root" > /tmp/o0-postgresql-current-path

/tmp/fsgg-dotnet-10.0.400/dotnet test FS.GG.Coordination.Orchestration.PostgreSql.Tests.fsproj --configuration Release "$@"
