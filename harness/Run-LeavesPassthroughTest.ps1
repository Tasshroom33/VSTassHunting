# LEAVES PASSTHROUGH end-to-end test. Boots an OFFLINE dedicated server with TassHunting
# TWICE: once with the switch OFF (branchy leaves must still be solid - the negative control)
# and once ON (every leavesbranchy* block must be open, with normal leaves / logs / soil as
# controls, plus a live restore-and-reopen round trip inside the on-run). Vanilla ships the
# blocks under test, so no dino packs are needed.
#
# Results: PASS/FAIL lines and a "LEAVESPASS COMPLETE total= pass= fail=" summary per run.
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\5cf4eb78-497d-448a-ab23-62687712f8c5\scratchpad"
$port    = 42495   # distinct from live 42420 and every other harness port (42486-42494)

Write-Host "Building TassHunting + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"
if (-not (Test-Path "$huntOut\TassHunting.dll")) { throw "TassHunting output missing at $huntOut" }

$runRoot = Join-Path $scratch ("leavespass\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$results = @{}

foreach ($mode in @("off","on")) {
    # ONE RUN PER DATAPATH: separate world per mode, so neither run can inherit the other's state.
    $sdata = Join-Path $runRoot $mode
    New-Item -ItemType Directory -Force "$sdata\Mods","$sdata\ModConfig" | Out-Null

    Copy-Item $huntOut "$sdata\Mods\tasshunting" -Recurse
    New-Item -ItemType Directory -Force "$sdata\Mods\tasshuntingcompatharness" | Out-Null
    Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$sdata\Mods\tasshuntingcompatharness\"

    # Mod config for this run: only the passthrough switch differs between the two.
    $cfgObj = [ordered]@{
        LeavesPassthroughEnabled = ($mode -eq "on")
    }
    [System.IO.File]::WriteAllText("$sdata\ModConfig\TassHunting.json", ($cfgObj | ConvertTo-Json -Depth 5))

    Write-Host "`n===== RUN: leaves passthrough $mode ====="
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
    $env:TASSHUNTING_LEAVESPASS = $mode
    $sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
    $env:TASSHUNTING_LEAVESPASS = $null

    $complete = $false
    for ($i=0; $i -lt 80; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "LEAVESPASS COMPLETE" -Quiet)) { $complete = $true; break }
    }
    try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}

    Write-Host "complete=$complete"
    if (Test-Path $slog) {
        Select-String -Path $slog -Pattern "\[leavespass\]|leaves passthrough|\[Error\]" | ForEach-Object { $_.Line } | Select-Object -First 40
    }
    $results[$mode] = $complete
}

Write-Host "`n===== RESULT: off-run=$($results['off']) on-run=$($results['on']) ====="
if (-not ($results['off'] -and $results['on'])) { Write-Host "LEAVESPASS TEST DID NOT COMPLETE - inspect the logs under $runRoot" }
