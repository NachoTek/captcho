# verify-s03-build.ps1 — Build verification for S03 (Window Capture Modes).
#
# Runs both toolchains (Rust and .NET) and fails fast on any error.
# This script does NOT require an interactive desktop session — it only
# validates that the code compiles and tests pass.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s03-build.ps1
#     or: pwsh -File scripts/verify-s03-build.ps1

Set-StrictMode -Version 3.0

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S03 Build Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: Rust tests (includes window_capture_exports contract tests) ──
Write-Host "[1/6] Running Rust tests..." -ForegroundColor Yellow
$output = cargo test --manifest-path (Join-Path $RootDir "rust-dll/Cargo.toml") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: Rust tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Rust tests" -ForegroundColor Green
Write-Host ""

# ── Step 2: Rust release build ────────────────────────────────────────────
Write-Host "[2/6] Building Rust DLL (release)..." -ForegroundColor Yellow
$output = cargo build --manifest-path (Join-Path $RootDir "rust-dll/Cargo.toml") --release 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: Rust release build exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Rust release build" -ForegroundColor Green
Write-Host ""

# ── Step 3: .NET solution build ───────────────────────────────────────────
Write-Host "[3/6] Building .NET solution..." -ForegroundColor Yellow
$output = dotnet build (Join-Path $RootDir "captcho.sln") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: .NET build exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: .NET build" -ForegroundColor Green
Write-Host ""

# ── Step 4: captcho-capture tests (includes S03 window interop) ──────
Write-Host "[4/6] Running captcho-capture tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-capture.Tests/captcho-capture.Tests.csproj") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: captcho-capture tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: captcho-capture tests" -ForegroundColor Green
Write-Host ""

# ── Step 5: cs-tester tests ──────────────────────────────────────────────
Write-Host "[5/6] Running cs-tester tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "cs-tester.Tests/cs-tester.Tests.csproj") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: cs-tester tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: cs-tester tests" -ForegroundColor Green
Write-Host ""

# ── Step 6: captcho-ui tests (includes S03 WindowResolver + preview) ─
Write-Host "[6/6] Running captcho-ui tests..." -ForegroundColor Yellow
$output = dotnet test (Join-Path $RootDir "captcho-ui.Tests/captcho-ui.Tests.csproj") 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) {
    Write-Host "FAIL: captcho-ui tests exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: captcho-ui tests" -ForegroundColor Green
Write-Host ""

Write-Host "=== All S03 build checks passed ===" -ForegroundColor Cyan
exit 0
