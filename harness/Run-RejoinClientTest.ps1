# Real-client exit-mid-bleed rejoin test (field report Sanches31 2026-08-18). Same world,
# same player account, two joins with a REAL client (borrowed local session, isolated
# profile, -c localhost - see the client-testing recipe):
#   Join 1: server wounds the player, client photographs the bleeding box, then the script
#           kills the client mid-bleed (the "exit world") and the server shuts down.
#   Join 2: the client must see NO phantom bleed box, and a fresh wound + dressing cycle
#           must still work on screen - the two things the report says break.
#
# -ModZip <path to tasshunting release zip>: deploy that zip instead of the built output,
#   for proving the OLD build shows the symptom (red) before the new one clears it (green).
param([string]$ModZip = "")
$ErrorActionPreference = "Stop"

$root    = "J:\Root\Games\VintageStoryCustomMods\TassHunting"
$vs      = "C:\Users\8byteTass\AppData\Roaming\Vintagestory1.22.5"
$srvExe  = "$vs\VintagestoryServer.exe"
$cliExe  = "$vs\Vintagestory.exe"
$realProfile = "C:\Users\8byteTass\AppData\Roaming\VintagestoryData"
$scratch = "C:\Users\8BYTET~1\AppData\Local\Temp\claude\j--Root-Games-VintageStoryCustomMods\66ddbc7c-5322-446f-8d07-4f62b9575744\scratchpad"
$port    = 42490   # distinct from live 42420 and every other harness port

if (Get-Process -Name "Vintagestory" -ErrorAction SilentlyContinue) {
    Write-Host "REFUSING TO RUN: a Vintage Story client is open. Launching with -c would forward the"
    Write-Host "connect over the single-instance pipe and drag that game onto this test server."
    exit 2
}

Write-Host "Building mod + harness..."
$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "TassHunting build failed" }
dotnet build "$root\harness\TassHuntingCompatHarness\TassHuntingCompatHarness.csproj" -c Release -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }

$huntOut    = "$root\TassHunting\bin\Release\Mods\mod"
$harnessOut = "$root\harness\TassHuntingCompatHarness\bin\Release"

$run   = Join-Path $scratch ("rejoinclient\" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$sdata = Join-Path $run "server"
$cdata = Join-Path $run "client"
$shots = Join-Path $run "shots"
New-Item -ItemType Directory -Force $sdata,$cdata,$shots | Out-Null

# Both sides get both mods. With -ModZip the tasshunting under test comes from that zip.
foreach ($d in @($sdata,$cdata)) {
    New-Item -ItemType Directory -Force "$d\Mods\tasshuntingcompatharness" | Out-Null
    if ($ModZip -ne "") {
        Expand-Archive -Path $ModZip -DestinationPath "$d\Mods\tasshunting" -Force
    } else {
        Copy-Item $huntOut "$d\Mods\tasshunting" -Recurse
    }
    Copy-Item "$harnessOut\TassHuntingCompatHarness.dll","$harnessOut\modinfo.json" "$d\Mods\tasshuntingcompatharness\"
}
if ($ModZip -ne "") { Write-Host "MOD UNDER TEST: $ModZip" } else { Write-Host "MOD UNDER TEST: built working tree" }

# Isolated client profile with the borrowed cached session.
$csPath = Join-Path $cdata "clientsettings.json"
Copy-Item "$realProfile\clientsettings.json" $csPath
$cs = Get-Content $csPath -Raw | ConvertFrom-Json
$cs.stringListSettings.modPaths = @("Mods", (Join-Path $cdata "Mods"))
if ($cs.boolSettings.PSObject.Properties.Name -contains "multipleInstances") { $cs.boolSettings.multipleInstances = $true }
else { $cs.boolSettings | Add-Member -NotePropertyName "multipleInstances" -NotePropertyValue $true }
[System.IO.File]::WriteAllText($csPath, ($cs | ConvertTo-Json -Depth 60))

$slog = "$sdata\Logs\server-main.log"
$clog = "$cdata\Logs\client-main.log"
$cfgPath = "$sdata\serverconfig.json"

Write-Host "Generating server config..."
Start-Process $srvExe -ArgumentList @("--dataPath",$sdata,"--genconfig") -Wait -WindowStyle Hidden
if (-not (Test-Path $cfgPath)) { throw "--genconfig did not produce a config" }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.VerifyPlayerAuth = $false
$cfg.Port = $port
if ($cfg.PSObject.Properties.Name -contains "AdvertiseServer") { $cfg.AdvertiseServer = $false }
if ($cfg.PSObject.Properties.Name -contains "Upnp") { $cfg.Upnp = $false }
[System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 40))

function Wait-LogLine([string]$log, [string]$pattern, [int]$tries) {
    for ($i=0; $i -lt $tries; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern $pattern -Quiet)) { return $true }
    }
    return $false
}

try {
    # ---- Join 1: wound, photograph, exit mid-bleed ----
    Write-Host "Boot 1: offline server on port $port ..."
    $env:TASSHUNTING_REJOINCLIENT = "1"
    $env:TASSHUNTING_REJOINCLIENT_SHOTS = $shots
    $sp = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
    if (-not (Wait-LogLine $slog "Dedicated Server now running" 40)) { throw "server 1 never came up" }

    Write-Host "Join 1: client (isolated profile) -> localhost:$port ..."
    $cp = Start-Process $cliExe -ArgumentList @("--dataPath",$cdata,"-c","localhost:$port") -PassThru
    $p1 = Wait-LogLine $clog "REJOINCLIENT PHASE1 COMPLETE" 60
    Write-Host "phase1-complete: $p1"
    # THE EXIT: kill the client while bleeding.
    try { Stop-Process -Id $cp.Id -Force -ErrorAction SilentlyContinue } catch {}
    if (-not $p1) { throw "phase 1 client checks never completed - run invalid" }

    # The server sees the disconnect and shuts itself down cleanly (saving mid-bleed state).
    if (-not $sp.WaitForExit(120000)) {
        try { Stop-Process -Id $sp.Id -Force -ErrorAction SilentlyContinue } catch {}
        throw "server 1 did not shut down gracefully - the save is untrustworthy, run invalid"
    }
    Copy-Item $clog "$run\client-main-phase1.log" -ErrorAction SilentlyContinue
    Copy-Item $slog "$run\server-main-phase1.log" -ErrorAction SilentlyContinue

    # ---- Join 2: same world, same account ----
    Write-Host "Boot 2: same world ..."
    $env:TASSHUNTING_REJOINCLIENT = "2"
    $sp2 = Start-Process $srvExe -ArgumentList @("--dataPath",$sdata) -PassThru -WindowStyle Hidden
    if (-not (Wait-LogLine $slog "Dedicated Server now running" 40)) { throw "server 2 never came up" }

    Write-Host "Join 2: client rejoining ..."
    $cp2 = Start-Process $cliExe -ArgumentList @("--dataPath",$cdata,"-c","localhost:$port") -PassThru
    $p2 = Wait-LogLine $clog "REJOINCLIENT PHASE2 COMPLETE" 80
    Write-Host "phase2-complete: $p2"
    try { Stop-Process -Id $cp2.Id -Force -ErrorAction SilentlyContinue } catch {}
    try { Stop-Process -Id $sp2.Id -Force -ErrorAction SilentlyContinue } catch {}
}
finally {
    $env:TASSHUNTING_REJOINCLIENT = $null
    $env:TASSHUNTING_REJOINCLIENT_SHOTS = $null
    # The borrowed session credential does not stay on disk.
    Remove-Item $csPath -Force -ErrorAction SilentlyContinue
}

Write-Host "===== RESULTS ($run) ====="
Write-Host "----- CLIENT phase 1 -----"
if (Test-Path "$run\client-main-phase1.log") {
    Select-String -Path "$run\client-main-phase1.log" -Pattern "\[rejoinclient\]|lacking mods|\[Error\]" | ForEach-Object { $_.Line }
}
Write-Host "----- CLIENT phase 2 -----"
if (Test-Path $clog) {
    Select-String -Path $clog -Pattern "\[rejoinclient\]|lacking mods|\[Error\]" | ForEach-Object { $_.Line }
}
Write-Host "----- SERVER phase 1 -----"
if (Test-Path "$run\server-main-phase1.log") {
    Select-String -Path "$run\server-main-phase1.log" -Pattern "\[rejoinclient\]|\[Error\]" | ForEach-Object { $_.Line }
}
Write-Host "----- SERVER phase 2 -----"
if (Test-Path $slog) {
    Select-String -Path $slog -Pattern "\[rejoinclient\]|\[Error\]" | ForEach-Object { $_.Line }
}
Write-Host "shots: $shots"
if (Test-Path $shots) { Get-ChildItem $shots | ForEach-Object { "  " + $_.FullName + "  (" + $_.Length + " bytes)" } }
