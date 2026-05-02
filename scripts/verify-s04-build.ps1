# verify-s04-build.ps1 — Build verification for S04 (Rectangular Region Selector).
#
# Builds the .NET solution and runs the respectacle-ui test suite,
# with explicit filtering for S04's RegionSelection, CoordinateHelper,
# and RegionSelectionStatus tests.
#
# This script does NOT require an interactive desktop session — it only
# validates that the code compiles and deterministic geometry/formatting
# tests pass. The overlay window itself requires manual UAT (see
# docs/s04-region-selector-spike.md).
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s04-build.ps1

Set-StrictMode -Version 3.0

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S04 Build Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: .NET solution build ───────────────────────────────────────────
Write-Host "[1/4] Building .NET solution..." -ForegroundColor Yellow
$output = dotnet build (Join-Path $RootDir "Respectacle.sln") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: .NET build exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: .NET build" -ForegroundColor Green
Write-Host ""

# ── Step 2: RegionSelection tests ────────────────────────────────────────
Write-Host "[2/4] Running RegionSelection tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") --filter "RegionSelection" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: RegionSelection tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: RegionSelection tests" -ForegroundColor Green
Write-Host ""

# ── Step 3: CoordinateHelper tests ───────────────────────────────────────
Write-Host "[3/4] Running CoordinateHelper tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") --filter "CoordinateHelper" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: CoordinateHelper tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: CoordinateHelper tests" -ForegroundColor Green
Write-Host ""

# ── Step 4: RegionSelectionStatus tests ──────────────────────────────────
Write-Host "[4/4] Running RegionSelectionStatus tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") --filter "RegionSelectionStatus" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: RegionSelectionStatus tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: RegionSelectionStatus tests" -ForegroundColor Green
Write-Host ""

Write-Host "=== All S04 build checks passed ===" -ForegroundColor Cyan
exit 0
