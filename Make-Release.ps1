# Builds TassHunting (Release) and packages Releases\<modid>_VS<gamever>_<modver>.zip with the
# same root layout as prior releases (modinfo.json / modicon.png / TassHunting.dll / assets/**).
# Single source of truth for packaging - do not hand-roll zips.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"  # machine env var points at a stale dir
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$rel = Join-Path $root "TassHunting\bin\Release\Mods\mod"
if (-not (Test-Path (Join-Path $rel "TassHunting.dll"))) { throw "Build output missing at $rel" }

$modinfo = Get-Content (Join-Path $rel "modinfo.json") -Raw | ConvertFrom-Json
$version = $modinfo.version
$modid = $modinfo.modid

# EVERY RELEASE DOCUMENTS ITSELF (owner order 2026-08-03): the version being
# zipped needs its own entry in CHANGELOG-versions.md, written at the bump.
# Missing entry: a stub from recent git subjects is appended and the build
# refuses, so the curated line lands in the same commit as the zip.
$clPath = Join-Path $root 'CHANGELOG-versions.md'
$clText = if (Test-Path $clPath) { Get-Content $clPath -Raw } else { '' }
if ($clText -notmatch ('(?m)^## ' + [regex]::Escape($version) + '\b')) {
    $subjects = if (Test-Path (Join-Path $root '.git')) { @(git -C $root log --format='  - %s' -15) } else { @('  - (no git history here)') }
    Add-Content -Path $clPath -Value ("`r`n## $version ($(Get-Date -Format yyyy-MM-dd)) STUB - rewrite as what a player would notice`r`n" + ($subjects -join "`r`n") + "`r`n") -Encoding utf8
    throw "CHANGELOG-versions.md has no entry for $version. A stub was appended - rewrite it as the player-facing line, then rerun."
}

# Game version for the zip name: read from the install actually built against,
# never hardcoded (a stale literal is how tasshunting_vs1.22.3_*.zip lied).
$vsDir = $env:VINTAGE_STORY
if (-not $vsDir -or -not (Test-Path (Join-Path $vsDir 'VintagestoryAPI.dll'))) {
    $vsDir = Join-Path $env:APPDATA 'Vintagestory1.22.5'
}
$verFile = Join-Path $vsDir 'VintagestoryServer.dll'
if (-not (Test-Path $verFile)) { $verFile = Join-Path $vsDir 'Vintagestory.exe' }
$gameVer = (((Get-Item $verFile).VersionInfo.FileVersion -split '\.')[0..2]) -join '.'

$outDir = Join-Path $root "Releases"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$zip = Join-Path $outDir ($modid + "_VS" + $gameVer + "_" + $version + ".zip")
if (Test-Path $zip) { [System.IO.File]::Delete($zip) }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in @("modinfo.json", "modicon.png", "TassHunting.dll")) {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($fs, (Join-Path $rel $f), $f, 'Optimal') | Out-Null
}
$assetsDir = Join-Path $rel "assets"
if (Test-Path $assetsDir) {
    $baseLen = ($assetsDir + [char]92).Length
    Get-ChildItem $assetsDir -Recurse -File | ForEach-Object {
        $entry = "assets" + [char]47 + $_.FullName.Substring($baseLen).Replace([char]92, [char]47)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($fs, $_.FullName, $entry, 'Optimal') | Out-Null
    }
}
$fs.Dispose()

Write-Host "Packaged: $zip"
Write-Host ("zip SHA256: " + (Get-FileHash $zip -Algorithm SHA256).Hash)
Write-Host ("dll SHA256: " + (Get-FileHash (Join-Path $rel "TassHunting.dll") -Algorithm SHA256).Hash)
$z = [System.IO.Compression.ZipFile]::OpenRead($zip); $z.Entries | ForEach-Object { "  " + $_.FullName }; $z.Dispose()

# Show this version's changelog with the zip - the lines the mod page gets.
$clEntry = [regex]::Match($clText, ('(?ms)^## ' + [regex]::Escape($version) + '\b.*?(?=^## |\z)')).Value.TrimEnd()
if ($clEntry) { Write-Host "changelog for this zip:" -ForegroundColor Cyan; $clEntry -split "`r?`n" | ForEach-Object { Write-Host ("  " + $_) } }
