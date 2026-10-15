# Windows runner: build s&box from source (Facepunch/sbox-public), open the s&box editor (sbox-dev.exe)
# on a fixture project with the Network Storage library, and drive it with sbox-cli: compile check,
# the library's editor Sync Tool against a local sbox-ns, then play mode with real library calls.
# Then the same library on the dedicated server (sbox-server.exe -mcp). Artifacts go to $env:OUT.
$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$out = $env:OUT; New-Item -ItemType Directory -Force $out | Out-Null
$engineDir = Join-Path $env:RUNNER_TEMP 'sbox-public'
$game = Join-Path $engineDir 'game'
$fixture = Join-Path $env:RUNNER_TEMP 'fixture'
$results = [ordered]@{}
function Step($name) { Write-Host "::group::$name"; Write-Host "$(Get-Date -Format HH:mm:ss) $name" }
function EndStep { Write-Host '::endgroup::' }
function Shot($name) {
	try {
		Add-Type -AssemblyName System.Windows.Forms, System.Drawing
		$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
		$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
		$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
		$bmp.Save((Join-Path $out "$name.png")); $g.Dispose(); $bmp.Dispose()
	} catch { Write-Host "screenshot failed: $_" }
}

# 1. sbox-ns from this checkout.
Step 'Build and start sbox-ns'
dotnet build src/SboxNetworkStorage.Server -c Release -v q -nologo
$ns = Join-Path $root 'src/SboxNetworkStorage.Server/bin/Release/net10.0/sbox-ns.exe'
$nsArgs = @('--config-dir', "$env:RUNNER_TEMP/ns/config", '--data-dir', "$env:RUNNER_TEMP/ns/data")
& $ns setup --non-interactive --database sqlite --listen 127.0.0.1:8080 @nsArgs | Out-Null
$qs = & $ns quickstart 'Editor E2E' --require-sbox-auth false --json @nsArgs | ConvertFrom-Json
$nsProc = Start-Process $ns -ArgumentList (@('start') + $nsArgs) -PassThru -NoNewWindow -RedirectStandardOutput "$out/sbox-ns.log" -RedirectStandardError "$out/sbox-ns.err.log"
for ($i = 0; $i -lt 60; $i++) { try { Invoke-RestMethod http://127.0.0.1:8080/health | Out-Null; break } catch { Start-Sleep 1 } }
Invoke-RestMethod http://127.0.0.1:8080/health
$results.server = (& $ns version)
EndStep

# 2. Engine from source.
Step 'Build s&box from source (Setup.bat)'
if (-not (Test-Path "$game/bin/managed/Sandbox.Engine.dll")) {
	git clone -q --depth 1 https://github.com/Facepunch/sbox-public.git $engineDir
	Push-Location $engineDir; cmd /c Setup.bat; $code = $LASTEXITCODE; Pop-Location
	if ($code -ne 0) { throw "Setup.bat failed ($code)" }
}
$results.engine = (git -C $engineDir log -1 --format='%h %cs %s')
$cli = Join-Path $game 'bin/managed/sbox-cli.exe'
if (-not (Test-Path $cli)) {
	dotnet build "$engineDir/engine/Launcher/SboxCli/Sbox-Cli.csproj" -c Release -v q -nologo
}
Get-ChildItem $game -Filter '*.exe' | ForEach-Object Name
Test-Path $cli
EndStep

# 3. Fixture project with the library (manual install) and its definitions.
Step 'Fixture project'
Copy-Item -Recurse "$root/ci-fixture/compat_probe" $fixture
git clone -q --depth 1 https://github.com/sbox-cool/sbox-network-storage.git "$fixture/Libraries/sboxcool.network-storage"
$results.library = (git -C "$fixture/Libraries/sboxcool.network-storage" log -1 --format='%h %s')
$cfg = @{ projectId = $qs.projectId; publicKey = $qs.publicKey; baseUrl = 'http://127.0.0.1:8080'; cdnUrl = ''; apiVersion = 'v3'; dataFolder = 'Network Storage'; dataSource = 'api_only'; sourceExportMode = 'source_only'; enableAuthSessions = $false; enableEncryptedRequests = $false; publishTarget = 'live'; proxyEnabled = $false }
New-Item -ItemType Directory -Force "$fixture/Editor/Network Storage/config/public", "$fixture/Editor/Network Storage/config/secret" | Out-Null
$cfg | ConvertTo-Json | Set-Content "$fixture/Editor/Network Storage/config/public/projectConfig.json"
@{ secretKey = $qs.secretKey } | ConvertTo-Json | Set-Content "$fixture/Editor/Network Storage/config/secret/secret_key.json"
@{ projectId = $qs.projectId; publicKey = $qs.publicKey; baseUrl = 'http://127.0.0.1:8080'; apiVersion = 'v3' } | ConvertTo-Json | Set-Content "$fixture/Assets/network-storage.credentials.json"
(Get-Content "$root/ci-fixture/runtime-probe.cs" -Raw).Replace('__PROJECT_ID__', $qs.projectId).Replace('__PUBLIC_KEY__', $qs.publicKey) | Set-Content "$out/runtime-probe.cs"
Copy-Item "$root/ci-fixture/editor-sync.cs" "$out/editor-sync.cs"
EndStep

# PowerShell ships `cli` as an alias of Clear-Item, which would shadow this function.
Remove-Item -Force Alias:cli -ErrorAction SilentlyContinue
function Cli([string[]]$a) {
	Push-Location $fixture
	try { $o = & $cli @a 2>&1 | Out-String; $script:cliCode = $LASTEXITCODE; return $o } finally { Pop-Location }
}

# 4. The editor.
function Open-Editor($label) {
	Step "Open the editor ($label)"
	$p = Start-Process "$game/sbox-dev.exe" -ArgumentList @('-project', "`"$fixture/compat_probe.sbproj`"") -WorkingDirectory $game -PassThru
	$found = $false
	for ($i = 0; $i -lt 90; $i++) {
		Start-Sleep 10
		if ($p.HasExited) { Write-Host "sbox-dev exited with $($p.ExitCode)"; break }
		$list = Cli @('--list')
		if ($list -match 'compat_probe') { $found = $true; Write-Host $list; break }
		if ($i % 6 -eq 0) { Shot "editor-$label-$i"; Write-Host "waiting for the editor... ($($i * 10)s)" }
	}
	Shot "editor-$label-final"
	EndStep
	return @{ proc = $p; found = $found }
}
$editor = Open-Editor 'default'
if (-not $editor.found) {
	if (-not $editor.proc.HasExited) { Stop-Process -Id $editor.proc.Id -Force }
	Copy-Item -Recurse -Force "$game/logs" "$out/logs-default" -ErrorAction SilentlyContinue
	Step 'Install Mesa lavapipe (software Vulkan) and retry'
	$rel = Invoke-RestMethod https://api.github.com/repos/pal1000/mesa-dist-win/releases/latest -Headers @{ Authorization = "Bearer $env:GITHUB_TOKEN" }
	$asset = $rel.assets | Where-Object { $_.name -match 'release-msvc\.7z$' } | Select-Object -First 1
	Invoke-WebRequest $asset.browser_download_url -OutFile "$env:RUNNER_TEMP/mesa.7z"
	7z x "$env:RUNNER_TEMP/mesa.7z" "-o$env:RUNNER_TEMP/mesa" -y | Out-Null
	$icd = Get-ChildItem "$env:RUNNER_TEMP/mesa/x64" -Filter 'lvp_icd*.json' | Select-Object -First 1
	$env:VK_DRIVER_FILES = $icd.FullName; $env:VK_ICD_FILENAMES = $icd.FullName
	Write-Host "lavapipe ICD: $($icd.FullName)"
	EndStep
	$editor = Open-Editor 'lavapipe'
}
$results.editorStarted = $editor.found
if (-not $editor.found) {
	Copy-Item -Recurse -Force "$game/logs" "$out/logs" -ErrorAction SilentlyContinue
	$results | ConvertTo-Json -Depth 5 | Set-Content "$out/results.json"
	throw 'The editor never appeared in sbox-cli --list'
}

Step 'Editor: compile status'
for ($i = 0; $i -lt 60; $i++) {
	$st = Cli @('--call', 'editor_status') | ConvertFrom-Json
	if (-not $st.IsCompiling -and -not ((Cli @('--call', 'instance_status')) | ConvertFrom-Json).IsLoading) { break }
	Start-Sleep 5
}
$st | ConvertTo-Json -Depth 4
$results.editorCompile = @{ ok = $st.LastCompileSucceeded; errors = $st.LastCompileErrors; engine = $st.EngineVersion }
Cli @('--call', 'compile_status') | Set-Content "$out/compile_status.json"
Cli @('--call', 'list_toolsets') | Set-Content "$out/toolsets.json"
EndStep

Step 'Editor: library Sync Tool (validate, preflight, push)'
$sync = Cli @('--call', 'run_csharp', (@{ path = "$out/editor-sync.cs" } | ConvertTo-Json -Compress))
Write-Host $sync
$results.editorSync = try { $sync | ConvertFrom-Json } catch { $sync }
EndStep

Step 'Editor: play mode, real library calls'
Cli @('--call', 'play_start') | Write-Host
Start-Sleep 8
$rt = Cli @('--call', 'run_csharp', (@{ path = "$out/runtime-probe.cs" } | ConvertTo-Json -Compress))
Write-Host $rt
$results.editorPlay = try { $rt | ConvertFrom-Json } catch { $rt }
Cli @('--call', 'camera_screenshot') | Write-Host
Cli @('--call', 'read_console', '{"filter":"NetworkStorage","limit":60}') | Set-Content "$out/editor-console.txt"
Cli @('--call', 'play_stop') | Write-Host
Shot 'editor-after-play'
EndStep

Step 'Dedicated server (sbox-server.exe -mcp) with the same project'
Cli @('--call', 'instance_console_command', '{"command":"quit"}') | Out-Null
Start-Sleep 10
$ds = Start-Process "$game/sbox-server.exe" -ArgumentList @('+game', "`"$fixture/compat_probe.sbproj`"", '-mcp') -WorkingDirectory $game -PassThru -RedirectStandardOutput "$out/sbox-server.log" -RedirectStandardError "$out/sbox-server.err.log"
$dsReady = $false
for ($i = 0; $i -lt 60; $i++) {
	Start-Sleep 5
	if ($ds.HasExited) { break }
	$s = Cli @('--connect', "$($ds.Id)", '--call', 'instance_status')
	if ($cliCode -eq 0 -and ($s | ConvertFrom-Json).IsLoading -eq $false) { $dsReady = $true; Write-Host $s; break }
}
$results.dedicatedStarted = $dsReady
if ($dsReady) {
	$d = Cli @('--connect', "$($ds.Id)", '--call', 'run_csharp', (@{ path = "$out/runtime-probe.cs" } | ConvertTo-Json -Compress))
	Write-Host $d
	$results.dedicated = try { $d | ConvertFrom-Json } catch { $d }
}
if (-not $ds.HasExited) { Stop-Process -Id $ds.Id -Force }
EndStep

Step 'Server request log'
'{"projectId":"' + $qs.projectId + '","limit":60}' | & $ns dev logs_requests @nsArgs | Set-Content "$out/requests.json"
EndStep
Copy-Item -Recurse -Force "$game/logs" "$out/logs" -ErrorAction SilentlyContinue
$results | ConvertTo-Json -Depth 6 | Set-Content "$out/results.json"
Get-Content "$out/results.json"
$pass = $results.editorCompile.ok -and $results.editorSync.pass -and $results.editorPlay.pass
"## s&box editor E2E`n``````json`n$(Get-Content "$out/results.json" -Raw)`n``````" | Add-Content $env:GITHUB_STEP_SUMMARY
if (-not $pass) { throw 'editor E2E failed; see results.json' }
