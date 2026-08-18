# Real-client repro of the earwiq 2026-08-10 report (friend cannot loot corpses).
# The SERVER's ModConfig carries the reported config verbatim (HarvestTimeMult 0.00,
# HarvestAutoDrop false, EmptyCorpseAutoRemove true); the CLIENT profile has NO
# TassHunting.json at all - a fresh install, the friend. One boot, one real client
# join (borrowed local session, isolated profile, -c localhost). The server kills a
# pig as a player kill and marks it harvested; the client right-clicks the corpse the
# way the engine does and the carcass loot window must open, photographed.
#
# -ModZip <path to tasshunting release zip>: run the same test against a packaged
#   release - on a build without the config sync the window stays shut (the report).
param([string]$ModZip = "")
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$cliExe  = "$vs\Vintagestory.exe"
$realProfile = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\66ddbc7c-5322-446f-8d07-4f62b9575744\scratchpad"
$port    = 42492   # distinct from live 42420 and every other harness port

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

$run   = Join-Path $scratch ("harvsync\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$sdata = Join-Path $run "server"
$cdata = Join-Path $run "client"
$shots = Join-Path $run "shots"
New-Item -ItemType Directory -Force $sdata,$cdata,$shots | Out-Null

foreach ($d in @($sdata,$cdata)) {
    New-Item -ItemType Directory -Force "$d\Mods\tasshuntingcompatharness" | Out-Null
    if ($ModZip -ne "") {
        Expand-Archive -Path $ModZip -DestinationPath "$d\Mods\tasshunting" -Force
    } else {
        Copy-Item $huntOut "$d\Mods\tasshunting" -Recurse
    }
    Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$d\Mods\tasshuntingcompatharness\"
}
if ($ModZip -ne "") { Write-Host "MOD UNDER TEST: $ModZip" } else { Write-Host "MOD UNDER TEST: built working tree" }

# THE POINT OF THE TEST: server gets earwiq's config verbatim; the client profile
# gets NO TassHunting.json - a fresh install with pure defaults.
New-Item -ItemType Directory -Force "$sdata\ModConfig" | Out-Null
@'
{
  "HarvestTimeMult": 0.00,
  "HarvestAutoDrop": false,
  "EmptyCorpseAutoRemove": true
}
'@ | Out-File -FilePath "$sdata\ModConfig\TassHunting.json" -Encoding utf8

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

try {
    Write-Host "Launching offline server on port $port ..."
    $env:TASSHUNTING_HARVSYNC = "1"
    $env:TASSHUNTING_HARVSYNC_SHOTS = $shots
    $sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
    $serverUp = $false
    for ($i=0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "Dedicated Server now running" -Quiet)) { $serverUp = $true; break }
    }
    if (-not $serverUp) { throw "server never came up" }

    Write-Host "Launching client (fresh-install profile) -> localhost:$port ..."
    $cp = Start-Process $cliExe -ArgumentList @("--dataPath",$cdata,"-c","localhost:$port") -PassThru
    $complete = $false
    for ($i=0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $clog) -and (Select-String -Path $clog -Pattern "HARVSYNC COMPLETE" -Quiet)) { $complete = $true; break }
    }
    Write-Host "complete: $complete"
    try { Stop-Process -Id $cp.Id -Force -ErrorAction SilentlyContinue } catch {}
    try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}
}
finally {
    $env:TASSHUNTING_HARVSYNC = $null
    $env:TASSHUNTING_HARVSYNC_SHOTS = $null
    Remove-Item $csPath -Force -ErrorAction SilentlyContinue
}

Write-Host "===== RESULTS ($run) ====="
Write-Host "----- CLIENT -----"
if (Test-Path $clog) {
    Select-String -Path $clog -Pattern "\[harvsync\]|lacking mods|\[Error\]" | ForEach-Object { $_.Line }
}
Write-Host "----- SERVER -----"
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[harvsync\]|\[Error\]" | ForEach-Object { $_.Line }
}
Write-Host "shots: $shots"
if (Test-Path $shots) { Get-ChildItem $shots | ForEach-Object { "  " + $_.FullName + "  (" + $_.Length + " bytes)" } }
