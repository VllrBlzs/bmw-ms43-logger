[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [switch]$ConfirmReadOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $ConfirmReadOnly) {
    throw "BMW-14 live fault matrix requires -ConfirmReadOnly."
}

$root = [IO.Path]::GetFullPath($RepositoryRoot)
Set-Location -LiteralPath $root

$acceptanceProject = Join-Path $root `
    "tools\BmwMs43Logger.LiveAcceptanceProbe\BmwMs43Logger.LiveAcceptanceProbe.csproj"
$catalogPath = Join-Path $root "config\ms430ds0-parameter-catalog.draft.json"
$packPath = Join-Path $root "config\ms430ds0-bounded-live-test-pack.json"
$matrixId = "bmw14_fault_matrix_{0}" -f (Get-Date).ToUniversalTime().ToString(
    "yyyyMMdd_HHmmss")
$matrixRoot = Join-Path $root "data\private\bmw14-live-fault-matrix\$matrixId"
New-Item -ItemType Directory -Path $matrixRoot -Force | Out-Null

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE"
    }
}

function Read-RequiredConfirmation {
    param([Parameter(Mandatory)][string]$Prompt)

    $answer = Read-Host "$Prompt Type READY"
    if ($answer -cne "READY") {
        throw "BMW-14 phase cancelled; READY was not entered."
    }
}

function Wait-Com5 {
    param([int]$TimeoutSeconds = 30)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $ports = @([IO.Ports.SerialPort]::GetPortNames())
        if ($ports -contains "COM5") {
            Write-Host "COM5=AVAILABLE"
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "COM5 did not return within $TimeoutSeconds seconds."
}

function Invoke-Preflight {
    Invoke-Checked `
        -FilePath "dotnet" `
        -Arguments @(
            "run", "--project", $acceptanceProject,
            "-c", "Release", "--no-build", "--",
            "--preflight",
            "--ecu-path", "C:\EDIABAS\ECU",
            "--port", "COM5",
            "--catalog", $catalogPath,
            "--pack", $packPath
        ) `
        -FailureMessage "BMW-14 pinned live preflight failed."
}

function Invoke-LivePhase {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("capture", "expect-loss")]
        [string]$Mode,

        [Parameter(Mandatory)]
        [string]$Action,

        [Parameter(Mandatory)]
        [int]$DurationSeconds
    )

    $modeSwitch = if ($Mode -eq "capture") {
        "--capture"
    }
    else {
        "--expect-loss"
    }

    Invoke-Checked `
        -FilePath "dotnet" `
        -Arguments @(
            "run", "--project", $acceptanceProject,
            "-c", "Release", "--no-build", "--",
            $modeSwitch,
            "--confirm-read-only",
            "--action", $Action,
            "--duration-seconds", $DurationSeconds.ToString(),
            "--requested-hz", "5.0",
            "--selection", "default4",
            "--output-dir", $matrixRoot,
            "--ecu-path", "C:\EDIABAS\ECU",
            "--port", "COM5",
            "--catalog", $catalogPath,
            "--pack", $packPath
        ) `
        -FailureMessage "BMW-14 live phase failed: $Action."
}

function Assert-Phase {
    param(
        [Parameter(Mandatory)]
        [object]$Report,

        [Parameter(Mandatory)]
        [string]$ExpectedPhase,

        [Parameter(Mandatory)]
        [ValidateSet("capture", "expectloss")]
        [string]$ExpectedMode,

        [Parameter(Mandatory)]
        [ValidateSet("completed", "faulted")]
        [string]$ExpectedStatus
    )

    if ($Report.phase -cne $ExpectedPhase -or
        $Report.mode -cne $ExpectedMode -or
        $Report.session_status -cne $ExpectedStatus -or
        $Report.result -cne "pass" -or
        -not [bool]$Report.interface_released -or
        $Report.dispatch_policy -cne "pinned_status_empty_argument_only") {
        throw "BMW-14 phase acceptance mismatch: $ExpectedPhase."
    }

    if ($ExpectedMode -eq "capture") {
        if ([bool]$Report.requires_reconnect -or
            -not [string]::IsNullOrWhiteSpace([string]$Report.fault_code)) {
            throw "Normal capture contains a transport fault: $ExpectedPhase."
        }
    }
    else {
        $allowedFaults = @(
            "EDIABAS_TIMEOUT",
            "EDIABAS_PORT_LOST",
            "EDIABAS_TRANSPORT_ERROR"
        )
        if (-not [bool]$Report.requires_reconnect -or
            $allowedFaults -cnotcontains [string]$Report.fault_code) {
            throw "Expected transport loss was not proven: $ExpectedPhase."
        }
    }
}

Write-Host "BMW-14 LIVE FAULT/RECOVERY MATRIX"
Write-Host "ROAD_TEST_INCLUDED=False"
Write-Host "GUI_VALIDATION_INCLUDED=False"
Write-Host "ENGINE_REQUIRED=OFF"
Write-Host "INITIAL_IGNITION=POSITION_II"
Write-Host "INPA_REQUIRED=CLOSED"
Write-Host "MATRIX_ID=$matrixId"
Write-Host "MATRIX_ROOT=$matrixRoot"

Invoke-Preflight

Read-RequiredConfirmation `
    "Close INPA, connect COM5, set ignition to position II, keep engine OFF."
Wait-Com5
Invoke-LivePhase -Mode capture -Action baseline -DurationSeconds 15

Read-RequiredConfirmation `
    "Cable-loss setup: keep the USB cable CONNECTED, ignition II and engine OFF. Type READY, then wait for ACTION_NOW before unplugging the USB cable."
Wait-Com5
Start-Sleep -Seconds 3
Invoke-LivePhase -Mode expect-loss -Action cable_disconnect -DurationSeconds 90

Read-RequiredConfirmation `
    "Reconnect the USB cable, keep ignition II and engine OFF."
Wait-Com5
Invoke-LivePhase `
    -Mode capture `
    -Action reconnect_after_cable `
    -DurationSeconds 15

Read-RequiredConfirmation `
    "Ignition-loss setup: keep the cable connected, ignition II and engine OFF. Type READY, then wait for ACTION_NOW before turning ignition fully OFF."
Wait-Com5
Start-Sleep -Seconds 3
Invoke-LivePhase -Mode expect-loss -Action ignition_off -DurationSeconds 90

Read-RequiredConfirmation `
    "Turn ignition back to position II, keep engine OFF, and wait for the DME to wake."
Wait-Com5
Start-Sleep -Seconds 3
Invoke-LivePhase `
    -Mode capture `
    -Action reconnect_after_ignition `
    -DurationSeconds 15

$reportFiles = @(
    Get-ChildItem -LiteralPath $matrixRoot `
        -Filter "bmw14.phase.acceptance.json" `
        -File `
        -Recurse |
        Sort-Object LastWriteTimeUtc
)

if ($reportFiles.Count -ne 5) {
    throw "Expected exactly five fresh BMW-14 phase reports; found $($reportFiles.Count)."
}

$reports = @(
    $reportFiles |
        ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        }
)

Assert-Phase $reports[0] "baseline" "capture" "completed"
Assert-Phase $reports[1] "cable_disconnect" "expectloss" "faulted"
Assert-Phase $reports[2] "reconnect_after_cable" "capture" "completed"
Assert-Phase $reports[3] "ignition_off" "expectloss" "faulted"
Assert-Phase $reports[4] "reconnect_after_ignition" "capture" "completed"

$epochs = @($reports | ForEach-Object epoch_id | Sort-Object -Unique)
if ($epochs.Count -ne 5) {
    throw "BMW-14 reconnect proof requires five distinct epochs; found $($epochs.Count)."
}

$phaseEvidence = @()
for ($index = 0; $index -lt $reportFiles.Count; $index++) {
    $phaseEvidence += [ordered]@{
        phase = $reports[$index].phase
        mode = $reports[$index].mode
        epoch_id = $reports[$index].epoch_id
        session_status = $reports[$index].session_status
        fault_code = $reports[$index].fault_code
        requires_reconnect = $reports[$index].requires_reconnect
        report_sha256 = (
            Get-FileHash -LiteralPath $reportFiles[$index].FullName -Algorithm SHA256
        ).Hash
        result = $reports[$index].result
    }
}

$summary = [ordered]@{
    schema_version = "bmw14.live_fault_matrix.v1"
    generated_utc = [DateTimeOffset]::UtcNow
    matrix_id = $matrixId
    communication_attempted = $true
    read_only_dispatch = $true
    engine_state = "off"
    initial_ignition_state = "position_II"
    phase_count = 5
    distinct_epochs = $epochs.Count
    phases = $phaseEvidence
    result = "pass"
}

$summaryPath = Join-Path $matrixRoot "bmw14-live-fault-matrix.json"
$summaryText = $summary | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText(
    $summaryPath,
    $summaryText,
    [Text.UTF8Encoding]::new($false)
)

Write-Host "BMW14_BASELINE=PASS"
Write-Host "BMW14_CABLE_LOSS=PASS"
Write-Host "BMW14_CABLE_RECOVERY=PASS"
Write-Host "BMW14_IGNITION_LOSS=PASS"
Write-Host "BMW14_IGNITION_RECOVERY=PASS"
Write-Host "DISTINCT_EPOCHS=5"
Write-Host "MATRIX_REPORT=$summaryPath"
Write-Host "BMW14_LIVE_FAULT_MATRIX=PASS"
Write-Host "NEXT=Run scripts\complete-bmw15-final-qa.ps1 at the charger."
