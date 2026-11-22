#!/usr/bin/env bash
# Installs the four services on a real Linux machine with systemd from a dry run's packages, with each product's install.sh --system, and checks that each
# service owns its data (docs/install.md, "A service owns its data"):
#   - install.sh makes one system user per service, named as the program, in the group dbdatakit, and a unit that runs as it; a second run changes nothing;
#   - the install makes nothing under /var/lib/dbdatakit or /var/log/dbdatakit: systemd makes each service's folders at its start;
#   - after the first start every file under each service's data and logs folders is owned by its user, and /var/log/dbdatakit/<program> is mode 0700;
#   - a failed start (the Runner's port held) leaves /var/log/dbdatakit/dbdatarunner/startup-failure-dbdatarunner.txt, and a start after the fix removes it.
# Run by the job suite-install-linux of .github/workflows/suite-release.yml (a dry run), as a user with passwordless sudo, on a machine with the .NET 10
# runtime (ASP.NET Core) in /usr/share/dotnet.
#   dist/tests/suite-systemd-e2e.sh <packages folder> <version>
set -euo pipefail

PACKAGES="$(cd "$1" && pwd)"
VERSION="$2"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${RUNNER_TEMP:-/tmp}/suite-systemd"
PORT=8098
REPO=dbdatakit-test/suite
GROUP=dbdatakit
# program, product folder, data folder under /var/lib/dbdatakit; in start order (SuiteServices)
SERVICES=("dbdatakit DbDataKit server" "dbdatagateway DbDataGateway gateway" "dbdatarunner DbDataRunner runner" "dbdataworkspace DbDataWorkspace workspace")

ok() { echo "ok: $*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }

diagnose() {
  echo '--- units'
  for s in "${SERVICES[@]}"; do set -- $s; sudo systemctl status "$1" --no-pager -l 2>&1 | head -30 || true; sudo journalctl -u "$1" --no-pager -n 60 2>&1 || true; done
  echo '--- folders'
  sudo ls -laR /var/lib/dbdatakit /var/log/dbdatakit 2>&1 | head -200 || true
  for f in /var/log/dbdatakit/*/startup-failure-*.txt; do sudo test -f "$f" && { echo "## $f"; sudo cat "$f"; }; done 2>/dev/null || true
}
trap 'status=$?; [ $status -eq 0 ] || diagnose; [ -n "${SERVER_PID:-}" ] && kill "$SERVER_PID" 2>/dev/null; [ -n "${HOLDER_PID:-}" ] && kill "$HOLDER_PID" 2>/dev/null; exit $status' EXIT

rm -rf "$WORK"; mkdir -p "$WORK"
python3 -I "$ROOT/dist/tests/fake_releases.py" --packages "$PACKAGES" --version "$VERSION" --repo "$REPO" --port "$PORT" &
SERVER_PID=$!
sleep 2

install_all() { # install_all <run name>: each product's output in $WORK/install-<program>-<run name>.txt
  local run="$1"
  for s in "${SERVICES[@]}"; do
    set -- $s
    out="$WORK/$1"
    [ -f "$out/install.sh" ] || python3 -I "$ROOT/dist/dist.py" bind --meta "$ROOT/$2/dist.json" --out "$out" --repo "$REPO" > /dev/null
    sudo bash "$out/install.sh" --system --api-base "http://127.0.0.1:$PORT" --repo "$REPO" --version "$VERSION" 2>&1 | tee "$WORK/install-$1-$run.txt"
  done
}

echo '=== install.sh --system for each service'
install_all first
getent group "$GROUP" > /dev/null || fail "the group $GROUP exists"
ok "the group $GROUP exists"
for s in "${SERVICES[@]}"; do
  set -- $s
  id -u "$1" > /dev/null 2>&1 || fail "the system user $1 exists"
  [ "$(id -gn "$1")" = "$GROUP" ] || fail "$1's group is $GROUP (it is $(id -gn "$1"))"
  [ "$(getent passwd "$1" | cut -d: -f3)" -lt 1000 ] || fail "$1 is a system user"
  unit="/etc/systemd/system/$1.service"
  grep -qx "User=$1" "$unit" || fail "$unit runs as $1"
  grep -qx "Group=$GROUP" "$unit" || fail "$unit runs in the group $GROUP"
  grep -qx "LogsDirectory=dbdatakit/$1" "$unit" || fail "$unit names its logs folder dbdatakit/$1"
  grep -qx "StateDirectory=dbdatakit/$3" "$unit" || fail "$unit names its data folder dbdatakit/$3"
  ok "$1 is a system user in $GROUP and its unit runs as it"
done
for d in /var/lib/dbdatakit /var/log/dbdatakit; do
  [ ! -e "$d" ] || [ -z "$(sudo ls -A "$d")" ] || fail "the install made nothing under $d (found: $(sudo ls -A "$d"))"
done
ok 'the install made nothing under /var/lib/dbdatakit or /var/log/dbdatakit'

echo '=== a second run changes nothing'
before="$(getent passwd dbdatakit dbdatagateway dbdatarunner dbdataworkspace; getent group "$GROUP"; sha256sum /etc/systemd/system/dbdata*.service)"
install_all second
after="$(getent passwd dbdatakit dbdatagateway dbdatarunner dbdataworkspace; getent group "$GROUP"; sha256sum /etc/systemd/system/dbdata*.service)"
[ "$before" = "$after" ] || fail "a second install.sh --system changes no user, group or unit"
for s in "${SERVICES[@]}"; do set -- $s; grep -q "is up to date" "$WORK/install-$1-second.txt" || fail "the second run of $1's install.sh says its unit is up to date"; done
ok 'a second run changes no user, group or unit'

echo '=== each service starts as its own user'
for s in "${SERVICES[@]}"; do
  set -- $s
  sudo systemctl start "$1" || fail "systemctl start $1"
  pid="$(systemctl show -p MainPID --value "$1")"
  [ "$pid" != 0 ] || fail "$1 is running"
  [ "$(ps -o user= -p "$pid" | tr -d ' ')" = "$1" ] || fail "$1 runs as $1 (it runs as $(ps -o user= -p "$pid"))"
  ok "$1 is running as $1"
done

echo '=== every file under each service''s folders is its own'
for s in "${SERVICES[@]}"; do
  set -- $s
  data="/var/lib/dbdatakit/$3"; logs="/var/log/dbdatakit/$1"
  [ "$(sudo stat -c '%U %a' "$logs")" = "$1 700" ] || fail "$logs is owned by $1 with mode 0700 (it is $(sudo stat -c '%U %a' "$logs"))"
  [ "$(sudo stat -c '%U' "$data")" = "$1" ] || fail "$data is owned by $1"
  [ -n "$(sudo find "$data" -mindepth 1 -print -quit)" ] || fail "$1 made its data under $data"
  others="$(sudo find "$data" "$logs" ! -user "$1" -printf '%u %p\n')"
  [ -z "$others" ] || fail "every file under $data and $logs is owned by $1; these are not: $others"
  ok "everything under $data and $logs is owned by $1 ($(sudo find "$data" "$logs" | wc -l) entries)"
done

echo '=== a failed start leaves its reason in the service''s own logs folder'
failure=/var/log/dbdatakit/dbdatarunner/startup-failure-dbdatarunner.txt
listen="$(sudo grep -E '^listen:' /var/lib/dbdatakit/runner/server.yaml || true)"
port="$(printf '%s' "$listen" | grep -oE '[0-9]+/?$' | tr -d '/')"
[ -n "$port" ] || fail "the Runner pinned its port in /var/lib/dbdatakit/runner/server.yaml (found '$listen')"
sudo systemctl stop dbdatarunner
python3 -I -c 'import socket, sys, time
s = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
s.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
s.bind(("::", int(sys.argv[1]))); s.listen(); time.sleep(600)' "$port" &
HOLDER_PID=$!
sleep 1
if sudo systemctl start dbdatarunner; then fail "the Runner does not start while its port $port is held"; fi
sudo test -f "$failure" || fail "$failure was written"
[ "$(sudo stat -c '%U %a' "$failure")" = "dbdatarunner 600" ] || fail "$failure is owned by dbdatarunner with mode 0600 (it is $(sudo stat -c '%U %a' "$failure"))"
sudo grep -q 'failed at the ports step' "$failure" || fail "$failure names the ports step"
ok "the failed start wrote $failure, naming the ports step"
kill "$HOLDER_PID"; wait "$HOLDER_PID" 2>/dev/null || true; HOLDER_PID=
sudo systemctl reset-failed dbdatarunner
sudo systemctl start dbdatarunner || fail "the Runner starts once its port is free"
sudo test ! -e "$failure" || fail "the start after the fix removed $failure"
ok 'the start after the fix removed the failure file'

echo '=== stop and remove'
for s in "${SERVICES[@]}"; do set -- $s; sudo systemctl stop "$1"; done
echo 'Suite install on systemd: OK'
