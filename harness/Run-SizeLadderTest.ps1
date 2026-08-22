# The size-ladder bleed rework (owner spec 2026-08-22). Headless dedicated server ONLY -
# no client, nothing opens on the desktop. Proves the lookup and boundaries, the ODDS against
# a real rng over 10k rolls per creature with real spawned wolves and bears, the clock each
# body gets from its own health, the length multiplier, the weapon/claw dial split, that blunt
# still never wounds, and the config migration off the old balance.
$ErrorActionPreference = "Stop"

$root   = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs     = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe = "$vs\VintagestoryServer.exe"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\66ddbc7c-5322-446f-8d07-4f62b9575744\scratchpad"
$port   = 42494   # distinct from live 42420 and every other harness port

Write-Host "Building TassHunting + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"

$run   = Join-Path $scratch ("sizeladder\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
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

Write-Host "Launching headless server on port $port ..."
$env:TASSHUNTING_SIZELADDER = "1"
$sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
$env:TASSHUNTING_SIZELADDER = $null

$complete = $false
for ($i=0; $i -lt 50; $i++) {
    Start-Sleep -Seconds 3
    if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "SIZELADDER COMPLETE" -Quiet)) { $complete = $true; break }
}
try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}

Write-Host "===== RESULT: complete=$complete ====="
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[sizeladder\]|\[Error\]|Exception" | ForEach-Object { $_.Line } | Select-Object -First 40
}
if (-not $complete) { Write-Host "SIZELADDER TEST FAILED TO COMPLETE - inspect $slog" }
