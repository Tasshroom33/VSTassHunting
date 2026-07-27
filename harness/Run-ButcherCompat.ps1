# Butchering-compat end-to-end test. Boots an OFFLINE dedicated server with TassHunting +
# Butchering + the compat harness, joins it with a real client (borrowed local session, isolated
# profile), and the harness then runs the whole pipeline server-side: spawn pig -> player-kill ->
# corpse must be pristine (no pre-roll flags) -> Butchering empty-hand pickup -> AnimalDrops JSON
# must satisfy the butcher table's input contract. Results: PASS/FAIL lines + a
# "BUTCHERCOMPAT COMPLETE total= pass= fail=" summary in the server log.
#
# DEDICATED ONLY: requires no other Vintage Story client running (the game forwards -c to an
# existing instance - see the TassFactions Run-ClientSmoke.ps1 notes). No SP fallback here, because
# the fresh-world character screen pauses SP and this harness has no dialog-closer.
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$cliExe  = "$vs\Vintagestory.exe"
$realProfile   = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData"
$butcheringZip = "$realProfile\Mods\butchering_1.13.5.zip"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\1e07fe82-7e1f-43bf-adf8-a7bdd1441ef5\scratchpad"
$port    = 42487   # distinct from live 42420, TassFactions smoke 42488, TassFactions harness 42489

if (Get-Process -Name "Vintagestory" -ErrorAction SilentlyContinue) {
    Write-Host "SKIPPED: a Vintage Story client is already running - close it and re-run (dedicated join required)."
    exit 2
}
if (-not (Test-Path $butcheringZip)) { throw "Butchering zip not found at $butcheringZip" }

Write-Host "Building TassHunting + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"   # the machine's env var points at a stale dir
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"
if (-not (Test-Path "$huntOut\TassHunting.dll")) { throw "TassHunting output missing at $huntOut" }

$run   = Join-Path $scratch ("butchercompat\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$sdata = Join-Path $run "server"
$cdata = Join-Path $run "client"
New-Item -ItemType Directory -Force "$sdata\Mods","$cdata\Mods" | Out-Null

# Server mods: tasshunting (folder), butchering (zip), harness (folder).
Copy-Item $huntOut "$sdata\Mods\tasshunting" -Recurse
Copy-Item $butcheringZip "$sdata\Mods\"
New-Item -ItemType Directory -Force "$sdata\Mods\tasshuntingcompatharness" | Out-Null
Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$sdata\Mods\tasshuntingcompatharness\"
# Client mods: tasshunting + butchering only (harness is server-side).
Copy-Item $huntOut "$cdata\Mods\tasshunting" -Recurse
Copy-Item $butcheringZip "$cdata\Mods\"

# Isolated client profile with the borrowed local session; multipleInstances defensively true.
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
$sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
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
        if (Select-String -Path $slog -Pattern "BUTCHERCOMPAT COMPLETE" -Quiet) { $complete = $true; break }
    }
    try { Stop-Process -Id $cp.Id -Force -ErrorAction SilentlyContinue } catch {}
}
try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}
Remove-Item $csPath -Force -ErrorAction SilentlyContinue   # borrowed credential out of the temp profile

Write-Host "===== RESULT: server-up=$serverUp complete=$complete ====="
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[butchercompat\]|\[Error\]|Exception" | ForEach-Object { $_.Line } | Select-Object -First 40
}
if (-not $complete) { Write-Host "COMPAT TEST FAILED TO COMPLETE - inspect $slog" }
