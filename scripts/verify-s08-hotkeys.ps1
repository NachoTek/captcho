# verify-s08-hotkeys.ps1 — Build verification for S08 (Global Hotkeys).
#
# Builds the .NET solution and runs S08 headless tests (HotkeyRoute, HotkeyManager,
# HotkeyUiWiring) plus dependent regression tests (CapturePreviewService,
# WindowCapturePreviewService, RegionCapturePreviewService, RegionCaptureUiWiring,
# DelayedCapture, ExportUiWiring).
#
# Does NOT require an interactive desktop session — tests use headless
# mediator/test doubles. Manual UAT for real hotkey behavior is documented below
# and in README.md.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s08-hotkeys.ps1

Set-StrictMode -Version 3.0

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S08 Global Hotkeys — Build Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: .NET solution build ───────────────────────────────────────────
Write-Host "[1/8] Building .NET solution..." -ForegroundColor Yellow
$output = dotnet build (Join-Path $RootDir "Respectacle.sln") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: .NET build exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: .NET build" -ForegroundColor Green
Write-Host ""

# ── Step 2: HotkeyRoute mapping tests ─────────────────────────────────────
Write-Host "[2/8] Running HotkeyRoute mapping tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") `
    --filter "HotkeyRoute" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: HotkeyRoute tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: HotkeyRoute tests" -ForegroundColor Green
Write-Host ""

# ── Step 3: HotkeyManager registration/cleanup tests ──────────────────────
Write-Host "[3/8] Running HotkeyManager tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") `
    --filter "HotkeyManager" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: HotkeyManager tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: HotkeyManager tests" -ForegroundColor Green
Write-Host ""

# ── Step 4: HotkeyUiWiring dispatch tests ─────────────────────────────────
Write-Host "[4/8] Running HotkeyUiWiring dispatch tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") `
    --filter "HotkeyUiWiring" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: HotkeyUiWiring tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: HotkeyUiWiring tests" -ForegroundColor Green
Write-Host ""

# ── Step 5: CapturePreviewService regression tests ─────────────────────────
Write-Host "[5/8] Running CapturePreviewService regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") `
    --filter "CapturePreviewService" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: CapturePreviewService tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: CapturePreviewService tests" -ForegroundColor Green
Write-Host ""

# ── Step 6: Window capture regression tests ───────────────────────────────
Write-Host "[6/8] Running WindowCapture regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") `
    --filter "WindowCapturePreviewService" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: WindowCapture tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: WindowCapture tests" -ForegroundColor Green
Write-Host ""

# ── Step 7: Region capture + delayed capture regression tests ─────────────
Write-Host "[7/8] Running RegionCapture + DelayedCapture regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") `
    --filter "RegionCapturePreviewService|RegionCaptureUiWiring|DelayedCapture" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: Region/Delayed capture regression tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Region/Delayed capture regression tests" -ForegroundColor Green
Write-Host ""

# ── Step 8: Export wiring regression tests ─────────────────────────────────
Write-Host "[8/8] Running ExportUiWiring regression tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "respectacle-ui.Tests/respectacle-ui.Tests.csproj") `
    --filter "ExportUiWiring" --verbosity normal 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: ExportUiWiring tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: ExportUiWiring tests" -ForegroundColor Green
Write-Host ""

Write-Host "=== All S08 build checks passed ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Manual UAT for global hotkeys (requires interactive desktop):" -ForegroundColor White
Write-Host ""
Write-Host "1. Launch the app:  dotnet run --project respectacle-ui"
Write-Host "2. Check status bar shows 'All 4 hotkeys registered.' (or conflict details)"
Write-Host "3. ACTIVE WINDOW TESTS:"
Write-Host "   - Press Print Screen          -> Current Monitor capture"
Write-Host "   - Press Win + Print Screen    -> Active Window capture"
Write-Host "   - Press Shift + Print Screen  -> Full Desktop capture"
Write-Host "   - Press Win + Shift + Print   -> Rectangular Region selector"
Write-Host "4. MINIMIZED WINDOW TESTS:"
Write-Host "   - Minimize the app, then press each hotkey -> captures should still trigger"
Write-Host "   - App should restore (or flash in taskbar) and show capture preview"
Write-Host "5. CONFLICT HANDLING:"
Write-Host "   - If Windows/another app owns Print Screen, status shows conflict details"
Write-Host "   - Conflicting hotkeys show Win32 error code; working ones still function"
Write-Host "   - Close and reopen: all hotkeys should reregister cleanly"
Write-Host "6. OVERLAP GUARD:"
Write-Host "   - Press Print Screen rapidly multiple times -> only one capture runs"
Write-Host "   - No crash, no duplicate captures queued"
Write-Host "7. CLEANUP:"
Write-Host "   - Close the app -> no dangling hotkey registrations"
Write-Host "   - After closing, Print Screen returns to normal OS behavior"
Write-Host ""
exit 0
