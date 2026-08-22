# Real-client test for the bleeding box. Launches an OFFLINE dedicated server (VerifyPlayerAuth
# =false) running TassHunting + the harness, then a real client in an ISOLATED profile that joins
# via -c localhost. The server harness wounds the joined player and later dresses the wound; the
# client harness asserts what the player sees and SAVES PNG SCREENSHOTS of the real framebuffer.
#
# How the usual blockers are beaten (see the client-testing recipe):
#  - login prompt: clientsettings.json is copied from the real profile for its locally-valid
#    cached session, and DELETED again at the end.
#  - mod mismatch: the isolated client profile carries exactly the server's two mods.
#  - whitelist: localhost connections bypass it.
#  - single-instance hijack: a running client would swallow our -c and yank the user's own game
#    onto this server, so we refuse to run when one is open.
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$cliExe  = "$vs\Vintagestory.exe"
$realProfile = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\66ddbc7c-5322-446f-8d07-4f62b9575744\scratchpad"
$port    = 42489   # distinct from live 42420 and every other harness port

if (Get-Process -Name "Vintagestory" -ErrorAction SilentlyContinue) {
    Write-Host "REFUSING TO RUN: a Vintage Story client is open. Launching with -c would forward the"
    Write-Host "connect over the single-instance pipe and drag that game onto this test server."
    exit 2
}

Write-Host "Building mod + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"

$run   = Join-Path $scratch ("hudsmoke\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$sdata = Join-Path $run "server"
$cdata = Join-Path $run "client"
$shots = Join-Path $run "shots"
New-Item -ItemType Directory -Force $sdata,$cdata,$shots | Out-Null

# Both sides get both mods: the harness is Universal now (it has a client half).
foreach ($d in @($sdata,$cdata)) {
    New-Item -ItemType Directory -Force "$d\Mods\tasshuntingcompatharness" | Out-Null
    Copy-Item $huntOut "$d\Mods\tasshunting" -Recurse
    Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$d\Mods\tasshuntingcompatharness\"
}

# Isolated client profile: borrow the cached session, then point modPaths at THIS profile only so
# the client's mod set matches the server's.
$csPath = Join-Path $cdata "clientsettings.json"
Copy-Item "$realProfile\clientsettings.json" $csPath
$cs = Get-Content $csPath -Raw | ConvertFrom-Json
$cs.stringListSettings.modPaths = @("Mods", (Join-Path $cdata "Mods"))
if ($cs.boolSettings.PSObject.Properties.Name -contains "multipleInstances") { $cs.boolSettings.multipleInstances = $true }
else { $cs.boolSettings | Add-Member -NotePropertyName "multipleInstances" -NotePropertyValue $true }
[System.IO.File]::WriteAllText($csPath, ($cs | ConvertTo-Json -Depth 60))

$slog = "$sdata\Logs\server-main.log"
$clog = "$cdata\Logs\client-main.log"
$cfgPath = "$sdata\serverconfig.json"

Write-Host "Generating server config..."
Start-Process $srvExe -ArgumentList @("--dataPath",$sdata,"--genconfig") -Wait -WindowStyle Hidden
if (-not (Test-Path $cfgPath)) { throw "--genconfig did not produce a config" }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.VerifyPlayerAuth = $false
$cfg.Port = $port
if ($cfg.PSObject.Properties.Name -contains "AdvertiseServer") { $cfg.AdvertiseServer = $false }
if ($cfg.PSObject.Properties.Name -contains "Upnp") { $cfg.Upnp = $false }
[System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 40))

Write-Host "Launching offline server on port $port ..."
$env:TASSHUNTING_HUDTEST = "1"
$sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
$serverUp = $false
for ($i=0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 3
    if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "Dedicated Server now running" -Quiet)) { $serverUp = $true; break }
}
Write-Host "server-up: $serverUp"

$hudComplete = $false
if ($serverUp) {
    Write-Host "Launching client (isolated profile) -> localhost:$port ..."
    # No embedded quotes in ArgumentList elements - the client takes them literally and silently
    # falls back to the DEFAULT profile, which invalidates the whole run.
    $env:TASSHUNTING_HUDTEST_SHOTS = $shots
    $cp = Start-Process $cliExe -ArgumentList @("--dataPath",$cdata,"-c","localhost:$port") -PassThru
    for ($i=0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $clog) -and (Select-String -Path $clog -Pattern "HUDTEST COMPLETE" -Quiet)) { $hudComplete = $true; break }
    }
    try { Stop-Process -Id $cp.Id -Force -ErrorAction SilentlyContinue } catch {}
}
$env:TASSHUNTING_HUDTEST = $null
$env:TASSHUNTING_HUDTEST_SHOTS = $null
try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}

# The borrowed session credential does not stay on disk.
Remove-Item $csPath -Force -ErrorAction SilentlyContinue

Write-Host "===== RESULT: server-up=$serverUp hudtest-complete=$hudComplete ====="
Write-Host "shots: $shots"
if (Test-Path $shots) { Get-ChildItem $shots | ForEach-Object { "  " + $_.FullName + "  (" + $_.Length + " bytes)" } }
Write-Host "----- CLIENT log -----"
if (Test-Path $clog) {
    Select-String -Path $clog -Pattern "\[hudtest\]|lacking mods|\[Error\]|Exception" | ForEach-Object { $_.Line } | Select-Object -First 45
} else { "no client log - --dataPath did not take, run is invalid" }
Write-Host "----- SERVER log -----"
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[hudtest\]|joins\.|now playing|\[Error\]|Exception|kick" | ForEach-Object { $_.Line } | Select-Object -First 25
} else { "no server log" }
