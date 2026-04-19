<#
.SYNOPSIS
    Build SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More and package it as an installable zip.

.DESCRIPTION
    1. Finds Stardew Valley (or you pass -GamePath).
    2. Builds the C# project with dotnet.
    3. Copies the DLL + manifest + assets into mod\SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More\.
    4. Creates a distributable zip in dist\.

.PARAMETER GamePath
    Path to your Stardew Valley folder (e.g. "C:\Steam\steamapps\common\Stardew Valley").
    If omitted the script tries common Steam / GOG locations automatically.

.PARAMETER OutputDir
    Where to write the final zip.  Defaults to "dist".

.EXAMPLE
    .\build-package.ps1
    .\build-package.ps1 -GamePath "D:\Games\Stardew Valley" -OutputDir "release"
#>
param(
    [string]$GamePath  = "",
    [string]$OutputDir = "dist"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ModFolder  = "SVP Physics, Collisions, Hitstops, Idles, Ragdolls and More"
$ModDir     = Join-Path $PSScriptRoot "mod\$ModFolder"
$ProjectDir = Join-Path $PSScriptRoot "src\StardewHdtPhysics"
$CsprojPath = Join-Path $ProjectDir  "StardewHdtPhysics.csproj"

# ── Auto-detect game path ─────────────────────────────────────────────────────
if (-not $GamePath) {
    $candidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
        "C:\Program Files\Steam\steamapps\common\Stardew Valley",
        "$env:ProgramFiles\GOG Galaxy\Games\Stardew Valley",
        "$env:LOCALAPPDATA\GOG.com\Galaxy\Games\Stardew Valley - v2",
        "$env:USERPROFILE\AppData\Local\GOG.com\Galaxy\Games\Stardew Valley"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "StardewValley.exe")) {
            $GamePath = $c
            Write-Host "Auto-detected game path: $GamePath"
            break
        }
    }
}

if (-not $GamePath -or -not (Test-Path (Join-Path $GamePath "StardewValley.exe"))) {
    Write-Warning "Stardew Valley not found.  Building without auto-deploy."
    Write-Warning "Pass -GamePath 'C:\path\to\Stardew Valley' to enable auto-deploy."
    $GamePath = ""
}

# ── Build ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==> Building StardewHdtPhysics ..." -ForegroundColor Cyan

$buildArgs = @("build", $CsprojPath, "-c", "Release")
if ($GamePath) { $buildArgs += "/p:GamePath=$GamePath" }
else           { $buildArgs += "/p:EnableGameDeployment=false" }

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit $LASTEXITCODE)."
    exit $LASTEXITCODE
}

# ── Stage mod folder ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==> Staging mod folder ..." -ForegroundColor Cyan

$dllSrc = Join-Path $ProjectDir "bin\Release\net6.0\StardewHdtPhysics.dll"
if (-not (Test-Path $dllSrc)) {
    Write-Error "DLL not found at: $dllSrc  (did the build succeed?)"
    exit 1
}

# Copy DLL into the mod template folder so it's complete for drag-and-drop
Copy-Item $dllSrc $ModDir -Force
Write-Host "  DLL  → $ModDir\StardewHdtPhysics.dll"

# Sync assets from source (keeps the mod folder up-to-date with any asset edits)
Copy-Item (Join-Path $ProjectDir "assets\*.json") (Join-Path $ModDir "assets") -Force
Write-Host "  assets → $ModDir\assets\"

# Sync manifest from source
Copy-Item (Join-Path $ProjectDir "manifest.json") $ModDir -Force
Write-Host "  manifest.json → $ModDir"

# ── Create distribution zip ───────────────────────────────────────────────────
Write-Host ""
Write-Host "==> Packaging zip ..." -ForegroundColor Cyan

$null = New-Item -ItemType Directory -Force $OutputDir
$ZipPath = Join-Path $OutputDir "$ModFolder.zip"

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path (Join-Path (Split-Path $ModDir) $ModFolder) -DestinationPath $ZipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Green
Write-Host " Package ready:" -ForegroundColor Green
Write-Host "   $ZipPath" -ForegroundColor White
Write-Host ""
Write-Host " To install manually:" -ForegroundColor Green
Write-Host "   Extract the zip." -ForegroundColor White
Write-Host "   Copy '$ModFolder\' into Stardew Valley\Mods\" -ForegroundColor White
Write-Host "=====================================================" -ForegroundColor Green
