# verify-s03-e2e.ps1 — End-to-end validation for S03 Window Capture Modes.
#
# Builds Rust DLL + C# solution, stages the DLL, runs real window capture
# through cs-tester --verify-s03, and asserts structured output invariants
# for active-window and window-under-cursor modes. Gracefully handles
# CaptureUnavailable (non-interactive session limitation).
#
# Produces a pass/fail verdict with capture/display timings.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s03-e2e.ps1
#     or: pwsh -File scripts/verify-s03-e2e.ps1
#
# Requires:
#   - Rust toolchain (cargo) on PATH
#   - .NET 8 SDK on PATH
#   - Interactive Windows desktop session for real capture (headless gracefully skips)
#   - Windows 10 1903+ or Windows 11 (Graphics Capture API)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "=== S03 End-to-End Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: Build everything ─────────────────────────────────────────────
Write-Host "[1/3] Running build verification..." -ForegroundColor Yellow
$buildScript = Join-Path $RootDir "scripts/verify-s03-build.ps1"
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
Write-Host "[2/3] Staging Rust DLL..." -ForegroundColor Yellow

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

# ── Step 3: Run S03 e2e capture and verify output ────────────────────────
Write-Host "[3/3] Running S03 end-to-end capture with --verify-s03..." -ForegroundColor Yellow
Write-Host ""

$verifyOutput = dotnet run --project $csProj -- --verify-s03 2>&1
$verifyExit = $LASTEXITCODE

# Print all output
$verifyOutput | ForEach-Object { Write-Host $_ }
Write-Host ""

# ── Parse structured output ──────────────────────────────────────────────
$stdout = ($verifyOutput | Where-Object { $_ -is [string] }) -join "`n"

# ── Check for WGC unavailability (non-interactive session) ─────────────
# In non-interactive/CI sessions, WGC returns CaptureUnavailable. Both window
# capture modes should report this gracefully — it is expected behavior.
$activeUnavailable = $stdout -match 'CaptureMode=active_window[\s\S]*?Status=CaptureUnavailable'
$cursorUnavailable = $stdout -match 'CaptureMode=window_under_cursor[\s\S]*?Status=CaptureUnavailable'
$wgcError = $stdout -match 'Failed to convert item to .*GraphicsCaptureItem'

# Detect non-interactive session: both captures unavailable with WGC error
$bothUnavailable = $activeUnavailable -and $cursorUnavailable

if ($bothUnavailable -and $wgcError) {
    Write-Host ""
    Write-Host "WARNING: WGC capture unavailable in non-interactive session" -ForegroundColor Yellow
    Write-Host "  Both active_window and window_under_cursor returned CaptureUnavailable." -ForegroundColor Yellow
    Write-Host "  This is expected in CI/headless environments. Real capture testing" -ForegroundColor Yellow
    Write-Host "  requires an interactive Windows desktop session. See README.md for manual UAT." -ForegroundColor Yellow

    # Still check that zero-HWND test passed (negative test doesn't need desktop)
    if ($stdout -notmatch 'PASS: zero_handle_non_ok') {
        Write-Host "FAIL: Zero-HWND negative test did not pass" -ForegroundColor Red
        exit 1
    }
    Write-Host "PASS: Zero-HWND negative test passed (window_by_handle correctly rejects null)" -ForegroundColor Green

    # Check that VerifyResult is PASS
    if ($stdout -notmatch 'VerifyResult:\s*PASS') {
        Write-Host "FAIL: VerifyResult line not found or not PASS" -ForegroundColor Red
        exit 1
    }

    Write-Host ""
    Write-Host "=== S03 E2E Verification PASSED (WGC capture skipped) ===" -ForegroundColor Cyan
    Write-Host "NOTE: Real window capture and WinUI UI launch are manual UAT steps -- see README.md." -ForegroundColor Yellow
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

# ── Helper: parse key=value from a capture mode block ────────────────────
function Parse-BlockKeyValue {
    param(
        [string]$Text,
        [string]$ModeName,
        [string]$Key
    )
    # Match CaptureMode=<modeName>...Key=<value> up to next CaptureMode or section
    $pattern = "CaptureMode=$([regex]::Escape($ModeName))[\s\S]*?$Key=([^\r\n]+)"
    if ($Text -match $pattern) {
        return $Matches[1].Trim()
    }
    return $null
}

# ── Validate active_window capture invariants ────────────────────────────
if ($stdout -match 'CaptureMode=active_window[\s\S]*?Status=Ok') {
    $awWidth = [int](Parse-BlockKeyValue -Text $stdout -ModeName "active_window" -Key "Width")
    $awHeight = [int](Parse-BlockKeyValue -Text $stdout -ModeName "active_window" -Key "Height")
    $awStride = [long](Parse-BlockKeyValue -Text $stdout -ModeName "active_window" -Key "Stride")
    $awDataLen = [long](Parse-BlockKeyValue -Text $stdout -ModeName "active_window" -Key "DataLen")
    $awRoundTrip = Parse-BlockKeyValue -Text $stdout -ModeName "active_window" -Key "RoundTripMs"

    if ($null -eq $awWidth -or $null -eq $awHeight) {
        Write-Host "FAIL: Could not parse active_window dimensions" -ForegroundColor Red
        exit 1
    }

    # Validate positive dimensions
    if ($awWidth -le 0 -or $awHeight -le 0) {
        Write-Host "FAIL: active_window dimensions non-positive: ${awWidth}x${awHeight}" -ForegroundColor Red
        exit 1
    }

    # Validate stride >= width * 4
    $awMinStride = $awWidth * 4
    if ($awStride -lt $awMinStride) {
        Write-Host "FAIL: active_window stride ($awStride) < width*4 ($awMinStride)" -ForegroundColor Red
        exit 1
    }

    # Validate data_len == stride * height
    $awExpectedLen = $awStride * $awHeight
    if ($awDataLen -ne $awExpectedLen) {
        Write-Host "FAIL: active_window data_len ($awDataLen) != stride*height ($awExpectedLen)" -ForegroundColor Red
        exit 1
    }

    Write-Host "PASS: active_window invariants valid: ${awWidth}x${awHeight}, stride=$awStride, data_len=$awDataLen" -ForegroundColor Green
    if ($null -ne $awRoundTrip) {
        Write-Host "  RoundTripMs: $awRoundTrip" -ForegroundColor Green
    }
} else {
    Write-Host "FAIL: active_window capture Status is not Ok" -ForegroundColor Red
    exit 1
}

# ── Validate window_under_cursor capture invariants ──────────────────────
if ($stdout -match 'CaptureMode=window_under_cursor[\s\S]*?Status=Ok') {
    $wcWidth = [int](Parse-BlockKeyValue -Text $stdout -ModeName "window_under_cursor" -Key "Width")
    $wcHeight = [int](Parse-BlockKeyValue -Text $stdout -ModeName "window_under_cursor" -Key "Height")
    $wcStride = [long](Parse-BlockKeyValue -Text $stdout -ModeName "window_under_cursor" -Key "Stride")
    $wcDataLen = [long](Parse-BlockKeyValue -Text $stdout -ModeName "window_under_cursor" -Key "DataLen")
    $wcRoundTrip = Parse-BlockKeyValue -Text $stdout -ModeName "window_under_cursor" -Key "RoundTripMs"

    if ($null -eq $wcWidth -or $null -eq $wcHeight) {
        Write-Host "FAIL: Could not parse window_under_cursor dimensions" -ForegroundColor Red
        exit 1
    }

    # Validate positive dimensions
    if ($wcWidth -le 0 -or $wcHeight -le 0) {
        Write-Host "FAIL: window_under_cursor dimensions non-positive: ${wcWidth}x${wcHeight}" -ForegroundColor Red
        exit 1
    }

    # Validate stride >= width * 4
    $wcMinStride = $wcWidth * 4
    if ($wcStride -lt $wcMinStride) {
        Write-Host "FAIL: window_under_cursor stride ($wcStride) < width*4 ($wcMinStride)" -ForegroundColor Red
        exit 1
    }

    # Validate data_len == stride * height
    $wcExpectedLen = $wcStride * $wcHeight
    if ($wcDataLen -ne $wcExpectedLen) {
        Write-Host "FAIL: window_under_cursor data_len ($wcDataLen) != stride*height ($wcExpectedLen)" -ForegroundColor Red
        exit 1
    }

    Write-Host "PASS: window_under_cursor invariants valid: ${wcWidth}x${wcHeight}, stride=$wcStride, data_len=$wcDataLen" -ForegroundColor Green
    if ($null -ne $wcRoundTrip) {
        Write-Host "  RoundTripMs: $wcRoundTrip" -ForegroundColor Green
    }
} else {
    Write-Host "FAIL: window_under_cursor capture Status is not Ok" -ForegroundColor Red
    exit 1
}

# ── Validate zero-HWND negative test ─────────────────────────────────────
if ($stdout -notmatch 'PASS: zero_handle_non_ok') {
    Write-Host "FAIL: Zero-HWND negative test did not pass" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: Zero-HWND negative test (window_by_handle correctly rejects null handle)" -ForegroundColor Green

Write-Host ""
Write-Host "=== All S03 e2e checks passed ===" -ForegroundColor Cyan
Write-Host "NOTE: WinUI runtime UI launch is a manual UAT step -- see README.md." -ForegroundColor Yellow
exit 0
