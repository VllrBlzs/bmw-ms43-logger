[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "Offline",
        "EngineOff",
        "IdleCatalog",
        "IdleBenchmark",
        "StationaryAcceptance",
        "DisconnectProof"
    )]
    [string]$Phase,

    [switch]$ConfirmReadOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot

$solution = Join-Path $repoRoot "BmwMs43Logger.sln"
$stationaryProject = Join-Path $repoRoot `
    "tools\BmwMs43Logger.StationaryLogger\BmwMs43Logger.StationaryLogger.csproj"
$boundedProject = Join-Path $repoRoot `
    "tools\BmwMs43Logger.BoundedLiveProbe\BmwMs43Logger.BoundedLiveProbe.csproj"
$ediabasProject = Join-Path $repoRoot `
    "third_party\ediabaslib\EdiabasLib\EdiabasLib\EdiabasLib.csproj"
$catalogTest = Join-Path $repoRoot "scripts\test-bmw04-catalog.ps1"
$liveOutput = Join-Path $repoRoot "data\private\bmw08-live"
$boundedOutput = Join-Path $repoRoot "data\private\bmw03-live"
$dryOutput = Join-Path $repoRoot "artifacts\bmw08"

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$NativeArguments,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage,

        [int[]]$AllowedExitCodes = @(0)
    )

    & $FilePath @NativeArguments |
        ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "$FailureMessage Exit code: $exitCode"
    }
    return $exitCode
}

function Test-StationaryArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.DirectoryInfo]$RunDirectory,

        [Parameter(Mandatory = $true)]
        [ValidateSet("completed", "faulted")]
        [string]$ExpectedStatus,

        [string[]]$ExpectedFaultCodes = @()
    )

    $csvPath = Join-Path $RunDirectory.FullName "session.csv"
    $metadataPath = Join-Path $RunDirectory.FullName "session.meta.json"
    $reportPath = Join-Path $RunDirectory.FullName "session.acceptance.json"
    foreach ($requiredPath in @($csvPath, $metadataPath, $reportPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Missing BMW-08 artifact: $requiredPath"
        }
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw |
        ConvertFrom-Json
    $report = Get-Content -LiteralPath $reportPath -Raw |
        ConvertFrom-Json
    $rows = @(Import-Csv -LiteralPath $csvPath)

    if ($metadata.status -ne $ExpectedStatus) {
        throw "Unexpected metadata status: $($metadata.status)"
    }
    if ([long]$metadata.samples_written -ne $rows.Count) {
        throw "CSV row count does not match metadata."
    }
    if ([long]$metadata.samples_written -ne [long]$report.samples_observed) {
        throw "Acceptance sample count does not match metadata."
    }
    if ([long]$metadata.cycles_written -ne [long]$report.cycles_observed) {
        throw "Acceptance cycle count does not match metadata."
    }
    if (@($metadata.selected_parameter_ids).Count -ne
        [int]$report.planned_channels) {
        throw "Selected parameter count does not match the acceptance plan."
    }
    if ([long]$metadata.queue_dropped -ne [long]$report.queue_dropped -or
        [long]$metadata.write_errors -ne [long]$report.write_errors) {
        throw "Queue/write counters do not match between metadata and acceptance."
    }
    $qualityTotal = ($metadata.quality_counts.PSObject.Properties |
        Measure-Object -Property Value -Sum).Sum
    if ([long]$qualityTotal -ne $rows.Count) {
        throw "Quality counters do not add up to the CSV row count."
    }
    if ([bool]$metadata.privacy.vin_recorded -or
        [bool]$metadata.privacy.absolute_paths_recorded -or
        [bool]$metadata.privacy.licensed_descriptors_embedded) {
        throw "Session metadata violates the privacy contract."
    }
    if (-not [bool]$report.com_closed) {
        throw "The EDIABAS interface was not reported closed."
    }

    $actualHash = (Get-FileHash -LiteralPath $csvPath -Algorithm SHA256).Hash
    if ($actualHash -ne $metadata.csv_sha256) {
        throw "CSV SHA256 does not match metadata."
    }

    $previousCycle = [long]::MinValue
    $previousTicks = [long]::MinValue
    foreach ($row in $rows) {
        $cycle = [long]::Parse(
            $row.cycle_id,
            [Globalization.CultureInfo]::InvariantCulture)
        $ticks = [long]::Parse(
            $row.cycle_completed_monotonic_ticks,
            [Globalization.CultureInfo]::InvariantCulture)
        if ($cycle -lt $previousCycle -or $ticks -lt $previousTicks) {
            throw "CSV cycle/timestamp order is not monotonic."
        }
        $previousCycle = $cycle
        $previousTicks = $ticks
    }

    if ($ExpectedStatus -eq "completed") {
        if ($report.result -ne "PASS" -or $null -ne $report.failure_code) {
            throw "Completed BMW-08 run has a failed acceptance report."
        }
    }
    else {
        if ($report.result -ne "FAIL") {
            throw "Fault proof did not produce a failed acceptance report."
        }
        if ($ExpectedFaultCodes -notcontains [string]$report.failure_code) {
            throw "Unexpected fault code: $($report.failure_code)"
        }
        if ($rows.Count -eq 0) {
            throw "Disconnect proof contains no pre-fault samples."
        }
        $lastCycleId = ($rows |
            ForEach-Object { [long]$_.cycle_id } |
            Measure-Object -Maximum).Maximum
        $terminalFaultRows = @($rows | Where-Object {
            [long]$_.cycle_id -eq [long]$lastCycleId -and
            $_.quality -in @("timeout", "error", "missing")
        })
        if ($terminalFaultRows.Count -eq 0) {
            throw "The terminal disconnect cycle has no explicit invalid quality state."
        }
    }

    Write-Host "ARTIFACT_ROUNDTRIP=PASS"
    Write-Host "ARTIFACT_DIRECTORY=$($RunDirectory.FullName)"
}

function Test-All28CatalogObservation {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.DirectoryInfo]$RunDirectory
    )

    $reportPath = Join-Path $RunDirectory.FullName "session.acceptance.json"
    $report = Get-Content -LiteralPath $reportPath -Raw |
        ConvertFrom-Json
    if ($report.selection -ne "all28" -or
        [int]$report.planned_channels -ne 28 -or
        @($report.parameters).Count -ne 28) {
        throw "Catalog observation is not an all-28 run."
    }
    $incomplete = @($report.parameters | Where-Object {
        [long]$_.values_present -lt 1 -or
        @($_.observed_units).Count -lt 1 -or
        $null -eq $_.first_value -or
        $null -eq $_.last_value
    })
    if ($incomplete.Count -gt 0) {
        $ids = ($incomplete.parameter_id | Sort-Object) -join ", "
        throw "Live value/unit evidence is incomplete for: $ids"
    }
    Write-Host "ALL28_VALUE_UNIT_EVIDENCE=PASS"
}

function Invoke-Stationary {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("dry-run", "live")]
        [string]$Mode,

        [Parameter(Mandatory = $true)]
        [string]$Selection,

        [Parameter(Mandatory = $true)]
        [int]$DurationSeconds,

        [Parameter(Mandatory = $true)]
        [string]$VehicleState,

        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory,

        [long]$FaultCycle = 0,

        [int[]]$AllowedExitCodes = @(0)
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $existingRunPaths = @(Get-ChildItem -LiteralPath $OutputDirectory -Directory |
        ForEach-Object { $_.FullName })
    $nativeArguments = @(
        "run",
        "--project", $stationaryProject,
        "-c", "Release",
        "--no-build",
        "--",
        "--$Mode",
        "--selection", $Selection,
        "--vehicle-state", $VehicleState,
        "--duration-seconds", $DurationSeconds.ToString(
            [Globalization.CultureInfo]::InvariantCulture),
        "--requested-hz", "5.0",
        "--output-dir", $OutputDirectory,
        "--ecu-path", "C:\EDIABAS\ECU",
        "--port", "COM5"
    )
    if ($Mode -eq "live") {
        $nativeArguments += "--confirm-read-only"
    }
    if ($FaultCycle -gt 0) {
        $nativeArguments += @(
            "--inject-fault-cycle",
            $FaultCycle.ToString([Globalization.CultureInfo]::InvariantCulture)
        )
    }

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments $nativeArguments `
        -FailureMessage "BMW-08 $Mode run failed: $Selection" `
        -AllowedExitCodes $AllowedExitCodes | Out-Null

    $newRuns = @(Get-ChildItem -LiteralPath $OutputDirectory -Directory |
        Where-Object { $existingRunPaths -notcontains $_.FullName })
    if ($newRuns.Count -ne 1) {
        throw "Expected exactly one new BMW-08 run directory, found $($newRuns.Count)."
    }
    return $newRuns[0]
}

function Invoke-BoundedLive {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("engine-off", "idle")]
        [string]$Scenario,

        [Parameter(Mandatory = $true)]
        [ValidateSet("baseline", "core", "states", "extended")]
        [string]$Group,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 3)]
        [int]$Passes
    )

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments @(
            "run",
            "--project", $boundedProject,
            "-c", "Release",
            "--no-build",
            "--",
            "--live",
            "--confirm-read-only",
            "--scenario", $Scenario,
            "--group", $Group,
            "--passes", $Passes.ToString(
                [Globalization.CultureInfo]::InvariantCulture),
            "--output-dir", $boundedOutput,
            "--ecu-path", "C:\EDIABAS\ECU",
            "--port", "COM5"
        ) `
        -FailureMessage "BMW-03/04 bounded live run failed: $Scenario/$Group" |
        Out-Null
}

function Assert-LivePrerequisites {
    if (-not $ConfirmReadOnly) {
        throw "Live phases require -ConfirmReadOnly."
    }
    if (-not (Test-Path -LiteralPath $stationaryProject -PathType Leaf) -or
        -not (Test-Path -LiteralPath $boundedProject -PathType Leaf)) {
        throw "Missing project file. Complete the Offline phase first."
    }

    $blockedProcesses = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessName -match `
                '^(INPA|TOOL32|TestO|EdiabasTool|DeepOBD)$'
        })
    if ($blockedProcesses.Count -gt 0) {
        $names = ($blockedProcesses.ProcessName | Sort-Object -Unique) -join ", "
        throw "Close diagnostic applications before using COM5: $names"
    }

    $com5 = @(Get-CimInstance Win32_PnPEntity |
        Where-Object { $_.Name -match '\(COM5\)' })
    if ($com5.Count -ne 1 -or $com5[0].Status -ne "OK") {
        throw "Exactly one healthy COM5 device is required."
    }
    Write-Host "COM5=$($com5[0].Name)"

    $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $battery) {
        Write-Host "BATTERY_PERCENT=$($battery.EstimatedChargeRemaining)"
        if ([int]$battery.EstimatedChargeRemaining -lt 70) {
            Write-Warning `
                "Battery is below 70%. The full live plan needs about 25-30 minutes."
        }
    }

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments @(
            "run",
            "--project", $stationaryProject,
            "-c", "Release",
            "--no-build",
            "--",
            "--preflight",
            "--ecu-path", "C:\EDIABAS\ECU",
            "--port", "COM5"
        ) `
        -FailureMessage "BMW-08 no-communication preflight failed." |
        Out-Null
}

function Invoke-OfflinePhase {
    foreach ($path in @(
        $solution,
        $stationaryProject,
        $boundedProject,
        $ediabasProject,
        $catalogTest
    )) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required file is missing: $path"
        }
    }

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments @("sln", $solution, "add", $stationaryProject) `
        -FailureMessage "Could not add BMW-08 to the solution." |
        Out-Null

    $solutionProjects = (& dotnet sln $solution list) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list solution projects."
    }
    if ($solutionProjects -match `
        'third_party[\\/]ediabaslib[\\/]EdiabasLib[\\/]EdiabasLib[\\/]EdiabasLib.csproj') {
        Invoke-Native `
            -FilePath "dotnet" `
            -NativeArguments @("sln", $solution, "remove", $ediabasProject) `
            -FailureMessage "Could not remove the transitive EdiabasLib project." |
            Out-Null
    }

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments @("build", $stationaryProject, "-c", "Release") `
        -FailureMessage "BMW-08 Release build failed." |
        Out-Null

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments @("build", $boundedProject, "-c", "Release") `
        -FailureMessage "BMW-03/04 Release build failed." |
        Out-Null

    Invoke-Native `
        -FilePath "powershell.exe" `
        -NativeArguments @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $catalogTest
        ) `
        -FailureMessage "BMW-04 catalog test failed." |
        Out-Null

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments @(
            "run",
            "--project", $boundedProject,
            "-c", "Release",
            "--no-build",
            "--",
            "--preflight"
        ) `
        -FailureMessage "BMW-03/04 preflight failed." |
        Out-Null

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments @(
            "run",
            "--project", $stationaryProject,
            "-c", "Release",
            "--no-build",
            "--",
            "--preflight",
            "--ecu-path", "C:\EDIABAS\ECU",
            "--port", "COM5"
        ) `
        -FailureMessage "BMW-08 preflight failed." |
        Out-Null

    foreach ($selection in @(
        "rpm_only",
        "core4",
        "batch8",
        "core16",
        "all28"
    )) {
        $run = Invoke-Stationary `
            -Mode "dry-run" `
            -Selection $selection `
            -DurationSeconds 3 `
            -VehicleState "simulated" `
            -OutputDirectory $dryOutput
        Test-StationaryArtifacts `
            -RunDirectory $run `
            -ExpectedStatus "completed"
        if ($selection -eq "all28") {
            Test-All28CatalogObservation -RunDirectory $run
        }
    }

    $faultRun = Invoke-Stationary `
        -Mode "dry-run" `
        -Selection "all28" `
        -DurationSeconds 3 `
        -VehicleState "simulated" `
        -OutputDirectory $dryOutput `
        -FaultCycle 5
    Test-StationaryArtifacts `
        -RunDirectory $faultRun `
        -ExpectedStatus "faulted" `
        -ExpectedFaultCodes @("SIMULATED_TIMEOUT")

    Invoke-Native `
        -FilePath "dotnet" `
        -NativeArguments @("build", $solution, "-c", "Release") `
        -FailureMessage "Full solution build failed." |
        Out-Null

    # Final BMW-08 Release rebuild and hash-pinned preflights.
    Invoke-Native -FilePath "dotnet" -NativeArguments @("build", $stationaryProject, "-c", "Release", "--no-incremental") -FailureMessage "Final BMW-08 Release rebuild failed." | Out-Null
    Invoke-Native -FilePath "dotnet" -NativeArguments @("build", $boundedProject, "-c", "Release", "--no-incremental") -FailureMessage "Final BMW-03/04 Release rebuild failed." | Out-Null
    Invoke-Native -FilePath "dotnet" -NativeArguments @("run", "--project", $boundedProject, "-c", "Release", "--no-build", "--", "--preflight") -FailureMessage "Final BMW-03/04 hash preflight failed." | Out-Null
    Invoke-Native -FilePath "dotnet" -NativeArguments @("run", "--project", $stationaryProject, "-c", "Release", "--no-build", "--", "--preflight", "--ecu-path", "C:\EDIABAS\ECU", "--port", "COM5") -FailureMessage "Final BMW-08 hash preflight failed." | Out-Null

    Write-Host "BMW-08 OFFLINE PREPARATION"
    Write-Host "COMMUNICATION_ATTEMPTED=False"
    Write-Host "DRY_SELECTIONS=5"
    Write-Host "FAULT_HANDLING=PASS"
    Write-Host "RESULT=PASS"
}

if ($Phase -eq "Offline") {
    Invoke-OfflinePhase
    exit 0
}

Assert-LivePrerequisites

switch ($Phase) {
    "EngineOff" {
        Write-Host "STATE_REQUIRED=IGNITION_ON_ENGINE_OFF"
        Invoke-BoundedLive -Scenario "engine-off" -Group "baseline" -Passes 1
        Invoke-BoundedLive -Scenario "engine-off" -Group "core" -Passes 1
        $run = Invoke-Stationary `
            -Mode "live" `
            -Selection "all28" `
            -DurationSeconds 20 `
            -VehicleState "engine-off" `
            -OutputDirectory $liveOutput
        Test-StationaryArtifacts -RunDirectory $run -ExpectedStatus "completed"
    }
    "IdleCatalog" {
        Write-Host "STATE_REQUIRED=ENGINE_IDLING_STABLE"
        Invoke-BoundedLive -Scenario "idle" -Group "baseline" -Passes 1
        Invoke-BoundedLive -Scenario "idle" -Group "core" -Passes 3
        Invoke-BoundedLive -Scenario "idle" -Group "states" -Passes 1
        Invoke-BoundedLive -Scenario "idle" -Group "extended" -Passes 1
        $run = Invoke-Stationary `
            -Mode "live" `
            -Selection "all28" `
            -DurationSeconds 30 `
            -VehicleState "idle" `
            -OutputDirectory $liveOutput
        Test-StationaryArtifacts -RunDirectory $run -ExpectedStatus "completed"
        Test-All28CatalogObservation -RunDirectory $run
    }
    "IdleBenchmark" {
        Write-Host "STATE_REQUIRED=ENGINE_IDLING_STABLE"
        foreach ($selection in @(
            "rpm_only",
            "core4",
            "batch8",
            "core16",
            "all28"
        )) {
            $run = Invoke-Stationary `
                -Mode "live" `
                -Selection $selection `
                -DurationSeconds 20 `
                -VehicleState "idle" `
                -OutputDirectory $liveOutput
            Test-StationaryArtifacts `
                -RunDirectory $run `
                -ExpectedStatus "completed"
        }
    }
    "StationaryAcceptance" {
        Write-Host "STATE_REQUIRED=ENGINE_IDLING_STABLE"
        foreach ($attempt in 1..2) {
            Write-Host "START_STOP_ATTEMPT=$attempt"
            $run = Invoke-Stationary `
                -Mode "live" `
                -Selection "core4" `
                -DurationSeconds 30 `
                -VehicleState "idle" `
                -OutputDirectory $liveOutput
            Test-StationaryArtifacts `
                -RunDirectory $run `
                -ExpectedStatus "completed"
        }
        $longRun = Invoke-Stationary `
            -Mode "live" `
            -Selection "all28" `
            -DurationSeconds 900 `
            -VehicleState "idle" `
            -OutputDirectory $liveOutput
        Test-StationaryArtifacts `
            -RunDirectory $longRun `
            -ExpectedStatus "completed"
        Test-All28CatalogObservation -RunDirectory $longRun
    }
    "DisconnectProof" {
        Write-Host "STATE_REQUIRED=ENGINE_IDLING_STABLE"
        Write-Host "ACTION=Unplug the USB diagnostic cable 10 seconds after logging starts."
        Write-Host "ACTION=Do not switch ignition off during this proof."
        $run = Invoke-Stationary `
            -Mode "live" `
            -Selection "core4" `
            -DurationSeconds 60 `
            -VehicleState "idle" `
            -OutputDirectory $liveOutput `
            -AllowedExitCodes @(1)
        Test-StationaryArtifacts `
            -RunDirectory $run `
            -ExpectedStatus "faulted" `
            -ExpectedFaultCodes @("EDIABAS_TIMEOUT", "EDIABAS_ERROR")
        Write-Host "ACTION=Reconnect the cable and wait for COM5 to reappear."
    }
}

Write-Host "PHASE=$Phase"
Write-Host "RESULT=PASS"
