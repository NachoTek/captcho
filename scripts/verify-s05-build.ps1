# verify-s05-build.ps1 — Build verification for S05 (Rectangular Region Capture).
#
# Builds the Rust DLL, builds the .NET solution, and runs all S05-related
# tests: Rust crop/validate-crop/capture-region tests, managed interop
# (RegionCapture), UI service (RegionCapturePreviewService), and UI wiring
# (RegionCaptureUiWiring). Also runs the S04 geometry tests to catch
# regressions in the overlay layer.
#
# This script does NOT require an interactive desktop session — it only
# validates that code compiles and deterministic unit tests pass. The
# overlay + capture flow requires manual UAT (see docs/s05-region-selector.md).
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s05-build.ps1

Set-StrictMode -Version 3.0

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S05 Build Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: Rust build + region tests ─────────────────────────────────────
Write-Host "[1/7] Building Rust DLL and running region tests..." -ForegroundColor Yellow
$env:RUSTFLAGS = "--deny warnings"
$output = cargo test --manifest-path (Join-Path $RootDir "rust-dll/Cargo.toml") crop 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: Rust crop tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Rust crop tests" -ForegroundColor Green
Write-Host ""

# ── Step 2: Rust validate_crop tests ──────────────────────────────────────
Write-Host "[2/7] Running Rust validate_crop tests..." -ForegroundColor Yellow
$output = cargo test --manifest-path (Join-Path $RootDir "rust-dll/Cargo.toml") validate 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: Rust validate tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Rust validate tests" -ForegroundColor Green
Write-Host ""

# ── Step 3: Rust capture_region FFI tests ─────────────────────────────────
Write-Host "[3/7] Running Rust capture_region FFI tests..." -ForegroundColor Yellow
$output = cargo test --manifest-path (Join-Path $RootDir "rust-dll/Cargo.toml") capture_region 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: Rust capture_region tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Rust capture_region tests" -ForegroundColor Green
Write-Host ""

# ── Step 4: .NET solution build ───────────────────────────────────────────
Write-Host "[4/7] Building .NET solution..." -ForegroundColor Yellow
$output = dotnet build (Join-Path $RootDir "Respectacle.sln") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: .NET build exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: .NET build" -ForegroundColor Green
Write-Host ""

# ── Step 5: Managed interop tests (RegionCapture) ─────────────────────────
Write-Host "[5/7] Running managed RegionCapture interop tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-capture.Tests/respectacle-capture.Tests.csproj") --filter "RegionCapture" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: RegionCapture interop tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: RegionCapture interop tests" -ForegroundColor Green
Write-Host ""

# ── Step 6: UI service + wiring tests ─────────────────────────────────────
Write-Host "[6/7] Running UI RegionCapturePreviewService + wiring tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") --filter "RegionCapture" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: UI RegionCapture tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: UI RegionCapture tests" -ForegroundColor Green
Write-Host ""

# ── Step 7: S04 overlay regression tests ──────────────────────────────────
Write-Host "[7/7] Running S04 overlay regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") --filter "RegionSelection|CoordinateHelper|RegionSelectionStatus" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: S04 regression tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: S04 overlay regression tests" -ForegroundColor Green
Write-Host ""

Write-Host "=== All S05 build checks passed ===" -ForegroundColor Cyan
exit 0
