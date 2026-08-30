# ARROW FIELD real-client test (field report 2026-08-30: arrows missing from a tight
# pickup loop). One REAL client joins a disposable local server; the server fires copper
# arrows AS THAT PLAYER at an armored animal (the trike math: 90% bounce), then walks the
# player through the drop field the way the reporter walked to each highlight particle.
# The 4-block vacuum, vanilla walk-over collect, the firedBy own-arrows filter, the UID
# owner lock, and real inventory accounting all run for real. The reconciliation closes
# the loop the headless ledger could not: every PickedUp despawn must show up as actual
# inventory gain; a mismatch is the silent-delete class, printed with the arrow's events.
#
# Companion to Run-ArrowLedger.ps1 (headless conservation: 3 runs / 177 arrows / 0 lost).
# Recipe: borrowed local session, isolated client profile, -c localhost (client-testing
# recipe). YOUR GAME MUST BE CLOSED - the script refuses to run otherwise, because a
# second launch would forward -c over the single-instance pipe and hijack the open game.
#
# Run it, then read the PASS/FAIL lines it prints from both logs. Screenshots of the
# arrow field land in the run folder.
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$cliExe  = "$vs\Vintagestory.exe"
$realProfile = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData"
$port    = 42494   # live 42420, butcher 42487, factions 42488/89, rejoinclient 42490, staywild 42491, biggame 42492, arrowledger 42493

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
if (-not (Test-Path "$huntOut\TassHunting.dll")) { throw "TassHunting output missing at $huntOut" }

$run   = Join-Path $env:TEMP ("tasshunting-arrowfield\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$sdata = Join-Path $run "server"
$cdata = Join-Path $run "client"
$shots = Join-Path $run "shots"
New-Item -ItemType Directory -Force $sdata,$cdata,$shots | Out-Null

# Both sides get both mods.
foreach ($d in @($sdata,$cdata)) {
    New-Item -ItemType Directory -Force "$d\Mods\tasshuntingcompatharness" | Out-Null
    Copy-Item $huntOut "$d\Mods\tasshunting" -Recurse
    Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$d\Mods\tasshuntingcompatharness\"
}

# Isolated client profile with the borrowed cached session.
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

function Wait-LogLine([string]$log, [string]$pattern, [int]$tries) {
    for ($i=0; $i -lt $tries; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern $pattern -Quiet)) { return $true }
    }
    return $false
}

try {
    Write-Host "Boot: offline server on port $port ..."
    $env:TASSHUNTING_ARROWFIELD = "1"
    $env:TASSHUNTING_ARROWFIELD_SHOTS = $shots
    $sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
    if (-not (Wait-LogLine $slog "Dedicated Server now running" 40)) { throw "server never came up" }

    Write-Host "Join: client (isolated profile) -> localhost:$port ..."
    $cp = Start-Process $cliExe -ArgumentList @("--dataPath",$cdata,"-c","localhost:$port") -PassThru
    # join ~1 min + phases ~4 min; allow 10 min total
    $ok = Wait-LogLine $slog "ARROWFIELD SERVER COMPLETE" 200
    Write-Host "server-complete: $ok"
    Start-Sleep -Seconds 5   # let the client take its final shot and log COMPLETE
    try { Stop-Process -Id $cp.Id -Force -ErrorAction SilentlyContinue } catch {}
    try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}
}
finally {
    $env:TASSHUNTING_ARROWFIELD = $null
    $env:TASSHUNTING_ARROWFIELD_SHOTS = $null
    # The borrowed session credential does not stay on disk.
    Remove-Item $csPath -Force -ErrorAction SilentlyContinue
}

Write-Host "===== RESULTS ($run) ====="
Write-Host "----- SERVER -----"
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[arrowfield\]|\[Error\]" | ForEach-Object { $_.Line }
}
Write-Host "----- CLIENT -----"
if (Test-Path $clog) {
    Select-String -Path $clog -Pattern "\[arrowfield\]|lacking mods|\[Error\]" | ForEach-Object { $_.Line }
}
Write-Host "----- TassHunting hit diagnostics -----"
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[TassHunting\] (arrow hit|.*glanced)" | ForEach-Object { $_.Line } | Select-Object -First 30
}
Write-Host "shots: $shots"
if (Test-Path $shots) { Get-ChildItem $shots | ForEach-Object { "  " + $_.FullName + "  (" + $_.Length + " bytes)" } }
