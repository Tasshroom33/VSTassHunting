# CRAFT SPIKE CAPTURE: dedicated server with the 15 dino packs + a REAL client (borrowed
# local session, isolated profile) that arms the engine's slow-tick profiler itself, while
# the server toggles a hammer in and out of the crafting grid. Produces itemized spike
# breakdowns in the ISOLATED client's client-main.log. Fully self-driving.
# REFUSES to run while a Vintage Story client is open (the -c flag would hijack it).
param([switch]$WithFix)
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$cliExe  = "$vs\Vintagestory.exe"
$realProfile = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\bf4462b8-f339-4c37-bbde-8da048fe299b\scratchpad"
$port    = 42494

if (Get-Process -Name "Vintagestory" -ErrorAction SilentlyContinue) {
    Write-Host "REFUSED: a Vintage Story client is running."
    exit 2
}

$dinoZips = @("DinoRuntime_2.0.0.zip","BirdsOfPrey_2.0.0.zip","CarnivorousBull_2.0.0.zip","DomedHead_2.0.0.zip",
  "FusedBody_2.0.0.zip","HornedCrown_2.0.0.zip","HorribleHands_2.0.0.zip","LongNeck_2.0.0.zip","OceanTyrant_2.0.0.zip",
  "PlatedBack_2.0.0.zip","SailedSpine_2.0.0.zip","ScytheClaws_2.0.0.zip","SharpTooth_2.0.0.zip","ShovelMouth_2.0.0.zip","TyrantKing_2.0.0.zip")

$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"

$run   = Join-Path $scratch ("craftspike\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$sdata = Join-Path $run "server"
$cdata = Join-Path $run "client"
New-Item -ItemType Directory -Force "$sdata\Mods","$cdata\Mods" | Out-Null

# Both sides get the dino packs + the harness (its client half arms the profiler).
foreach ($d in @($sdata,$cdata)) {
    New-Item -ItemType Directory -Force "$d\Mods\tasshuntingcompatharness" | Out-Null
    Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$d\Mods\tasshuntingcompatharness\"
    if ($WithFix) {
        Copy-Item -Recurse "J:/Root/Games/VintageStoryCustomMods/TassModFixesTweaks/bin/Release/Mods/mod" "$d/Mods/tassmodfixestweaks"
    }
    foreach ($z in $dinoZips) {
        $src = Join-Path $realProfile "Mods\$z"
        if (Test-Path $src) { Copy-Item $src "$d\Mods\" }
    }
}

# Isolated client profile with the borrowed local session; extended debug ON for per-task attribution.
$csPath = Join-Path $cdata "clientsettings.json"
Copy-Item "$realProfile\clientsettings.json" $csPath
$cs = Get-Content $csPath -Raw | ConvertFrom-Json
$cs.stringListSettings.modPaths = @("Mods", (Join-Path $cdata "Mods"))
if ($cs.stringListSettings.PSObject.Properties.Name -contains "disabledMods") { $cs.stringListSettings.disabledMods = @() }
$cs.boolSettings.extendedDebugInfo = $true
if ($cs.boolSettings.PSObject.Properties.Name -contains "multipleInstances") { $cs.boolSettings.multipleInstances = $true }
else { $cs.boolSettings | Add-Member -NotePropertyName "multipleInstances" -NotePropertyValue $true }
[System.IO.File]::WriteAllText($csPath, ($cs | ConvertTo-Json -Depth 60))

Start-Process $srvExe -ArgumentList @("--dataPath",$sdata,"--genconfig") -Wait -WindowStyle Hidden
$cfg = Get-Content "$sdata\serverconfig.json" -Raw | ConvertFrom-Json
$cfg.VerifyPlayerAuth = $false; $cfg.Port = $port
if ($cfg.PSObject.Properties.Name -contains "AdvertiseServer") { $cfg.AdvertiseServer = $false }
if ($cfg.PSObject.Properties.Name -contains "Upnp") { $cfg.Upnp = $false }
[System.IO.File]::WriteAllText("$sdata\serverconfig.json", ($cfg | ConvertTo-Json -Depth 40))

$slog = "$sdata\Logs\server-main.log"
$clog = "$cdata\Logs\client-main.log"
$env:TASSHUNTING_CRAFTSPIKE = "1"
$sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
$serverUp = $false
for ($i=0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 3
    if ((Test-Path $slog) -and (Select-String -Path $slog -Pattern "Dedicated Server now running" -Quiet)) { $serverUp = $true; break }
}
Write-Host "server-up: $serverUp"
$done = $false
if ($serverUp) {
    $cp = Start-Process $cliExe -ArgumentList @("--dataPath",$cdata,"-c","localhost:$port") -PassThru -WindowStyle Minimized
    for ($i=0; $i -lt 70; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $clog) -and (Select-String -Path $clog -Pattern "CRAFTSPIKE CLIENT DONE" -Quiet)) { $done = $true; break }
    }
    Start-Sleep -Seconds 3
    try { Stop-Process -Id $cp.Id -Force -ErrorAction SilentlyContinue } catch {}
}
$env:TASSHUNTING_CRAFTSPIKE = $null
try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}
Remove-Item $csPath -Force -ErrorAction SilentlyContinue

Write-Host "===== RESULT: server-up=$serverUp driver-done=$done ====="
Write-Host "client log: $clog"
if (Test-Path $clog) {
    Select-String -Path $clog -Pattern "craftspike|A tick took" | ForEach-Object { $_.Line } | Select-Object -First 60
}
