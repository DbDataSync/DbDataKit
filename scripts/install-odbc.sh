#!/usr/bin/env bash
# Installs unixODBC and Microsoft's "ODBC Driver 18 for SQL Server" on Debian/Ubuntu, the pieces the Gateway's SQL Server tests and Kit's ODBC projects need
# (the same steps CI runs). Safe to run again. Works in a Claude Code cloud session: packages.microsoft.com is reachable through the proxy.
#   scripts/install-odbc.sh            installs, then lists the drivers
# Environment: ODBC_SKIP_UPDATE=1 skips `apt-get update`.
set -euo pipefail
if [ "$(id -u)" -ne 0 ]; then SUDO=sudo; else SUDO=; fi
if odbcinst -q -d -n "ODBC Driver 18 for SQL Server" >/dev/null 2>&1; then
  echo "ODBC Driver 18 for SQL Server is already installed."; exit 0
fi
. /etc/os-release
case "$ID" in ubuntu|debian) ;; *) echo "scripts/install-odbc.sh supports Debian and Ubuntu only (this is $ID)." >&2; exit 1 ;; esac
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
curl -sSL -o "$tmp/ms.deb" "https://packages.microsoft.com/config/$ID/$VERSION_ID/packages-microsoft-prod.deb"
$SUDO dpkg -i "$tmp/ms.deb"
[ "${ODBC_SKIP_UPDATE:-}" = 1 ] || $SUDO apt-get update -q
$SUDO env ACCEPT_EULA=Y DEBIAN_FRONTEND=noninteractive apt-get install -y -q msodbcsql18 unixodbc unixodbc-dev
odbcinst -q -d
