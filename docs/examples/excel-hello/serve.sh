#!/bin/sh
# Serves this folder over HTTPS on https://localhost:3000 with the development certificate of office-addin-dev-certs (nothing is added to the repository).
# First, once: npx --yes office-addin-dev-certs install   (puts a development CA into the machine's trust store; valid 30 days by default)
set -e
here=$(dirname "$0")
certs="$HOME/.office-addin-dev-certs"
exec npx --yes http-server "$here" -S -C "$certs/localhost.crt" -K "$certs/localhost.key" -p 3000 -c-1
