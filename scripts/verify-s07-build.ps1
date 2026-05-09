# verify-s07-build.ps1 — Build verification for S07 (Export Workflows).
#
# Builds the .NET solution and runs S07-related headless tests:
# FilenameTemplate and PngExportService (capture library export),
# ExportUiWiring (UI wiring + status formatting), ExportService (clipboard),
# and CapturePreviewService regression tests. Also runs S06 delayed-capture
# and S05 region-capture regressions.
#
# Does NOT require an interactive desktop session — tests use headless
# mediator/test doubles. Manual UAT for Save As (FileSavePicker) and
# clipboard paste is documented in README.md.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s07-build.ps1

Set-StrictMode -Version 3.0

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S07 Build Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: .NET solution build ───────────────────────────────────────────
Write-Host "[1/6] Building .NET solution..." -ForegroundColor Yellow
$output = dotnet build (Join-Path $RootDir "captcho.sln") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: .NET build exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: .NET build" -ForegroundColor Green
Write-Host ""

# ── Step 2: Capture-library export tests (FilenameTemplate + PngExportService) ─
Write-Host "[2/6] Running capture-library export tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-capture.Tests/captcho-capture.Tests.csproj") `
    --filter "FilenameTemplate|PngExportService" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: Capture-library export tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Capture-library export tests" -ForegroundColor Green
Write-Host ""

# ── Step 3: UI export wiring + status formatter tests ─────────────────────
Write-Host "[3/6] Running ExportUiWiring tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") `
    --filter "ExportUiWiring" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: ExportUiWiring tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: ExportUiWiring tests" -ForegroundColor Green
Write-Host ""

# ── Step 4: Clipboard/ExportService tests ──────────────────────────────────
Write-Host "[4/6] Running ExportService (clipboard) tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") `
    --filter "ExportService|ClipboardExport" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: ExportService tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: ExportService tests" -ForegroundColor Green
Write-Host ""

# ── Step 5: CapturePreviewService regression tests ─────────────────────────
Write-Host "[5/6] Running CapturePreviewService regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") `
    --filter "CapturePreviewService" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: CapturePreviewService tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: CapturePreviewService tests" -ForegroundColor Green
Write-Host ""

# ── Step 6: S06 delayed-capture + S05 region-capture regressions ──────────
Write-Host "[6/6] Running S06 + S05 regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") `
    --filter "DelayedCapture|RegionCapturePreviewService|RegionCaptureUiWiring" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: S06 + S05 regression tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: S06 + S05 regression tests" -ForegroundColor Green
Write-Host ""

Write-Host "=== All S07 build checks passed ===" -ForegroundColor Cyan
exit 0
