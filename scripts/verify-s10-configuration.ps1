# verify-s10-configuration.ps1 — Build verification for S10 (Configuration Persistence).
#
# Builds the .NET solution and runs S10-related headless tests:
# ConfigurationService + AppSettings (capture library config persistence),
# ConfigurationUiWiring (UI startup and Save/SaveAs settings awareness),
# CapturePreviewService (settings-aware export path), and FilenameTemplate
# (template expansion regression). Also runs ExportUiWiring regressions to
# verify existing export workflows are unaffected by settings integration.
#
# Does NOT require an interactive desktop session — tests use headless
# mediator/test doubles. Manual UAT for settings persistence across restart
# and Save As directory persistence is documented in README.md.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s10-configuration.ps1

Set-StrictMode -Version 3.0

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S10 Configuration Persistence — Build Verification ===" -ForegroundColor Cyan
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

# ── Step 2: Capture-library configuration tests (ConfigurationService + AppSettings) ─
Write-Host "[2/6] Running capture-library configuration tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-capture.Tests/captcho-capture.Tests.csproj") `
    --filter "ConfigurationService|AppSettings" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: Capture-library configuration tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Capture-library configuration tests" -ForegroundColor Green
Write-Host ""

# ── Step 3: FilenameTemplate regression tests ─────────────────────────────
Write-Host "[3/6] Running FilenameTemplate regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-capture.Tests/captcho-capture.Tests.csproj") `
    --filter "FilenameTemplate" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: FilenameTemplate regression tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: FilenameTemplate regression tests" -ForegroundColor Green
Write-Host ""

# ── Step 4: UI configuration wiring tests ─────────────────────────────────
Write-Host "[4/6] Running ConfigurationUiWiring tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") `
    --filter "ConfigurationUiWiring" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: ConfigurationUiWiring tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: ConfigurationUiWiring tests" -ForegroundColor Green
Write-Host ""

# ── Step 5: CapturePreviewService regression (settings-aware export) ───────
Write-Host "[5/6] Running CapturePreviewService regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") `
    --filter "CapturePreviewService" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: CapturePreviewService regression tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: CapturePreviewService regression tests" -ForegroundColor Green
Write-Host ""

# ── Step 6: ExportUiWiring regression tests ────────────────────────────────
Write-Host "[6/6] Running ExportUiWiring regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") `
    --filter "ExportUiWiring" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: ExportUiWiring regression tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: ExportUiWiring regression tests" -ForegroundColor Green
Write-Host ""

Write-Host "=== All S10 configuration checks passed ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Manual UAT for configuration persistence (requires interactive desktop):" -ForegroundColor White
Write-Host ""
Write-Host "1. Launch the app:  dotnet run --project captcho-ui"
Write-Host "2. RESTART PERSISTENCE:"
Write-Host "   - Close and relaunch the app -> verify it starts without errors"
Write-Host "   - Check %LOCALAPPDATA%\captcho\settings.json exists (created on first save)"
Write-Host "3. SAVE LOCATION PERSISTENCE:"
Write-Host "   - Capture a screenshot -> click Save As -> choose a different directory"
Write-Host "   - After successful save, close and relaunch the app"
Write-Host "   - Capture again -> click Save -> verify it saves to the previously chosen directory"
Write-Host "4. CORRUPTED CONFIG RECOVERY:"
Write-Host "   - Edit %LOCALAPPDATA%\captcho\settings.json to contain invalid JSON (e.g. '{bad')"
Write-Host "   - Launch the app -> verify it starts with defaults and shows a config warning"
Write-Host "   - Check that a .backup file was created next to the corrupted settings.json"
Write-Host "5. MISSING CONFIG:"
Write-Host "   - Delete %LOCALAPPDATA%\captcho\settings.json"
Write-Host "   - Launch the app -> verify it starts normally with default settings"
Write-Host "6. SAVE AS CANCEL:"
Write-Host "   - Capture a screenshot -> click Save As -> cancel the picker"
Write-Host "   - Verify status shows 'Save cancelled' and settings are not changed"
Write-Host ""
exit 0
