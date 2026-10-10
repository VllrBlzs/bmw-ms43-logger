[CmdletBinding()]
param(
    [ValidateSet("Offline", "Faults", "RoadTest", "AllLive", "Analyze")]
    [string]$Phase = "Offline",

    [string]$RepositoryRoot = "C:\Users\Valler\Desktop\Biznisz\BMW",

    [string]$RunDirectory,

    [switch]$ConfirmReadOnly,

    [switch]$ConfirmVehicleSafe,

    [switch]$ConfirmPassengerOperator
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath($RepositoryRoot)
Set-Location $root
$offlineScript = Join-Path $root "scripts\test-bmw14-offline.ps1"
$acceptanceProject = Join-Path $root `
    "tools\BmwMs43Logger.LiveAcceptanceProbe\BmwMs43Logger.LiveAcceptanceProbe.csproj"
$analyzerProject = Join-Path $root `
    "tools\BmwMs43Logger.RoadTestAnalyzer\BmwMs43Logger.RoadTestAnalyzer.csproj"
$appProject = Join-Path $root "src\BmwMs43Logger.App\BmwMs43Logger.App.csproj"
$faultOutput = Join-Path $root "data\private\bmw14-live-acceptance"
$roadOutput = Join-Path $root "data\private\bmw14-gui-live"

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

function Invoke-LiveProbe {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("capture", "expect-loss")]
        [string]$Mode,

        [Parameter(Mandatory)]
        [string]$Action,

        [Parameter(Mandatory)]
        [int]$DurationSeconds
    )

    $modeSwitch = if ($Mode -eq "capture") { "--capture" } else { "--expect-loss" }
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
            "--output-dir", $faultOutput,
            "--ecu-path", "C:\EDIABAS\ECU",
            "--port", "COM5"
        ) `
        -FailureMessage "Live phase failed: $Action"
}

function Read-RequiredConfirmation {
    param([string]$Prompt)
    $answer = Read-Host "$Prompt Type READY"
    if ($answer -cne "READY") {
        throw "Phase cancelled; READY was not entered."
    }
}

function Invoke-FaultSequence {
    if (-not $ConfirmReadOnly) {
        throw "Faults requires -ConfirmReadOnly."
    }

    Write-Host "BMW-14 FAULT SEQUENCE"
    Write-Host "ENGINE_REQUIRED=OFF"
    Write-Host "IGNITION_INITIAL=ON"
    Write-Host "INPA_REQUIRED=CLOSED"
    Write-Host "Do not start the engine during these fault phases."

    Read-RequiredConfirmation `
        "Connect COM5, turn ignition to position II, keep engine OFF."
    Invoke-LiveProbe -Mode capture -Action baseline -DurationSeconds 15

    Read-RequiredConfirmation `
        "Cable test: ignition ON, engine OFF. After ACTION_NOW unplug only the USB diagnostic cable."
    Invoke-LiveProbe -Mode expect-loss -Action cable_disconnect -DurationSeconds 90

    Read-RequiredConfirmation `
        "Reconnect the USB cable, wait for COM5, keep ignition ON and engine OFF."
    Invoke-LiveProbe -Mode capture -Action reconnect_after_cable -DurationSeconds 15

    Read-RequiredConfirmation `
        "Ignition test: cable connected, ignition ON, engine OFF. After ACTION_NOW turn ignition fully OFF."
    Invoke-LiveProbe -Mode expect-loss -Action ignition_off -DurationSeconds 90

    Read-RequiredConfirmation `
        "Turn ignition back to position II, wait a few seconds; engine remains OFF."
    Invoke-LiveProbe -Mode capture -Action reconnect_after_ignition -DurationSeconds 15

    $reports = Get-ChildItem $faultOutput `
        -Filter "bmw14.phase.acceptance.json" -File -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 5 |
        ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json }

    if (@($reports).Count -ne 5 -or
        @($reports | Where-Object result -ne "pass").Count -ne 0) {
        throw "The five BMW-14 phase reports did not all pass."
    }
    $epochs = @($reports | ForEach-Object epoch_id | Sort-Object -Unique)
    if ($epochs.Count -ne 5) {
        throw "Reconnect did not create five distinct acquisition epochs."
    }
    Write-Host "BMW14_LIVE_FAULT_MATRIX=PASS"
    Write-Host "DISTINCT_EPOCHS=5"
}

function Invoke-RoadTest {
    if (-not $ConfirmReadOnly) {
        throw "RoadTest requires -ConfirmReadOnly."
    }
    if (-not $ConfirmVehicleSafe) {
        throw "RoadTest blocked: -ConfirmVehicleSafe is required."
    }
    if (-not $ConfirmPassengerOperator) {
        throw "RoadTest blocked: -ConfirmPassengerOperator is required."
    }

    Write-Host "BMW-13 FINAL ROAD TEST"
    Write-Host "TARGET_DURATION_MINUTES=15"
    Write-Host "DRIVER_LAPTOP_INTERACTION=FORBIDDEN"
    Write-Host "PASSENGER_OPERATOR_REQUIRED=True"
    Write-Host "Normal legal driving only; no forced high load or actuator tests."
    Write-Host "In the GUI: Select approved (22), leave 1-5 Plot channels, Connect, Start."
    Write-Host "After at least 15 minutes: Stop, Disconnect, close the GUI."
    Read-RequiredConfirmation `
        "Vehicle is safe, passenger is ready, INPA is closed, cable is secured."

    $started = [datetime]::UtcNow
    Invoke-Checked `
        -FilePath "dotnet" `
        -Arguments @(
            "run", "--project", $appProject,
            "-c", "Release", "--no-build", "--",
            "--live", "--confirm-read-only",
            "--repo-root", $root,
            "--ecu-path", "C:\EDIABAS\ECU",
            "--port", "COM5"
        ) `
        -FailureMessage "BMW-14 live GUI exited with an error."

    $candidate = Get-ChildItem $roadOutput -Directory -ErrorAction SilentlyContinue |
        Where-Object LastWriteTimeUtc -ge $started |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw "No new BMW-13 GUI run directory was found."
    }
    Invoke-Analyze -Directory $candidate.FullName
}

function Invoke-Analyze {
    param([Parameter(Mandatory)][string]$Directory)
    Invoke-Checked `
        -FilePath "dotnet" `
        -Arguments @(
            "run", "--project", $analyzerProject,
            "-c", "Release", "--no-build", "--",
            "--run-directory", $Directory,
            "--expected-parameters", "22",
            "--minimum-seconds", "840",
            "--minimum-hz", "1.5"
        ) `
        -FailureMessage "BMW-13 road-test acceptance failed."
}

switch ($Phase) {
    "Offline" {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
            $offlineScript -RepositoryRoot $root
        if ($LASTEXITCODE -ne 0) {
            throw "Offline gate failed. Exit code: $LASTEXITCODE"
        }
    }
    "Faults" {
        Invoke-FaultSequence
    }
    "RoadTest" {
        Invoke-RoadTest
    }
    "AllLive" {
        Invoke-FaultSequence
        Invoke-RoadTest
    }
    "Analyze" {
        if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
            throw "Analyze requires -RunDirectory."
        }
        Invoke-Analyze -Directory ([IO.Path]::GetFullPath($RunDirectory))
    }
}
