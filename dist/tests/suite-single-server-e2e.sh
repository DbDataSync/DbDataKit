#!/usr/bin/env bash
# `dbdatakit setup single-server` on a real Linux machine with systemd (docs/install.md, "Setting up one machine, or inviting another"), after
# suite-systemd-e2e.sh installed the four programs with install.sh --system (/opt/<program>/<program>) and left their services stopped:
#   - one run registers (each unit enabled), starts and joins every service: four progress lines reach their last stage, the console lists the three
#     servers in its instances.yaml, and the summary prints the console's address and the first administrator's setup link;
#   - each program folder is root's and nobody else can write to it or to anything in it;
#   - a second run says it has nothing to do;
#   - with the Runner's configured port held by another program, the Runner's line stops at `started`, its startup-failure file is printed and the
#     summary names it; a run after the port is freed finishes it;
#   - a run with --skip-folder-permissions-check puts the switch on each unit's command line;
#   - `setup single-server --stop` stops every service.
# Run by the job suite-install-linux of .github/workflows/suite-release.yml (a dry run), as a user with passwordless sudo. Written without a machine
# with systemd: its first run in that job is its proof.
#   dist/tests/suite-single-server-e2e.sh
set -euo pipefail

WORK="${RUNNER_TEMP:-/tmp}/suite-single-server"
DBDATAKIT=/opt/dbdatakit/dbdatakit
# program, data folder under /var/lib/dbdatakit; in start order (SuiteServices)
SERVICES=("dbdatakit server" "dbdatagateway gateway" "dbdatarunner runner" "dbdataworkspace workspace")
MEMBERS=(dbdatagateway dbdatarunner dbdataworkspace)

ok() { echo "ok: $*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }

diagnose() {
  echo '--- units'
  for s in "${SERVICES[@]}"; do set -- $s; sudo systemctl status "$1" --no-pager -l 2>&1 | head -30 || true; sudo journalctl -u "$1" --no-pager -n 60 2>&1 || true; done
  for f in /var/log/dbdatakit/*/startup-failure-*.txt; do sudo test -f "$f" && { echo "## $f"; sudo cat "$f"; }; done 2>/dev/null || true
  sudo cat /var/lib/dbdatakit/server/instances.yaml 2>/dev/null || true
}
trap 'status=$?; [ $status -eq 0 ] || diagnose; [ -n "${HOLDER_PID:-}" ] && kill "$HOLDER_PID" 2>/dev/null; exit $status' EXIT

rm -rf "$WORK"; mkdir -p "$WORK"

setup() { # setup <run name> [arguments]: the output in $WORK/<run name>.txt; the exit code in $CODE
  local run="$1"; shift
  set +e
  sudo "$DBDATAKIT" setup single-server "$@" > "$WORK/$run.txt" 2>&1
  CODE=$?
  set -e
  cat "$WORK/$run.txt"
}

listen_port() { # listen_port <settings file>: the port of its listen: line
  sudo grep -E '^listen:' "$1" | grep -oE '[0-9]+/?$' | tr -d '/'
}

echo '=== one run registers, starts and joins every service'
setup first
[ "$CODE" = 0 ] || fail "setup single-server exits 0 (it exited $CODE)"
grep -qE '^dbdatakit +registered · started · listening$' "$WORK/first.txt" || fail "the console's line reaches listening"
for m in "${MEMBERS[@]}"; do
  grep -qE "^$m +registered · started · listening · joined$" "$WORK/first.txt" || fail "$m's line reaches joined"
done
grep -qE '^The console is on https://[^ ]+:[0-9]+\.$' "$WORK/first.txt" || fail 'the summary prints the console address'
grep -qE 'https://[^ ]+/invite#[^ ]+' "$WORK/first.txt" || fail "the summary prints the first administrator's setup link"
for s in "${SERVICES[@]}"; do
  set -- $s
  [ "$(systemctl is-enabled "$1")" = enabled ] || fail "$1's unit is enabled"
  [ "$(systemctl is-active "$1")" = active ] || fail "$1 is running"
done
listed="$(sudo grep -cE '^ *- *\{id:' /var/lib/dbdatakit/server/instances.yaml || true)"
[ "$listed" = 3 ] || fail "the console lists the three servers in instances.yaml (it lists $listed)"
for s in runner workspace; do
  port="$(listen_port "/var/lib/dbdatakit/$s/server.yaml")"
  sudo grep -q ":$port" /var/lib/dbdatakit/server/instances.yaml || fail "the console lists the $s at its port $port"
done
for m in "${MEMBERS[@]}"; do sudo test ! -e "/var/lib/dbdatakit/${m#dbdata}/join-request.yaml" || fail "$m consumed its join request"; done
ok 'every service is registered, running and joined, and the console lists the three servers'

echo '=== the program folders are root'"'"'s and nobody else can write to them'
for s in "${SERVICES[@]}"; do
  set -- $s
  folder="/opt/$1"
  [ "$(stat -c '%U' "$folder")" = root ] || fail "$folder is owned by root"
  writable="$(find "$folder" -perm /022 -printf '%m %p\n' | head -5)"
  [ -z "$writable" ] || fail "nothing under $folder is writable by its group or others; these are: $writable"
  others="$(find "$folder" ! -user root -printf '%u %p\n' | head -5)"
  [ -z "$others" ] || fail "everything under $folder is root's; these are not: $others"
done
ok 'each program folder is root'"'"'s with no write for the group or others'

echo '=== a second run has nothing to do'
setup second
[ "$CODE" = 0 ] || fail "the second run exits 0 (it exited $CODE)"
grep -q 'Nothing to do: every service is registered, running and joined.' "$WORK/second.txt" || fail 'the second run says it has nothing to do'
ok 'the second run has nothing to do'

echo '=== a service whose configured port is held'
port="$(listen_port /var/lib/dbdatakit/runner/server.yaml)"
[ -n "$port" ] || fail 'the Runner pinned its port'
sudo systemctl stop dbdatarunner
python3 -I -c 'import socket, sys, time
s = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
s.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
s.bind(("::", int(sys.argv[1]))); s.listen(); time.sleep(900)' "$port" &
HOLDER_PID=$!
sleep 1
setup held
[ "$CODE" = 1 ] || fail "the run with the Runner's port held exits 1 (it exited $CODE)"
grep -qE '^dbdatarunner +registered · started$' "$WORK/held.txt" || fail "the Runner's line stops at started"
grep -q 'startup-failure-dbdatarunner.txt (the last start failed):' "$WORK/held.txt" || fail "the Runner's startup-failure file is printed"
grep -q "$port" "$WORK/held.txt" || fail "the printed file names the port $port"
grep -q 'Not ready: dbdatarunner' "$WORK/held.txt" || fail 'the summary names the Runner as not ready'
grep -qE '^dbdataworkspace +registered · started · listening · joined \(already\)$' "$WORK/held.txt" || fail 'the Workspace is left alone'
ok 'the failed start is printed and named'
kill "$HOLDER_PID"; wait "$HOLDER_PID" 2>/dev/null || true; HOLDER_PID=
sudo systemctl reset-failed dbdatarunner || true
setup fixed
[ "$CODE" = 0 ] || fail "the run after the fix exits 0 (it exited $CODE)"
grep -qE '^dbdatarunner +registered · started · listening · joined' "$WORK/fixed.txt" || fail "the run after the fix finishes the Runner"
ok 'the run after the fix finishes the Runner'

echo '=== --skip-folder-permissions-check is put on each unit'
setup skip --skip-folder-permissions-check
[ "$CODE" = 0 ] || fail "the run with --skip-folder-permissions-check exits 0 (it exited $CODE)"
for s in "${SERVICES[@]}"; do
  set -- $s
  grep -qE '^ExecStart=.* --skip-folder-permissions-check$' "/etc/systemd/system/$1.service" || fail "$1's unit runs with --skip-folder-permissions-check"
  [ "$(systemctl is-active "$1")" = active ] || fail "$1 runs again"
done
ok 'each unit carries the switch'

echo '=== --stop stops every service'
setup stop --stop
[ "$CODE" = 0 ] || fail "setup single-server --stop exits 0 (it exited $CODE)"
for s in "${SERVICES[@]}"; do set -- $s; [ "$(systemctl is-active "$1" || true)" = inactive ] || fail "$1 is stopped"; done
ok 'every service is stopped'
echo 'setup single-server on systemd: OK'
