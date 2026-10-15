#!/usr/bin/env bash
# Throwaway: can a GitHub-hosted Linux runner load the Network Storage library in the
# s&box dedicated server (anonymous SteamCMD) and drive it with sbox-cli over -mcp?
set -x
ts() { date -u +%H:%M:%S; }
ROOT=$PWD
FIX=$ROOT/ci-fixture/compat_probe
echo "$(ts) apt"
sudo apt-get update -qq && sudo apt-get install -y -qq lib32gcc-s1 >/dev/null

# 1. sbox-cli from source (no engine dependencies).
git clone -q --filter=blob:none --sparse https://github.com/Facepunch/sbox-public.git sbox-public
(cd sbox-public && git sparse-checkout set engine/Launcher/SboxCli engine/Sandbox.Engine/Systems/Mcp/Transport engine/Launcher/StandaloneTest)
dotnet build -c Release sbox-public/engine/Launcher/SboxCli/Sbox-Cli.csproj -o "$ROOT/cli" -v q -nologo || exit 1
CLI="dotnet $ROOT/cli/sbox-cli.dll"
$CLI --help | head -3

# 2. s&box dedicated server, anonymous (steamcmd is flaky: retry the install).
mkdir -p steamcmd && curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar -xz -C steamcmd
for i in 1 2 3; do echo "$(ts) steamcmd attempt $i"; timeout 1200 ./steamcmd/steamcmd.sh +force_install_dir "$ROOT/sboxserver" +login anonymous +app_update 1892930 validate +quit | tail -3; [ -x "$ROOT/sboxserver/sbox-server" ] && break; echo "steamcmd attempt $i failed, retrying"; sleep 10; done
[ -x "$ROOT/sboxserver/sbox-server" ] || { echo "STEAMCMD FAILED"; ls -la "$ROOT/sboxserver"; exit 1; }
mkdir -p ~/.steam/sdk64 && ln -sf "$ROOT/steamcmd/linux64/steamclient.so" ~/.steam/sdk64/
ls "$ROOT/sboxserver" | head -30
cat "$ROOT/sboxserver/.version" 2>/dev/null

# 3. Library main as a manual install.
git clone -q --depth 1 https://github.com/sbox-cool/sbox-network-storage.git "$FIX/Libraries/sboxcool.network-storage"
git -C "$FIX/Libraries/sboxcool.network-storage" log --oneline -1

# 4. sbox-ns latest release with a development project and a ping endpoint.
curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh | env SBOX_NS_NO_SETUP=1 SBOX_NS_NO_SERVICE=1 sh >/dev/null
export PATH="$HOME/.local/bin:$PATH"
sbox-ns setup --non-interactive --database sqlite --listen 127.0.0.1:8080
sbox-ns quickstart "CI Probe" --require-sbox-auth false --json > qs.json
PROJECT=$(jq -r .projectId qs.json); KEY=$(jq -r .publicKey qs.json)
sbox-ns config set mcp.allow_writes true
printf 'sourceVersion: 1\nkind: endpoint\nid: ping\nmethod: "POST"\nsteps: []\nresponse:\n  status: 200\n  body:\n    ok: true\n    pong: true\n' > ping.yml
jq -n --arg p "$PROJECT" --rawfile s ping.yml '{projectId:$p, kind:"endpoint", source:$s, confirm:($p+"/endpoint/ping"), target:"live"}' | sbox-ns dev definition_save
(sbox-ns start > ns.log 2>&1 &)
for i in $(seq 30); do curl -fsS http://127.0.0.1:8080/health && break; sleep 1; done
curl -fsS http://127.0.0.1:8080/health || { echo "SBOX-NS FAILED TO START"; tail -40 ns.log; exit 1; }

# 5. Dedicated server on the local project, with the MCP pipe.
cd "$ROOT/sboxserver"
(./sbox-server +game "$FIX/compat_probe.sbproj" -mcp > "$ROOT/sbox-server.log" 2>&1 &)
cd "$ROOT"
sleep 30
SPID=$(pgrep -x sbox-server | head -1)
[ -n "$SPID" ] || SPID=$(powershell -NoProfile -Command "(Get-Process sbox-server -ErrorAction SilentlyContinue | Select-Object -First 1).Id" 2>/dev/null | tr -d '\r')
echo "sbox-server process: '$SPID'"
ls -la /tmp/CoreFxPipe_* 2>/dev/null | head
PID=""
for i in $(seq 60); do
  PID=$($CLI --list 2>/dev/null | jq -r '.[0].pid // empty')
  [ -n "$PID" ] && break
  $CLI --connect "$SPID" --call instance_status >/dev/null 2>"$ROOT/connect.err" && PID=$SPID && break
  sleep 5
done
echo "connectable pid: '$PID' (list: $($CLI --list 2>&1 | tr -d '\n'))"
cat "$ROOT/connect.err" 2>/dev/null | tail -3
tail -60 sbox-server.log
[ -n "$PID" ] || exit 1
$CLI --connect "$PID" --call instance_status
$CLI --connect "$PID" --call list_toolsets | jq -r '.toolsets[].name' | tr '\n' ' '; echo
sed -e "s/__PROJECT_ID__/$PROJECT/" -e "s/__PUBLIC_KEY__/$KEY/" ci-fixture/probe.cs > "$ROOT/probe.cs"
for i in $(seq 20); do
  $CLI --connect "$PID" --call instance_status | jq -e '.IsLoading == false' && break; sleep 5
done
$CLI --connect "$PID" --call run_csharp "$(jq -n --arg p "$ROOT/probe.cs" '{path:$p}')"
RC=$?
$CLI --connect "$PID" --call read_console '{"filter":"NetworkStorage","limit":50}'
echo "{\"projectId\":\"$PROJECT\",\"limit\":20}" | sbox-ns dev logs_requests | jq -c '.requests[]? | [.method,.path,.status]' 2>/dev/null || echo "{\"projectId\":\"$PROJECT\",\"limit\":20}" | sbox-ns dev logs_requests
exit $RC
