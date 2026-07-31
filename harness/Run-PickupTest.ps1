# Arrow-vacuum end-to-end test. Boots an OFFLINE dedicated server with TassHunting + the harness,
# joins it with a real client (borrowed local session, isolated profile), and the harness then
# drives the real vacuum against the real player inventory:
#   room for part of a stack -> the remainder must SURVIVE on the ground (the 2026-07-30 bug)
#   no room at all           -> item untouched
#   room for all of it       -> item despawns, player holds it all
#   every successful grab    -> the engine's "onitemcollected" event fires
#   a landed arrow           -> same contract through the projectile branch
#
# A REAL CLIENT IS REQUIRED and there is no way around it: the thing under test is
# EntityPlayer.TryGiveItemStack -> PlayerInventoryManager, which does not exist without a connected
# player. Headless cannot reach it.
#
# DEDICATED ONLY: requires no other Vintage Story client running (the game forwards -c to an
# existing instance, hijacking a game you already have open).
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$cliExe  = "$vs\Vintagestory.exe"
$realProfile = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\369c03d3-f82b-49e6-bb96-82398609ee44\scratchpad"
$port    = 42490   # distinct from live 42420, bleed 42486, butcher/owner 42487, factions 42488/42489

if (Get-Process -Name "Vintagestory" -ErrorAction SilentlyContinue) {
    Write-Host "SKIPPED: a Vintage Story client is already running - close it and re-run (dedicated join required)."
    exit 2
}

Write-Host "Building TassHunting + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"
if (-not (Test-Path "$huntOut\TassHunting.dll")) { throw "TassHunting output missing at $huntOut" }

$run   = Join-Path $scratch ("pickuptest\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$sdata = Join-Path $run "server"
$cdata = Join-Path $run "client"
New-Item -ItemType Directory -Force "$sdata\Mods","$cdata\Mods" | Out-Null

Copy-Item $huntOut "$sdata\Mods\tasshunting" -Recurse
New-Item -ItemType Directory -Force "$sdata\Mods\tasshuntingcompatharness" | Out-Null
Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$sdata\Mods\tasshuntingcompatharness\"
Copy-Item $huntOut "$cdata\Mods\tasshunting" -Recurse

$csPath = Join-Path $cdata "clientsettings.json"
Copy-Item "$realProfile\clientsettings.json" $csPath
$cs = Get-Content $csPath -Raw | ConvertFrom-Json
$cs.stringListSettings.modPaths = @("Mods", (Join-Path $cdata "Mods"))
if ($cs.boolSettings.PSObject.Properties.Name -contains "multipleInstances") { $cs.boolSettings.multipleInstances = $true }
else { $cs.boolSettings | Add-Member -NotePropertyName "multipleInstances" -NotePropertyValue $true }
[System.IO.File]::WriteAllText($csPath, ($cs | ConvertTo-Json -Depth 60))

$slog = "$sdata\Logs\server-main.log"

Write-Host "Generating server config..."
Start-Process $srvExe -ArgumentList @("--dataPath",$sdata,"--genconfig") -Wait -WindowStyle Hidden
$cfgPath = "$sdata\serverconfig.json"
if (-not (Test-Path $cfgPath)) { throw "--genconfig produced no config" }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.VerifyPlayerAuth = $false
$cfg.Port = $port
if ($cfg.PSObject.Properties.Name -contains "AdvertiseServer") { $cfg.AdvertiseServer = $false }
if ($cfg.PSObject.Properties.Name -contains "Upnp") { $cfg.Upnp = $false }
[System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 40))

Write-Host "Launching server on port $port ..."
$env:TASSHUNTING_PICKUPTEST = "1"
$sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
$env:TASSHUNTING_PICKUPTEST = $null
$serverUp = $false
for ($i=0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 3
    if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "Dedicated Server now running" -Quiet)) { $serverUp = $true; break }
}
Write-Host "server-up: $serverUp"

$complete = $false
if ($serverUp) {
    Write-Host "Launching client -> localhost:$port ..."
    $cp = Start-Process $cliExe -ArgumentList @("--dataPath",$cdata,"-c","localhost:$port") -PassThru -WindowStyle Minimized
    for ($i=0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        if (Select-String -Path $slog -Pattern "PICKUPTEST COMPLETE" -Quiet) { $complete = $true; break }
    }
    try { Stop-Process -Id $cp.Id -Force -ErrorAction SilentlyContinue } catch {}
}
try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}
Remove-Item $csPath -Force -ErrorAction SilentlyContinue   # borrowed credential out of the temp profile

Write-Host "===== RESULT: server-up=$serverUp complete=$complete ====="
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[pickuptest\]" | ForEach-Object { $_.Line } | Select-Object -First 40
}
if (-not $complete) { Write-Host "PICKUP TEST FAILED TO COMPLETE - inspect $slog" }
