# ARROW LEDGER test: conservation-of-arrows for the 2026-08-30 field report (copper
# arrows vs an armored animal, arrows going missing). Fires real projectiles the way
# ItemBow does at a live pig classified ARMOR (the triceratops stand-in), runs the
# work-loose window and the kill-release, then reconciles every fired arrow id against
# its terminal state. Any arrow that despawned early or vanished without a despawn
# event is dumped with its full event history. Results: PASS/FAIL lines and an
# "ARROWLEDGER COMPLETE total= pass= fail=" summary.
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\ec6d518d-3e3f-4547-a580-971451615f47\scratchpad"
$port    = 42493   # live 42420, butcher 42487, factions 42488/42489, staywild 42491, biggame 42492

Write-Host "Building TassHunting + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"
if (-not (Test-Path "$huntOut\TassHunting.dll")) { throw "TassHunting output missing at $huntOut" }

$sdata = Join-Path $scratch ("arrowledger\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Force "$sdata\Mods","$sdata\ModConfig" | Out-Null
Copy-Item $huntOut "$sdata\Mods\tasshunting" -Recurse
# NEUTRAL BOOT for the type-mutating lists (BigGame pattern) - AI rosters are not under
# test and bake into entity types before the harness can pin anything. Arrow fate dials
# are pinned at runtime by the harness; diagnostics on so every hit's path is in the log.
$neutral = [ordered]@{
    StayWildEnabled = $false
    LeavesPassthroughEnabled = $false
    RetaliationCodes = @()
    TerritorialCodes = @()
    HuntAppend = @{}
    CreatureMeleeDamageMul = @{}
    StepSoundOverride = @{}
    NonPlayerKillsLeaveBones = $false
    BloodDiagnostics = $true
}
[System.IO.File]::WriteAllText("$sdata\ModConfig\TassHunting.json", ($neutral | ConvertTo-Json -Depth 5))
New-Item -ItemType Directory -Force "$sdata\Mods\tasshuntingcompatharness" | Out-Null
Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$sdata\Mods\tasshuntingcompatharness\"

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
$env:TASSHUNTING_ARROWLEDGER = "1"
$sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
$env:TASSHUNTING_ARROWLEDGER = $null

# phases: boot ~30s + volley A 45s + settle 8s + workloose 75s + volley B 19s + settle 8s
# + volley C 23s + final 10s  ~=  4 minutes; allow 8.
$complete = $false
for ($i=0; $i -lt 160; $i++) {
    Start-Sleep -Seconds 3
    if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "ARROWLEDGER COMPLETE" -Quiet)) { $complete = $true; break }
}
try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}

Write-Host "===== RESULT: complete=$complete ====="
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[arrowledger\]|\[Error\]" | ForEach-Object { $_.Line } | Select-Object -First 120
    Write-Host "----- TassHunting diagnostics (bounce/break/stick lines) -----"
    Select-String -Path $slog -Pattern "\[TassHunting\] (arrow hit|.*glanced|bounce)" | ForEach-Object { $_.Line } | Select-Object -First 30
}
if (-not $complete) { Write-Host "ARROWLEDGER TEST DID NOT COMPLETE - inspect $slog" }
Write-Host "log: $slog"
