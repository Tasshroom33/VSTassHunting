# Exit-mid-bleed rejoin test (field report 2026-08-18). Headless dedicated server ONLY - no
# client, nothing opens on the desktop. Boots the SAME world twice:
#   Boot 1 (TASSHUNTING_REJOINTEST=1): wounds a pig through the real damage funnel, proves it
#           is bleeding, then the harness shuts the server down cleanly - the exact thing
#           "exit world" does while you are bleeding.
#   Boot 2 (TASSHUNTING_REJOINTEST=2): same world again. The reloaded pig must carry NO
#           phantom bleed state, a dressing must clear a phantom anyway, the SpawnEntity path
#           (how a rejoining player re-enters) must scrub a poisoned entity, and fresh wounds
#           must still open, tick and dress.
$ErrorActionPreference = "Stop"

$root   = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs     = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe = "$vs\VintagestoryServer.exe"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\66ddbc7c-5322-446f-8d07-4f62b9575744\scratchpad"
$port   = 42489   # distinct from live 42420 and every other harness port

Write-Host "Building TassHunting + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"

$run   = Join-Path $scratch ("rejointest\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$sdata = Join-Path $run "server"
New-Item -ItemType Directory -Force "$sdata\Mods" | Out-Null
Copy-Item $huntOut "$sdata\Mods\tasshunting" -Recurse
New-Item -ItemType Directory -Force "$sdata\Mods\tasshuntingcompatharness" | Out-Null
Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$sdata\Mods\tasshuntingcompatharness\"

$slog = "$sdata\Logs\server-main.log"

Start-Process $srvExe -ArgumentList @("--dataPath",$sdata,"--genconfig") -Wait -WindowStyle Hidden
$cfgPath = "$sdata\serverconfig.json"
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.Port = $port
if ($cfg.PSObject.Properties.Name -contains "AdvertiseServer") { $cfg.AdvertiseServer = $false }
if ($cfg.PSObject.Properties.Name -contains "Upnp") { $cfg.Upnp = $false }
[System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 40))

# ---- Boot 1: wound, prove bleeding, clean self-shutdown mid-bleed ----
Write-Host "Boot 1 (wound + exit mid-bleed) on port $port ..."
$env:TASSHUNTING_REJOINTEST = "1"
$sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
$env:TASSHUNTING_REJOINTEST = $null

# The harness shuts the server down itself; a clean exit IS part of the scenario.
if (-not $sp.WaitForExit(180000)) {
    try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}
    Write-Host "BOOT 1 FAILED: server did not shut itself down - inspect $slog"
    if (Test-Path $slog) { Select-String -Path $slog -Pattern "\[rejointest\]|\[Error\]|Exception" | ForEach-Object { $_.Line } | Select-Object -First 30 }
    exit 1
}
$phase1ok = (Test-Path $slog) -and (Select-String -Path $slog -Pattern "PHASE1 COMPLETE total=\d+ pass=(\d+) fail=0" -Quiet)
Copy-Item $slog "$run\server-main-phase1.log" -ErrorAction SilentlyContinue
Write-Host "===== BOOT 1 (phase1ok=$phase1ok) ====="
if (Test-Path $slog) { Select-String -Path $slog -Pattern "\[rejointest\]" | ForEach-Object { $_.Line } }
if (-not $phase1ok) { Write-Host "BOOT 1 CHECKS FAILED - inspect $run\server-main-phase1.log"; exit 1 }

# ---- Boot 2: same world, verify the rejoin is clean ----
Write-Host "Boot 2 (rejoin) on port $port ..."
$env:TASSHUNTING_REJOINTEST = "2"
$sp2 = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
$env:TASSHUNTING_REJOINTEST = $null

$complete = $false
for ($i=0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 3
    if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "REJOINTEST COMPLETE" -Quiet)) { $complete = $true; break }
}
try { Stop-Process -Id $sp2.Id -Force -ErrorAction SilentlyContinue } catch {}

Write-Host "===== BOOT 2 (complete=$complete) ====="
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[rejointest\]|\[Error\]|Exception" | ForEach-Object { $_.Line } | Select-Object -First 40
}
if (-not $complete) { Write-Host "REJOIN TEST FAILED TO COMPLETE - inspect $slog" }
