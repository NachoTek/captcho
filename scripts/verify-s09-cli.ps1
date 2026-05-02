# verify-s09-cli.ps1 — Verification script for S09 (Production CLI).
#
# Builds the .NET solution, runs CLI unit tests, runs respectacle-cli --help,
# verifies invalid-argument exit codes, and attempts a real capture smoke test
# (accepting CaptureUnavailable as an environment limitation in non-interactive sessions).
#
# Does NOT depend on ignored .gsd/ fixtures — all paths are source-tracked.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/verify-s09-cli.ps1
#     or: pwsh -File scripts/verify-s09-cli.ps1
#
# Requires:
#   - .NET 8 SDK on PATH
#   - Optional: Rust toolchain + interactive desktop for real capture smoke test

Set-StrictMode -Version 3.0

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
$PassCount = 0
$FailCount = 0
$WarnCount = 0
$Results = @()

function Record-Result {
    param([string]$Step, [string]$Verdict, [string]$Detail = "")
    $script:Results += [PSCustomObject]@{ Step = $Step; Verdict = $Verdict; Detail = $Detail }
    switch ($Verdict) {
        "PASS"    { $script:PassCount++ }
        "FAIL"    { $script:FailCount++ }
        "WARN"    { $script:WarnCount++ }
    }
}

Write-Host "=== S09 Production CLI - Verification ===" -ForegroundColor Cyan
Write-Host "Root: $RootDir"
Write-Host ""

# ── Step 1: Build .NET solution ──────────────────────────────────────────
Write-Host "[1/6] Building .NET solution..." -ForegroundColor Yellow
$buildOutput = dotnet build (Join-Path $RootDir "Respectacle.sln") 2>&1
$buildExit = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }
if ($buildExit -ne 0) {
    Write-Host "FAIL: .NET build exited with code $buildExit" -ForegroundColor Red
    Record-Result "dotnet build" "FAIL" "Exit code $buildExit"
    # Build failure is fatal
    Write-Host ""
    Write-Host "=== VERIFICATION ABORTED (build failure) ===" -ForegroundColor Red
    exit 1
}
Write-Host "PASS: .NET build" -ForegroundColor Green
Record-Result "dotnet build" "PASS"
Write-Host ""

# ── Step 2: Run CLI unit tests ───────────────────────────────────────────
Write-Host "[2/6] Running CLI unit tests..." -ForegroundColor Yellow
$testOutput = dotnet test (Join-Path $RootDir "respectacle-cli.Tests/respectacle-cli.Tests.csproj") `
    --verbosity normal 2>&1
$testExit = $LASTEXITCODE
$testOutput | ForEach-Object { Write-Host $_ }
if ($testExit -ne 0) {
    Write-Host "FAIL: CLI tests exited with code $testExit" -ForegroundColor Red
    Record-Result "CLI unit tests" "FAIL" "Exit code $testExit"
} else {
    Write-Host "PASS: CLI unit tests" -ForegroundColor Green
    Record-Result "CLI unit tests" "PASS"
}
Write-Host ""

# ── Step 3: --help smoke test ────────────────────────────────────────────
Write-Host "[3/6] Testing respectacle-cli --help..." -ForegroundColor Yellow

# Find the CLI output directory
$cliProj = Join-Path $RootDir "respectacle-cli/respectacle-cli.csproj"
$cliOutputDir = Join-Path $RootDir "respectacle-cli/bin/Debug/net8.0-windows"

# Build the CLI project explicitly to ensure it's available
$cliBuildOutput = dotnet build $cliProj 2>&1
$cliBuildExit = $LASTEXITCODE
if ($cliBuildExit -ne 0) {
    $cliBuildOutput | ForEach-Object { Write-Host $_ }
    Write-Host "FAIL: CLI project build failed" -ForegroundColor Red
    Record-Result "CLI build" "FAIL" "Exit code $cliBuildExit"
    Write-Host ""
    Write-Host "=== VERIFICATION ABORTED (CLI build failure) ===" -ForegroundColor Red
    exit 1
}

# Run --help via dotnet run
$helpOutput = dotnet run --project $cliProj -- --help 2>&1
$helpExit = $LASTEXITCODE
$helpText = ($helpOutput | Where-Object { $_ -is [string] }) -join "`n"
$helpOutput | ForEach-Object { Write-Host $_ }
Write-Host ""

# Verify help exit code is 0
if ($helpExit -ne 0) {
    Write-Host "FAIL: --help returned exit code $helpExit (expected 0)" -ForegroundColor Red
    Record-Result "--help exit code" "FAIL" "Got $helpExit, expected 0"
} else {
    Write-Host "PASS: --help exit code is 0" -ForegroundColor Green
    Record-Result "--help exit code" "PASS"
}

# Verify help text contains all five capture modes
$modesPresent = $true
$requiredModes = @("--full", "--monitor", "--window-active", "--window-cursor", "--region")
foreach ($mode in $requiredModes) {
    if ($helpText -notmatch [regex]::Escape($mode)) {
        Write-Host "FAIL: --help missing mode '$mode'" -ForegroundColor Red
        $modesPresent = $false
    }
}
if ($modesPresent) {
    Write-Host "PASS: --help contains all 5 capture modes" -ForegroundColor Green
    Record-Result "--help modes" "PASS"
} else {
    Record-Result "--help modes" "FAIL" "One or more modes missing"
}

# Verify help text contains output flags and verbose
$helpFlags = $true
$requiredFlags = @("--output", "--filename", "--verbose", "--help")
foreach ($flag in $requiredFlags) {
    if ($helpText -notmatch [regex]::Escape($flag)) {
        Write-Host "FAIL: --help missing flag '$flag'" -ForegroundColor Red
        $helpFlags = $false
    }
}
if ($helpFlags) {
    Write-Host "PASS: --help contains output and general flags" -ForegroundColor Green
    Record-Result "--help flags" "PASS"
} else {
    Record-Result "--help flags" "FAIL" "One or more flags missing"
}

# Verify help mentions exit codes
if ($helpText -match "Exit Codes" -and $helpText -match "0" -and $helpText -match "1" -and $helpText -match "2" -and $helpText -match "3") {
    Write-Host "PASS: --help documents exit codes" -ForegroundColor Green
    Record-Result "--help exit codes" "PASS"
} else {
    Write-Host "FAIL: --help missing exit code documentation" -ForegroundColor Red
    Record-Result "--help exit codes" "FAIL" "Missing exit code section or values"
}

# Verify --region coordinate syntax is mentioned
if ($helpText -match "x,y,width,height") {
    Write-Host "PASS: --help describes region coordinate syntax" -ForegroundColor Green
    Record-Result "--help region syntax" "PASS"
} else {
    Write-Host "FAIL: --help missing region coordinate syntax (x,y,width,height)" -ForegroundColor Red
    Record-Result "--help region syntax" "FAIL"
}

# Verify --monitor index syntax is mentioned
if ($helpText -match "monitor.*index" -or $helpText -match "\[index\]") {
    Write-Host "PASS: --help describes monitor index syntax" -ForegroundColor Green
    Record-Result "--help monitor syntax" "PASS"
} else {
    Write-Host "FAIL: --help missing monitor index syntax" -ForegroundColor Red
    Record-Result "--help monitor syntax" "FAIL"
}

Write-Host ""

# ── Step 4: Invalid argument exit code test ──────────────────────────────
Write-Host "[4/6] Testing invalid argument handling..." -ForegroundColor Yellow

# Test with unknown flag
$invalidOutput = dotnet run --project $cliProj -- --bogus-flag 2>&1
$invalidExit = $LASTEXITCODE
$invalidText = ($invalidOutput | Where-Object { $_ -is [string] }) -join "`n"
$invalidOutput | ForEach-Object { Write-Host $_ }

if ($invalidExit -eq 1) {
    Write-Host "PASS: invalid args returned exit code 1 (InvalidArguments)" -ForegroundColor Green
    Record-Result "invalid arg exit code" "PASS"
} else {
    Write-Host "FAIL: invalid args returned exit code $invalidExit (expected 1)" -ForegroundColor Red
    Record-Result "invalid arg exit code" "FAIL" "Got $invalidExit, expected 1"
}

# Verify the error message identifies the offending option
# dotnet run 2>&1 may return ErrorRecord objects for stderr; convert all to string
$invalidAll = ($invalidOutput | Out-String)
if ($invalidAll -match "bogus-flag") {
    Write-Host "PASS: error message identifies offending option" -ForegroundColor Green
    Record-Result "invalid arg message" "PASS"
} else {
    Write-Host "FAIL: error message does not identify the offending option" -ForegroundColor Red
    Record-Result "invalid arg message" "FAIL"
}

# Test with no arguments
$noArgsOutput = dotnet run --project $cliProj 2>&1
$noArgsExit = $LASTEXITCODE
if ($noArgsExit -eq 1) {
    Write-Host "PASS: no-args returned exit code 1 (InvalidArguments)" -ForegroundColor Green
    Record-Result "no-args exit code" "PASS"
} else {
    Write-Host "FAIL: no-args returned exit code $noArgsExit (expected 1)" -ForegroundColor Red
    Record-Result "no-args exit code" "FAIL" "Got $noArgsExit, expected 1"
}

# Test with multiple modes
$multiModeOutput = dotnet run --project $cliProj -- --full --monitor 2>&1
$multiModeExit = $LASTEXITCODE
if ($multiModeExit -eq 1) {
    Write-Host "PASS: multiple modes returned exit code 1" -ForegroundColor Green
    Record-Result "multi-mode exit code" "PASS"
} else {
    Write-Host "FAIL: multiple modes returned exit code $multiModeExit (expected 1)" -ForegroundColor Red
    Record-Result "multi-mode exit code" "FAIL" "Got $multiModeExit, expected 1"
}

Write-Host ""

# ── Step 5: Real capture smoke test (optional) ───────────────────────────
Write-Host "[5/6] Real capture smoke test (full-desktop)..." -ForegroundColor Yellow

# Check if Rust DLL is available for native capture
$rustDll = Join-Path $RootDir "rust-dll/target/release/respectacle_capture.dll"
$captureAvailable = Test-Path $rustDll

if (-not $captureAvailable) {
    Write-Host "WARN: Rust DLL not found - skipping real capture smoke test" -ForegroundColor Yellow
    Write-Host "  To enable real capture: cargo build --manifest-path rust-dll/Cargo.toml --release" -ForegroundColor Yellow
    Record-Result "capture smoke test" "WARN" "Rust DLL not available"
} else {
    # Stage the DLL into the CLI output directory
    $cliOutDir = Join-Path $RootDir "respectacle-cli/bin/Debug/net8.0-windows"
    if (-not (Test-Path $cliOutDir)) {
        # Try finding any matching output directory
        $cliOutDir = (Get-ChildItem -Path (Join-Path $RootDir "respectacle-cli/bin/Debug") -Directory -Recurse |
                        Where-Object { $_.Name -like "net8.0*" -and (Test-Path (Join-Path $_.FullName "respectacle-cli.dll")) } |
                        Sort-Object LastWriteTime -Descending |
                        Select-Object -First 1).FullName
    }

    if ($cliOutDir -and (Test-Path $cliOutDir)) {
        Copy-Item -Path $rustDll -Destination (Join-Path $cliOutDir "respectacle_capture.dll") -Force

        $captureOutput = dotnet run --project $cliProj -- --full --output "$env:TEMP\respectacle-s09-verify" --verbose 2>&1
        $captureExit = $LASTEXITCODE
        $captureText = ($captureOutput | Where-Object { $_ -is [string] }) -join "`n"
        $captureOutput | ForEach-Object { Write-Host $_ }

        if ($captureExit -eq 0) {
            Write-Host "PASS: real capture succeeded" -ForegroundColor Green
            Record-Result "capture smoke test" "PASS"

            # Verify structured output fields
            $hasFields = $true
            foreach ($field in @("CaptureMode=", "Status=", "ExitCode=")) {
                if ($captureText -notmatch [regex]::Escape($field)) {
                    Write-Host "FAIL: capture output missing field '$field'" -ForegroundColor Red
                    $hasFields = $false
                }
            }
            if ($hasFields) {
                Write-Host "PASS: capture output has structured fields" -ForegroundColor Green
                Record-Result "capture structured output" "PASS"
            } else {
                Record-Result "capture structured output" "FAIL" "Missing structured fields"
            }

            # Verify dimensions present
            if ($captureText -match "Dimensions=\d+x\d+") {
                Write-Host "PASS: capture output includes dimensions" -ForegroundColor Green
                Record-Result "capture dimensions" "PASS"
            } else {
                Write-Host "FAIL: capture output missing dimensions" -ForegroundColor Red
                Record-Result "capture dimensions" "FAIL"
            }

            # Verify verbose timings
            if ($captureText -match "CaptureMs=" -and $captureText -match "TotalMs=") {
                Write-Host "PASS: verbose output includes timing breakdown" -ForegroundColor Green
                Record-Result "capture timings" "PASS"
            } else {
                Write-Host "FAIL: verbose output missing timing breakdown" -ForegroundColor Red
                Record-Result "capture timings" "FAIL"
            }

            # Clean up temp file
            $tempFile = Get-ChildItem "$env:TEMP\respectacle-s09-verify\*.png" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($tempFile) {
                Remove-Item $tempFile.FullName -Force -ErrorAction SilentlyContinue
                $parentDir = Split-Path $tempFile.FullName -Parent
                $remaining = @(Get-ChildItem $parentDir -ErrorAction SilentlyContinue)
                if ($remaining.Count -eq 0) {
                    Remove-Item $parentDir -Force -ErrorAction SilentlyContinue
                }
            }
        } elseif ($captureText -match "CaptureUnavailable" -or $captureExit -eq 2) {
            # CaptureUnavailable is an expected environment limitation
            Write-Host "WARN: Capture unavailable in non-interactive session" -ForegroundColor Yellow
            Write-Host "  This is expected in CI/RDP/headless environments." -ForegroundColor Yellow
            Write-Host "  Real capture requires an interactive Windows desktop session." -ForegroundColor Yellow
            Record-Result "capture smoke test" "WARN" "CaptureUnavailable - non-interactive session"

            # Still verify structured output
            if ($captureText -match "CaptureMode=" -and $captureText -match "ExitCode=") {
                Write-Host "PASS: capture failure has structured diagnostics" -ForegroundColor Green
                Record-Result "capture failure diagnostics" "PASS"
            } else {
                Write-Host "FAIL: capture failure missing structured diagnostics" -ForegroundColor Red
                Record-Result "capture failure diagnostics" "FAIL"
            }
        } else {
            Write-Host "FAIL: capture exited with code $captureExit" -ForegroundColor Red
            Record-Result "capture smoke test" "FAIL" "Exit code $captureExit (not 0 or CaptureUnavailable)"
        }
    } else {
        Write-Host "WARN: Could not locate CLI output directory for DLL staging" -ForegroundColor Yellow
        Record-Result "capture smoke test" "WARN" "CLI output directory not found"
    }
}
Write-Host ""

# ── Step 6: Documentation consistency test ───────────────────────────────
Write-Host "[6/6] Running documentation consistency tests..." -ForegroundColor Yellow
$docTestOutput = dotnet test (Join-Path $RootDir "respectacle-cli.Tests/respectacle-cli.Tests.csproj") `
    --filter "CliDocumentation" --verbosity normal 2>&1
$docTestExit = $LASTEXITCODE
$docTestOutput | ForEach-Object { Write-Host $_ }
if ($docTestExit -ne 0) {
    Write-Host "FAIL: Documentation consistency tests exited with code $docTestExit" -ForegroundColor Red
    Record-Result "documentation tests" "FAIL" "Exit code $docTestExit"
} else {
    Write-Host "PASS: Documentation consistency tests" -ForegroundColor Green
    Record-Result "documentation tests" "PASS"
}
Write-Host ""

# ── Summary ──────────────────────────────────────────────────────────────
Write-Host "=== S09 Verification Summary ===" -ForegroundColor Cyan
Write-Host ""
foreach ($r in $Results) {
    $color = switch ($r.Verdict) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "WARN" { "Yellow" }
    }
    $detail = if ($r.Detail) { " ($($r.Detail))" } else { "" }
    Write-Host ("  {0,-30} {1}{2}" -f $r.Step, $r.Verdict, $detail) -ForegroundColor $color
}
Write-Host ""
Write-Host ("Totals: {0} PASS, {1} FAIL, {2} WARN" -f $PassCount, $FailCount, $WarnCount) -ForegroundColor White

if ($FailCount -gt 0) {
    Write-Host ""
    Write-Host "=== VERIFICATION FAILED ===" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== VERIFICATION PASSED ===" -ForegroundColor Green
if ($WarnCount -gt 0) {
    Write-Host "  ($WarnCount warning(s) - environment limitations, not failures)" -ForegroundColor Yellow
}
exit 0
