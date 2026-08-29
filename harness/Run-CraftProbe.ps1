# CRAFT PROBE: measures the FindMatchingRecipe ingredient-key walk headlessly, twice -
# once WITH the 15 dino packs, once WITHOUT - on disposable servers. The diff between the
# two runs is the owner's crafting-grid lag, measured instead of theorized.
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$realMods = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData\Mods"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\bf4462b8-f339-4c37-bbde-8da048fe299b\scratchpad"
$port    = 42493

$dinoZips = @("DinoRuntime_2.0.0.zip","BirdsOfPrey_2.0.0.zip","CarnivorousBull_2.0.0.zip","DomedHead_2.0.0.zip",
  "FusedBody_2.0.0.zip","HornedCrown_2.0.0.zip","HorribleHands_2.0.0.zip","LongNeck_2.0.0.zip","OceanTyrant_2.0.0.zip",
  "PlatedBack_2.0.0.zip","SailedSpine_2.0.0.zip","ScytheClaws_2.0.0.zip","SharpTooth_2.0.0.zip","ShovelMouth_2.0.0.zip","TyrantKing_2.0.0.zip")

Write-Host "Building harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }
$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"

$runRoot = Join-Path $scratch ("craftprobe\" + [Guid]::NewGuid().ToString("N").Substring(0,8))

foreach ($mode in @("withdinos","vanilla")) {
    $sdata = Join-Path $runRoot $mode
    New-Item -ItemType Directory -Force "$sdata\Mods" | Out-Null
    Copy-Item $huntOut "$sdata\Mods\tasshunting" -Recurse
    New-Item -ItemType Directory -Force "$sdata\Mods\tasshuntingcompatharness" | Out-Null
    Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$sdata\Mods\tasshuntingcompatharness\"
    if ($mode -eq "withdinos") {
        foreach ($z in $dinoZips) {
            $src = Join-Path $realMods $z
            if (Test-Path $src) { Copy-Item $src "$sdata\Mods\" } else { Write-Host "  (missing $z)" }
        }
    }
    Start-Process $srvExe -ArgumentList @("--dataPath",$sdata,"--genconfig") -Wait -WindowStyle Hidden
    $cfg = Get-Content "$sdata\serverconfig.json" -Raw | ConvertFrom-Json
    $cfg.VerifyPlayerAuth = $false; $cfg.Port = $port
    if ($cfg.PSObject.Properties.Name -contains "AdvertiseServer") { $cfg.AdvertiseServer = $false }
    if ($cfg.PSObject.Properties.Name -contains "Upnp") { $cfg.Upnp = $false }
    [System.IO.File]::WriteAllText("$sdata\serverconfig.json", ($cfg | ConvertTo-Json -Depth 40))

    $slog = "$sdata\Logs\server-main.log"
    Write-Host "===== RUN: $mode ====="
    $env:TASSHUNTING_CRAFTPROBE = "1"
    $sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
    $env:TASSHUNTING_CRAFTPROBE = $null
    $complete = $false
    for ($i=0; $i -lt 80; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "CRAFTPROBE COMPLETE" -Quiet)) { $complete = $true; break }
    }
    try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}
    Write-Host "complete=$complete"
    if (Test-Path $slog) { Select-String -Path $slog -Pattern "\[craftprobe\]" | ForEach-Object { $_.Line } }
}
