#!/usr/bin/env bash
# Real macOS install with the launchd service (CI only).
#
#   sudo scripts/ci/install-matrix-macos.sh --fixture DIR --candidate A --next B
#
# DIR comes from scripts/ci/build-release-fixture.sh (--rid osx-arm64). Trusts its CA in the System
# keychain, points github.com, api.github.com and sboxcool.com at a local fixture server, then runs the
# real installer as root (LaunchDaemon): fresh install, re-run with the next release (the service must
# restart on the new binary), and uninstall. It changes /etc/hosts and the System keychain: never run it
# outside a throwaway CI machine.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fixture=''; candidate=''; next=''
while (($#)); do
  case "$1" in
    --fixture) fixture="$2"; shift 2 ;;
    --candidate) candidate="$2"; shift 2 ;;
    --next) next="$2"; shift 2 ;;
    *) sed -n '2,10p' "$0" >&2; exit 2 ;;
  esac
done
[[ "$(id -u)" == 0 && -d "$fixture" && -n "$candidate" && -n "$next" ]] || { sed -n '2,10p' "$0" >&2; exit 2; }

plist=/Library/LaunchDaemons/cool.sbox.sbox-ns.plist
log=/opt/sbox-ns/data/logs/sbox-ns.log
step() { printf '\n== %s\n' "$*"; }
fail() {
  echo "FAIL: $*" >&2
  launchctl print system/cool.sbox.sbox-ns >&2 2>/dev/null || true
  tail -n 80 "$log" >&2 2>/dev/null || true
  exit 1
}
health() {
  for _ in $(seq 60); do curl -fsS http://127.0.0.1:8080/health > /dev/null 2>&1 && return 0; sleep 1; done
  fail "no health response"
}
expect_version() {
  local actual
  actual="$(curl -fsS http://127.0.0.1:8080/v3/server-info | python3 -c 'import json,sys; print(json.load(sys.stdin)["version"])')"
  [[ "$actual" == "$1" ]] || fail "expected running version $1, got $actual"
  echo "running $actual"
}
install_release() {
  curl -fsSL "https://github.com/sbox-cool/sbox-network-storage-server/releases/download/v$1/install.sh" \
    | env SBOX_NS_VERSION="$1" SBOX_NS_PROJECT="Install Matrix" sh
}

step "Trust the fixture and route release hosts to it"
security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$fixture/tls/ca.crt"
cp /etc/hosts /etc/hosts.install-matrix
printf '127.0.0.1 github.com api.github.com sboxcool.com\n' >> /etc/hosts
dscacheutil -flushcache || true
python3 "$here/release_fixture.py" serve --root "$fixture" --cert "$fixture/tls/server.crt" --key "$fixture/tls/server.key" \
  > /tmp/release-fixture.log 2>&1 &
fixture_pid=$!
trap 'kill $fixture_pid 2>/dev/null || true; cp /etc/hosts.install-matrix /etc/hosts' EXIT
for _ in $(seq 30); do curl -fsS "https://github.com/sbox-cool/sbox-network-storage-server/releases/download/v$candidate/SHA256SUMS" > /dev/null 2>&1 && break; sleep 0.5; done
curl -fsS "https://sboxcool.com/api/network-storage/releases/latest?channel=stable" > /dev/null || fail "fixture is not reachable over trusted HTTPS"

step "Fresh install of v$candidate as root (LaunchDaemon)"
install_release "$candidate"
[[ -f "$plist" ]] || fail "LaunchDaemon plist was not installed"
launchctl print system/cool.sbox.sbox-ns > /dev/null || fail "LaunchDaemon is not loaded"
health
expect_version "$candidate"
curl -fsS http://127.0.0.1:8080/owner-assets/icon.svg | grep -q '#70e000' || fail "owner console brand icon missing"
/usr/local/bin/sbox-ns project list --config-dir /opt/sbox-ns/config --data-dir /opt/sbox-ns/data | grep -q 'Install Matrix' \
  || fail "project 'Install Matrix' is missing"

step "Re-run the installer with v$next (service must restart on the new binary)"
install_release "$next"
health
expect_version "$next"

step "Re-run the same release again (idempotent)"
install_release "$next"
health
expect_version "$next"

step "Uninstall the service"
/usr/local/bin/sbox-ns service uninstall --config-dir /opt/sbox-ns/config --data-dir /opt/sbox-ns/data
[[ ! -f "$plist" ]] || fail "plist still present after uninstall"
for _ in $(seq 20); do curl -fsS http://127.0.0.1:8080/health > /dev/null 2>&1 || break; sleep 1; done
if curl -fsS http://127.0.0.1:8080/health > /dev/null 2>&1; then fail "server still answers after uninstall"; fi
echo "macOS install matrix passed"
