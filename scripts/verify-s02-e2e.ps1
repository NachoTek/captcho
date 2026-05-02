# verify-s02-e2e.ps1 — End-to-end validation for S02 Capture Library + WinUI Preview.
#
# Builds Rust DLL + C# solution, stages the DLL, runs real desktop capture
# through cs-tester --verify-s02, and asserts structured output invariants
# for both full-desktop and current-monitor modes. Also verifies the WinUI
# project builds successfully (runtime UI launch is a manual UAT step).
#
# Produces a pass/fail verdict with capture/display timings.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s02-e2e.ps1
#     or: pwsh -File scripts/verify-s02-e2e.ps1
#
# Requires:
#   - Rust toolchain (cargo) on PATH
#   - .NET 8 SDK on PATH
#   - Interactive Windows desktop session (WGC needs a visible desktop)
#   - Windows 10 1903+ or Windows 11 (Graphics Capture API)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S02 End-to-End Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: Build everything ─────────────────────────────────────────────
Write-Host "[1/4] Running build verification..." -ForegroundColor Yellow
$buildScript = Join-Path $RootDir "scripts/verify-s02-build.ps1"
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

# ── Step 2: Locate and stage Rust DLL ────────────────────────────────────
Write-Host "[2/4] Staging Rust DLL..." -ForegroundColor Yellow

$dllName = "respectacle_capture.dll"
$rustDll = Join-Path $RootDir "rust-dll/target/release/$dllName"
if (-not (Test-Path $rustDll)) {
    Write-Host "FAIL: Rust DLL not found at $rustDll" -ForegroundColor Red
    Write-Host "Run: cargo build --manifest-path rust-dll/Cargo.toml --release" -ForegroundColor Yellow
    exit 1
}

# Stage the DLL into the cs-tester output directory
$csProj = Join-Path $RootDir "cs-tester/cs-tester.csproj"

# Build explicitly with Platform removed to avoid x64 subdirectory nesting
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

# ── Step 3: Run S02 e2e capture and verify output ────────────────────────
Write-Host "[3/4] Running S02 end-to-end capture with --verify-s02..." -ForegroundColor Yellow
Write-Host ""

$verifyOutput = dotnet run --project $csProj -- --verify-s02 2>&1
$verifyExit = $LASTEXITCODE

# Print all output
$verifyOutput | ForEach-Object { Write-Host $_ }
Write-Host ""

# ── Parse structured output ──────────────────────────────────────────────
$stdout = ($verifyOutput | Where-Object { $_ -is [string] }) -join "`n"

# ── Check for WGC unavailability (non-interactive session) ─────────────
# In non-interactive/CI sessions, WGC returns CaptureUnavailable with error
# "Failed to convert item to GraphicsCaptureItem". This is expected behavior.
$fullDesktopUnavailable = $stdout -match 'CaptureMode=full_desktop[\s\S]*?Status=CaptureUnavailable'
$monitorUnavailable = $stdout -match 'CaptureMode=monitor\[\d+\][\s\S]*?Status=CaptureUnavailable'
$wgcError = $stdout -match 'Failed to convert item to .*GraphicsCaptureItem'

if ($fullDesktopUnavailable -and $monitorUnavailable -and $wgcError) {
    Write-Host ""
    Write-Host "WARNING: WGC capture unavailable in non-interactive session" -ForegroundColor Yellow
    Write-Host "  This is expected in CI/headless environments. Real capture testing" -ForegroundColor Yellow
    Write-Host "  requires an interactive Windows desktop session. See README.md for manual UAT." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "=== S02 E2E Verification SKIPPED (WGC unavailable) ===" -ForegroundColor Cyan

    # Still verify WinUI project builds (Step 4)
    Write-Host ""
    Write-Host "[4/4] Verifying WinUI project builds..." -ForegroundColor Yellow
    $uiProj = Join-Path $RootDir "respectacle-ui/respectacle-ui.csproj"
    $output = dotnet build $uiProj 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        Write-Host "FAIL: WinUI project build exited with code $exitCode" -ForegroundColor Red
        exit 1
    }
    Write-Host "PASS: WinUI project builds successfully" -ForegroundColor Green
    Write-Host ""

    Write-Host "=== S02 verification passed (WGC capture skipped) ===" -ForegroundColor Cyan
    Write-Host "NOTE: Real capture and UI launch are manual UAT steps -- see README.md." -ForegroundColor Yellow
    exit 0
}

# ── Normal path: WGC available, verify full invariants ─────────────────
if ($verifyExit -ne 0) {
    Write-Host "FAIL: C# verify runner exited with code $verifyExit" -ForegroundColor Red
    exit 1
}

# Check for VerifyResult: PASS
if ($stdout -notmatch 'VerifyResult:\s*PASS') {
    Write-Host "FAIL: VerifyResult line not found or not PASS" -ForegroundColor Red
    exit 1
}

# ── Validate full-desktop capture invariants ─────────────────────────────
if ($stdout -notmatch 'CaptureMode=full_desktop[\s\S]*?Status=Ok') {
    Write-Host "FAIL: full_desktop capture Status is not Ok" -ForegroundColor Red
    exit 1
}

# Parse full-desktop dimensions
$fullDesktopBlock = ($stdout -split '===\s*Full Desktop Capture\s*===')[1]
if ($fullDesktopBlock) {
    $fullDesktopBlock = ($fullDesktopBlock -split '===\s*Monitor')[0]
}

if ($fullDesktopBlock -and $fullDesktopBlock -match 'Width=(\d+)\s') {
    $fdWidth = [int]$Matches[1]
} else {
    # Fallback: try key=value lines
    if ($stdout -match 'CaptureMode=full_desktop[\s\S]*?Width=(\d+)') {
        $fdWidth = [int]$Matches[1]
    } else {
        $fdWidth = 0
    }
}

if ($fullDesktopBlock -and $fullDesktopBlock -match 'Height=(\d+)') {
    $fdHeight = [int]$Matches[1]
} else {
    if ($stdout -match 'CaptureMode=full_desktop[\s\S]*?Height=(\d+)') {
        $fdHeight = [int]$Matches[1]
    } else {
        $fdHeight = 0
    }
}

if ($fullDesktopBlock -and $fullDesktopBlock -match 'Stride=(\d+)') {
    $fdStride = [int]$Matches[1]
} else {
    if ($stdout -match 'CaptureMode=full_desktop[\s\S]*?Stride=(\d+)') {
        $fdStride = [int]$Matches[1]
    } else {
        $fdStride = 0
    }
}

if ($fullDesktopBlock -and $fullDesktopBlock -match 'DataLen=(\d+)') {
    $fdDataLen = [long]$Matches[1]
} else {
    if ($stdout -match 'CaptureMode=full_desktop[\s\S]*?DataLen=(\d+)') {
        $fdDataLen = [long]$Matches[1]
    } else {
        $fdDataLen = 0
    }
}

# Validate full-desktop dimensions are positive
if ($fdWidth -le 0 -or $fdHeight -le 0) {
    Write-Host "FAIL: full_desktop dimensions non-positive: ${fdWidth}x${fdHeight}" -ForegroundColor Red
    exit 1
}

# Validate stride >= width * 4
$fdMinStride = $fdWidth * 4
if ($fdStride -lt $fdMinStride) {
    Write-Host "FAIL: full_desktop stride ($fdStride) < width*4 ($fdMinStride)" -ForegroundColor Red
    exit 1
}

# Validate data_len == stride * height
$fdExpectedLen = [long]$fdStride * [long]$fdHeight
if ($fdDataLen -ne $fdExpectedLen) {
    Write-Host "FAIL: full_desktop data_len ($fdDataLen) != stride*height ($fdExpectedLen)" -ForegroundColor Red
    exit 1
}

# Parse full-desktop timing
if ($fullDesktopBlock -and $fullDesktopBlock -match 'RoundTripMs=([\d.]+)') {
    $fdRoundTrip = [double]$Matches[1]
} elseif ($stdout -match 'CaptureMode=full_desktop[\s\S]*?RoundTripMs=([\d.]+)') {
    $fdRoundTrip = [double]$Matches[1]
} else {
    Write-Host "FAIL: Could not parse full_desktop RoundTripMs" -ForegroundColor Red
    exit 1
}

# ── Validate monitor[0] capture invariants ───────────────────────────────
if ($stdout -notmatch 'CaptureMode=monitor\[0\][\s\S]*?Status=Ok') {
    Write-Host "FAIL: monitor[0] capture Status is not Ok" -ForegroundColor Red
    exit 1
}

# Parse monitor[0] timing
if ($stdout -notmatch 'CaptureMode=monitor\[0\][\s\S]*?RoundTripMs=([\d.]+)') {
    Write-Host "FAIL: Could not parse monitor[0] RoundTripMs" -ForegroundColor Red
    exit 1
}
$m0RoundTrip = [double]$Matches[1]

Write-Host ""
Write-Host "=== S02 E2E Verification PASSED ===" -ForegroundColor Green
Write-Host "  Full Desktop: ${fdWidth}x${fdHeight}, stride=$fdStride, data_len=$fdDataLen, round-trip=$fdRoundTrip ms" -ForegroundColor Green
Write-Host "  Monitor[0]: round-trip=$m0RoundTrip ms" -ForegroundColor Green

# ── Step 4: Verify WinUI project build (UI launch is manual UAT) ─────────
Write-Host ""
Write-Host "[4/4] Verifying WinUI project builds..." -ForegroundColor Yellow
$uiProj = Join-Path $RootDir "respectacle-ui/respectacle-ui.csproj"
$output = dotnet build $uiProj 2>&1
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    $output | ForEach-Object { Write-Host $_ }
    Write-Host "FAIL: WinUI project build exited with code $exitCode" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: WinUI project builds successfully" -ForegroundColor Green
Write-Host ""

Write-Host "=== All S02 e2e checks passed ===" -ForegroundColor Cyan
Write-Host "NOTE: WinUI runtime UI launch is a manual UAT step -- see README.md." -ForegroundColor Yellow
exit 0
