# verify-s06-build.ps1 — Build verification for S06 (Delayed Capture).
#
# Builds the .NET solution and runs S06-related headless tests:
# DelayedCaptureCountdown (countdown logic), DelayedCaptureUiWiring
# (UI wiring), and CapturePreviewService regression tests. Also runs
# S05 region-capture regression tests.
#
# Does NOT require an interactive desktop session — tests use headless
# countdown and flow-mediator doubles. Manual UAT for live countdown
# behavior is documented in README.md.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s06-build.ps1

Set-StrictMode -Version 3.0

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S06 Build Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: .NET solution build ───────────────────────────────────────────
Write-Host "[1/4] Building .NET solution..." -ForegroundColor Yellow
$output = dotnet build (Join-Path $RootDir "captcho.sln") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: .NET build exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: .NET build" -ForegroundColor Green
Write-Host ""

# ── Step 2: Delayed capture countdown + UI wiring tests ───────────────────
Write-Host "[2/4] Running DelayedCapture countdown + UI wiring tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") --filter "DelayedCapture" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: DelayedCapture tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: DelayedCapture tests" -ForegroundColor Green
Write-Host ""

# ── Step 3: CapturePreviewService regression tests ────────────────────────
Write-Host "[3/4] Running CapturePreviewService regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") --filter "CapturePreviewService" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: CapturePreviewService tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: CapturePreviewService tests" -ForegroundColor Green
Write-Host ""

# ── Step 4: S05 region-capture regression tests ───────────────────────────
Write-Host "[4/4] Running S05 region-capture regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") --filter "RegionCapturePreviewService|RegionCaptureUiWiring" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: S05 regression tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: S05 regression tests" -ForegroundColor Green
Write-Host ""

Write-Host "=== All S06 build checks passed ===" -ForegroundColor Cyan
exit 0
