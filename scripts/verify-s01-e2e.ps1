# verify-s01-e2e.ps1 — End-to-end validation for S01 Core Capture spike.
#
# Builds Rust DLL + C# tester, runs a real capture on the interactive desktop,
# and asserts structured output invariants. Produces a pass/fail verdict.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s01-e2e.ps1
#     or: pwsh -File scripts/verify-s01-e2e.ps1
#
# Requires:
#   - Rust toolchain (cargo) on PATH
#   - .NET 8 SDK on PATH
#   - Interactive Windows desktop session (WGC needs a visible desktop)
#   - Windows 10 1903+ or Windows 11 (Graphics Capture API)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S01 End-to-End Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: Build everything ─────────────────────────────────────────────
Write-Host "[1/3] Running build verification..." -ForegroundColor Yellow
$buildScript = Join-Path $RootDir "scripts/verify-s01-build.ps1"
if (-not (Test-Path $buildScript)) {
    Write-Host "FAIL: Build script not found at $buildScript" -ForegroundColor Red
    exit 1
}

$buildOutput = & powershell -ExecutionPolicy Bypass -File $buildScript 2>&1
$buildExit = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }
if ($buildExit -ne 0) {
    Write-Host "FAIL: Build verification exited with code $buildExit" -ForegroundColor Red
    exit 1
}
Write-Host ""

# ── Step 2: Locate Rust DLL and stage beside C# runner ───────────────────
Write-Host "[2/3] Staging Rust DLL..." -ForegroundColor Yellow

$dllName = "captcho_capture.dll"
$rustDll = Join-Path $RootDir "rust-dll/target/release/$dllName"
if (-not (Test-Path $rustDll)) {
    Write-Host "FAIL: Rust DLL not found at $rustDll" -ForegroundColor Red
    Write-Host "Run: cargo build --manifest-path rust-dll/Cargo.toml --release" -ForegroundColor Yellow
    exit 1
}

# Find the C# output directory — dotnet run publishes to a subdirectory under bin/
$csProj = Join-Path $RootDir "cs-tester/cs-tester.csproj"

# Build explicitly so we control the output path. Remove Platform to avoid x64 subdirectory.
$output = dotnet build $csProj -p:Platform="" 2>&1
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    $output | ForEach-Object { Write-Host $_ }
    Write-Host "FAIL: C# build failed with code $exitCode" -ForegroundColor Red
    exit 1
}

$csOutputDir = Join-Path $RootDir "cs-tester/bin/Debug/net8.0-windows"

if (-not (Test-Path $csOutputDir)) {
    # Try finding any matching output directory
    $csOutputDir = (Get-ChildItem -Path (Join-Path $RootDir "cs-tester/bin/Debug") -Directory -Recurse |
                    Where-Object { $_.Name -like "net8.0*" -and (Test-Path (Join-Path $_.FullName "cs-tester.dll")) } |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1).FullName
}

if (-not $csOutputDir -or -not (Test-Path $csOutputDir)) {
    Write-Host "FAIL: Could not find C# output directory under cs-tester/bin/Debug/" -ForegroundColor Red
    exit 1
}

$targetDll = Join-Path $csOutputDir $dllName
Copy-Item -Path $rustDll -Destination $targetDll -Force
Write-Host "Staged: $targetDll" -ForegroundColor Green
Write-Host ""

# ── Step 3: Run e2e capture and verify output ────────────────────────────
Write-Host "[3/3] Running end-to-end capture with --verify..." -ForegroundColor Yellow
Write-Host ""

$verifyOutput = dotnet run --project $csProj -- --verify 2>&1
$verifyExit = $LASTEXITCODE

# Print all output
$verifyOutput | ForEach-Object { Write-Host $_ }
Write-Host ""

if ($verifyExit -ne 0) {
    Write-Host "FAIL: C# verify runner exited with code $verifyExit" -ForegroundColor Red
    exit 1
}

# ── Parse structured output lines ────────────────────────────────────────
$stdout = ($verifyOutput | Where-Object { $_ -is [string] }) -join "`n"

# Check for VerifyResult: PASS
if ($stdout -notmatch 'VerifyResult:\s*PASS') {
    Write-Host "FAIL: VerifyResult line not found or not PASS" -ForegroundColor Red
    exit 1
}

# Check for Captured: OK line with dimensions
if ($stdout -notmatch 'Captured:\s*OK\s+(\d+)x(\d+)\s+stride=(\d+)\s+data_len=(\d+)') {
    Write-Host "FAIL: Could not parse 'Captured: OK' line with dimensions" -ForegroundColor Red
    exit 1
}

$width = [int]$Matches[1]
$height = [int]$Matches[2]
$stride = [int]$Matches[3]
$dataLen = [int]$Matches[4]

# Assert dimensions are positive
if ($width -le 0 -or $height -le 0) {
    Write-Host "FAIL: Dimensions are non-positive: ${width}x${height}" -ForegroundColor Red
    exit 1
}

# Assert stride >= width * 4
$minStride = $width * 4
if ($stride -lt $minStride) {
    Write-Host "FAIL: Stride ($stride) < width*4 ($minStride)" -ForegroundColor Red
    exit 1
}

# Assert data_len == stride * height
$expectedLen = $stride * $height
if ($dataLen -ne $expectedLen) {
    Write-Host "FAIL: data_len ($dataLen) != stride*height ($expectedLen)" -ForegroundColor Red
    exit 1
}

# Check for RoundTripMs line
if ($stdout -notmatch 'RoundTripMs:\s*([\d.]+)') {
    Write-Host "FAIL: Could not parse RoundTripMs line" -ForegroundColor Red
    exit 1
}
$roundTripMs = [double]$Matches[1]
Write-Host "Observed round-trip: $roundTripMs ms" -ForegroundColor Cyan

# Check for CapturedBitmap dimensions line
if ($stdout -notmatch 'CapturedBitmap:\s+(\d+)x(\d+)\s+format=BGRA') {
    Write-Host "FAIL: Could not parse CapturedBitmap dimensions line" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== S01 E2E Verification PASSED ===" -ForegroundColor Green
Write-Host "  Resolution: ${width}x${height}" -ForegroundColor Green
Write-Host "  Stride: $stride bytes" -ForegroundColor Green
Write-Host "  Data length: $dataLen bytes" -ForegroundColor Green
Write-Host "  Round-trip: $roundTripMs ms" -ForegroundColor Green
exit 0
