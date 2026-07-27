# Builds TassHunting (Release) and packages dist\tasshunting_vs<gamever>_v<modver>.zip with the
# same root layout as prior releases (modinfo.json / modicon.png / TassHunting.dll / assets/**).
# Single source of truth for packaging - do not hand-roll zips.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$env:VINTAGE_STORY = "$env:APPDATA\Vintagestory1.22.5"  # machine env var points at a stale dir
dotnet build "$root\TassHunting\TassHunting.csproj" -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$rel = Join-Path $root "TassHunting\bin\Release\Mods\mod"
if (-not (Test-Path (Join-Path $rel "TassHunting.dll"))) { throw "Build output missing at $rel" }

$version = (Get-Content (Join-Path $rel "modinfo.json") -Raw | ConvertFrom-Json).version
$zip = Join-Path $root ("dist\tasshunting_vs1.22.3_v" + $version + ".zip")
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
