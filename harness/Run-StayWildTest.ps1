# STAY WILD end-to-end test. Boots an OFFLINE dedicated server with TassHunting + the 14 dino
# packs + DinoRuntime, TWICE over the same mod set: once with the stay-wild switch OFF (every
# domestication behavior must still be there - the negative control) and once ON (they must all
# be gone, while vanilla's elk keeps its own). Server-only, no client needed.
#
# Results: PASS/FAIL lines and a "STAYWILD COMPLETE total= pass= fail=" summary per run.
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$realMods = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData\Mods"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\bf4462b8-f339-4c37-bbde-8da048fe299b\scratchpad"
$port    = 42491   # distinct from live 42420 and the other harness ports (42487-42489)

# The dino packs under test. Absent packs are skipped, not fatal - the harness reports how many
# families it actually found.
$dinoZips = @("DinoRuntime_2.0.0.zip") + (Get-ChildItem $realMods -Filter "*_2.0.0.zip" |
             Where-Object { $_.Name -ne "DinoRuntime_2.0.0.zip" } | Select-Object -ExpandProperty Name)

# What the config points stay-wild at: the 14 family domains, by wildcard.
$stayWildCodes = @(
  "tyrannosauridae-*","carcharodontosauridae-*","abelisauridae-*","spinosauridae-*",
  "dromaeosauridae-*","mosasauridae-*","macronaria-*","stegosauria-*","ankylosauria-*",
  "ceratopsidae-*","pachycephalosauria-*","hadrosauroidea-*","ornithomimosauria-*",
  "therizinosauridae-*"
)

Write-Host "Building TassHunting + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"
if (-not (Test-Path "$huntOut\TassHunting.dll")) { throw "TassHunting output missing at $huntOut" }

$runRoot = Join-Path $scratch ("staywild\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$results = @{}

foreach ($mode in @("off","on")) {
    # ONE RUN PER DATAPATH: separate world per mode, so neither run can inherit the other's state.
    $sdata = Join-Path $runRoot $mode
    New-Item -ItemType Directory -Force "$sdata\Mods","$sdata\ModConfig" | Out-Null

    Copy-Item $huntOut "$sdata\Mods\tasshunting" -Recurse
    New-Item -ItemType Directory -Force "$sdata\Mods\tasshuntingcompatharness" | Out-Null
    Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$sdata\Mods\tasshuntingcompatharness\"
    foreach ($z in $dinoZips) {
        $src = Join-Path $realMods $z
        if (Test-Path $src) { Copy-Item $src "$sdata\Mods\" } else { Write-Host "  (missing $z - skipped)" }
    }

    # Mod config for this run: only the stay-wild switch differs between the two.
    $cfgObj = [ordered]@{
        StayWildEnabled = ($mode -eq "on")
        StayWildCodes   = $stayWildCodes
    }
    [System.IO.File]::WriteAllText("$sdata\ModConfig\TassHunting.json", ($cfgObj | ConvertTo-Json -Depth 5))

    Write-Host "`n===== RUN: stay-wild $mode ====="
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
    $env:TASSHUNTING_STAYWILD = $mode
    $sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
    $env:TASSHUNTING_STAYWILD = $null

    $complete = $false
    for ($i=0; $i -lt 80; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "STAYWILD COMPLETE" -Quiet)) { $complete = $true; break }
    }
    try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}

    Write-Host "complete=$complete"
    if (Test-Path $slog) {
        Select-String -Path $slog -Pattern "\[staywild\]|stay-wild|\[Error\]" | ForEach-Object { $_.Line } | Select-Object -First 40
    }
    $results[$mode] = $complete
}

Write-Host "`n===== RESULT: off-run=$($results['off']) on-run=$($results['on']) ====="
if (-not ($results['off'] -and $results['on'])) { Write-Host "STAYWILD TEST DID NOT COMPLETE - inspect the logs under $runRoot" }
