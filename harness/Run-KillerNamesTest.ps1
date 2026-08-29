# KILLER NAMES end-to-end test. Boots ONE offline dedicated server with TassHunting +
# DinoRuntime + TyrantKing + BirdsOfPrey and proves the death-message naming BOTH
# directions in the same boot (the harness flips the switch mid-run): a rex names itself
# "Tyrannosaurus - Tyrant Lizard King" through the engine's own killed-by call, a
# family-only dino gets its family name, a vanilla wolf keeps "a wolf", and with the
# switch off the rex reads "a wild animal" again. Also drives the killing-blow witness
# bookkeeping (consume-once, expiry, which sources count as nameless) through the real
# internals, and asserts the GetDeathMessage patch is attached. Server-only, no client.
#
# Results: PASS/FAIL lines and a "KILLERNAMES COMPLETE total= pass= fail=" summary.
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$realMods = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData\Mods"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\672b375a-aca2-44a8-857a-f9572589a3de\scratchpad"
$port    = 42494   # live 42420, butcher 42487, factions 42488/42489, staywild 42491, biggame 42492, tweaks 42493

# Only the packs the checks need: the dino core, the rex family, and the raptors.
$dinoZips = @("DinoRuntime_2.0.0.zip", "TyrantKing_2.0.0.zip", "BirdsOfPrey_2.0.0.zip")

Write-Host "Building TassHunting + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"
if (-not (Test-Path "$huntOut\TassHunting.dll")) { throw "TassHunting output missing at $huntOut" }

# ONE RUN PER DATAPATH: a fresh world every time, nothing inherited.
$sdata = Join-Path $scratch ("killernames\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Force "$sdata\Mods" | Out-Null

Copy-Item $huntOut "$sdata\Mods\tasshunting" -Recurse
New-Item -ItemType Directory -Force "$sdata\Mods\tasshuntingcompatharness" | Out-Null
Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$sdata\Mods\tasshuntingcompatharness\"
foreach ($z in $dinoZips) {
    $src = Join-Path $realMods $z
    if (Test-Path $src) { Copy-Item $src "$sdata\Mods\" } else { Write-Host "  (missing $z - skipped)" }
}

Write-Host "`n===== RUN: killer names ====="
Start-Process $srvExe -ArgumentList @("--dataPath",$sdata,"--genconfig") -Wait -WindowStyle Hidden
$cfgPath = "$sdata\serverconfig.json"
if (-not (Test-Path $cfgPath)) { throw "--genconfig produced no config" }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.VerifyPlayerAuth = $false
$cfg.Port = $port
if ($cfg.PSObject.Properties.Name -contains "AdvertiseServer") { $cfg.AdvertiseServer = $false }
if ($cfg.PSObject.Properties.Name -contains "Upnp") { $cfg.Upnp = $false }
[System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 40))

$slog = "$sdata\Logs\server-main.log"
$env:TASSHUNTING_KILLERNAMES = "run"
$sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
$env:TASSHUNTING_KILLERNAMES = $null

$complete = $false
for ($i=0; $i -lt 80; $i++) {
    Start-Sleep -Seconds 3
    if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "KILLERNAMES COMPLETE" -Quiet)) { $complete = $true; break }
}
try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}

Write-Host "complete=$complete"
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[killernames\]|killer names|\[Error\]" | ForEach-Object { $_.Line } | Select-Object -First 50
}
if (-not $complete) { Write-Host "KILLERNAMES TEST DID NOT COMPLETE - inspect the logs under $sdata" }
