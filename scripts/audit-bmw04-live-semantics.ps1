[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunDirectory,

    [string]$IdleExtendedReport,

    [string]$OutputDirectory = ".\artifacts\bmw04b"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$invariant = [Globalization.CultureInfo]::InvariantCulture
$csvPath = Join-Path $RunDirectory "session.csv"
$metadataPath = Join-Path $RunDirectory "session.meta.json"
$acceptancePath = Join-Path $RunDirectory "session.acceptance.json"

foreach ($path in @($csvPath, $metadataPath, $acceptancePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required evidence file is missing: $path"
    }
}

function Convert-ToInvariantDouble {
    param([Parameter(Mandatory = $true)][string]$Text)

    $value = 0.0
    $style = [Globalization.NumberStyles]::Float -bor `
        [Globalization.NumberStyles]::AllowThousands
    if (-not [double]::TryParse($Text, $style, $invariant, [ref]$value)) {
        throw "Not an invariant floating-point value: '$Text'"
    }
    return $value
}

function Get-Median {
    param([Parameter(Mandatory = $true)][double[]]$Values)

    if ($Values.Count -eq 0) { return $null }
    [Array]::Sort($Values)
    $middle = [int]($Values.Count / 2)
    if (($Values.Count % 2) -eq 1) { return $Values[$middle] }
    return ($Values[$middle - 1] + $Values[$middle]) / 2.0
}

function Get-IdleExtendedEvidence {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Idle extended report is missing: $Path"
    }

    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $values = [ordered]@{}
    foreach ($observation in @($document.observations)) {
        foreach ($result in @($observation.results)) {
            if ($result.present -and $result.value_kind -in @("Double", "Int64")) {
                $values[$result.name] = $result.value
            }
        }
    }

    return [ordered]@{
        report = [IO.Path]::GetFileName($Path)
        intake_actual = $values["STAT_VANOS_NW_LAGE_EINLASS_IST_WERT"]
        intake_target = $values["STAT_VANOS_NW_LAGE_EINLASS_SOLL_WERT"]
        intake_reference = $values["STAT_VANOS_NW_LAGE_EINLASS_REF_WERT"]
        exhaust_actual = $values["STAT_VANOS_NW_LAGE_AUSLASS_IST_WERT"]
        exhaust_target = $values["STAT_VANOS_NW_LAGE_AUSLASS_SOLL_WERT"]
        exhaust_reference = $values["STAT_VANOS_NW_LAGE_AUSLASS_REF_WERT"]
        lambda_add_bank1_ms = $values["STAT_LAMBDA_ADD_1_WERT"]
        lambda_add_bank2_ms = $values["STAT_LAMBDA_ADD_2_WERT"]
        lambda_mul_bank1_percent = $values["STAT_LAMBDA_MUL_1_WERT"]
        lambda_mul_bank2_percent = $values["STAT_LAMBDA_MUL_2_WERT"]
    }
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$acceptance = Get-Content -LiteralPath $acceptancePath -Raw | ConvertFrom-Json
$rows = @(Import-Csv -LiteralPath $csvPath)

$csvSha256 = (Get-FileHash -LiteralPath $csvPath -Algorithm SHA256).Hash
$hashMatches = $csvSha256 -eq [string]$metadata.csv_sha256
$rowCountMatches = $rows.Count -eq [long]$metadata.samples_written
$cycleGroups = @($rows | Group-Object cycle_id)
$cycleCountMatches = $cycleGroups.Count -eq [long]$metadata.cycles_written
$expectedChannels = @($metadata.selected_parameter_ids).Count
$completeCycleCount = @(
    $cycleGroups | Where-Object { $_.Count -eq $expectedChannels }
).Count
$allCyclesComplete = $completeCycleCount -eq $cycleGroups.Count
$qualityCounts = [ordered]@{}
foreach ($group in @($rows | Group-Object quality | Sort-Object Name)) {
    $qualityCounts[$group.Name] = $group.Count
}
$allRowsValid = $qualityCounts.Count -eq 1 -and `
    $qualityCounts.Contains("valid") -and `
    $qualityCounts["valid"] -eq $rows.Count
$acceptancePassed = [string]$acceptance.result -eq "PASS" -and `
    [bool]$acceptance.com_closed -and `
    [long]$acceptance.queue_dropped -eq 0 -and `
    [long]$acceptance.write_errors -eq 0

$holdRules = @{
    "coolant.outlet.temperature" = [ordered]@{
        status = "withheld_semantic_validation"
        reason = "Constant -48 GradC is sentinel-like. It does not by itself prove a failed vehicle sensor."
        next_proof = "Compare the dedicated zero-argument STATUS_KUEHLW_AUSL_TEMPERATUR result with INPA and separately read DME faults."
    }
    "ignition.cylinder1.advance" = [ordered]@{
        status = "withheld_semantic_validation"
        reason = "Constant 72 GradKW requires source convention/scaling confirmation."
        next_proof = "Compare the dedicated zero-argument STATUS_ZUENDWINKEL result with INPA over idle and a controlled RPM change."
    }
    "idle.integrator" = [ordered]@{
        status = "withheld_semantic_validation"
        reason = "Values near 98 percent coexist with a low median and may use a wrapped signed representation."
        next_proof = "Confirm the signed convention from the dedicated STATUS_LL_INTEGRATOR result before applying thresholds."
    }
    "vanos.intake.actual.angle" = [ordered]@{
        status = "withheld_dynamic_validation"
        reason = "The batch value stayed at 120 GradKW; the idle dedicated job matched actual to target, but no dedicated dynamic trace exists."
        next_proof = "Log IST, SOLL and REF from the dedicated intake VANOS job during a controlled RPM sweep."
    }
    "vanos.exhaust.actual.angle" = [ordered]@{
        status = "withheld_dynamic_validation"
        reason = "The batch value stayed at -105 GradKW; the idle dedicated job matched actual to target, but no dedicated dynamic trace exists."
        next_proof = "Log IST, SOLL and REF from the dedicated exhaust VANOS job during a controlled RPM sweep."
    }
    "cooling.fan.ac.request.stage" = [ordered]@{
        status = "withheld_naming_validation"
        reason = "The field name says stage while runtime metadata reports percent, and the value stayed constant."
        next_proof = "Confirm the dedicated job's meaning and rename the parameter if it is a percentage request."
    }
}

$parameterReports = @()
foreach ($parameterGroup in @($rows | Group-Object parameter_id | Sort-Object Name)) {
    [double[]]$values = @(
        $parameterGroup.Group | ForEach-Object {
            Convert-ToInvariantDouble ([string]$_.value)
        }
    )
    $units = @(
        $parameterGroup.Group.unit |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    $distinctValues = @(
        $parameterGroup.Group.value | Sort-Object -Unique
    ).Count
    $rule = $holdRules[$parameterGroup.Name]
    $status = if ($null -eq $rule) {
        "validated_live_observation"
    } else {
        $rule.status
    }

    $parameterReports += [ordered]@{
        parameter_id = $parameterGroup.Name
        samples = $parameterGroup.Count
        units = $units
        minimum = ($values | Measure-Object -Minimum).Minimum
        median = Get-Median $values
        maximum = ($values | Measure-Object -Maximum).Maximum
        distinct_values = $distinctValues
        constant = $distinctValues -eq 1
        semantic_status = $status
        reason = if ($null -eq $rule) {
            "Runtime presence, type, unit and live variation/operating-state response were observed. This is not a vehicle-health diagnosis."
        } else {
            $rule.reason
        }
        next_proof = if ($null -eq $rule) { $null } else { $rule.next_proof }
    }
}

$withheld = @(
    $parameterReports | Where-Object {
        $_.semantic_status -like "withheld_*"
    }
)
$observed = @(
    $parameterReports | Where-Object {
        $_.semantic_status -eq "validated_live_observation"
    }
)
$transportPass = $hashMatches -and $rowCountMatches -and `
    $cycleCountMatches -and $allCyclesComplete -and `
    $allRowsValid -and $acceptancePassed

$report = [ordered]@{
    schema_version = "bmw04.live_semantic_audit.v1"
    generated_utc = [DateTimeOffset]::UtcNow.ToString("O", $invariant)
    source = [ordered]@{
        session_id = [string]$metadata.session_id
        csv_file = [IO.Path]::GetFileName($csvPath)
        metadata_file = [IO.Path]::GetFileName($metadataPath)
        acceptance_file = [IO.Path]::GetFileName($acceptancePath)
        csv_sha256 = $csvSha256
        catalog_version = [string]$metadata.catalog_version
        catalog_sha256 = [string]$metadata.catalog_sha256
        sgbd_sha256 = [string]$metadata.sgbd_sha256
        backend_sha256 = [string]$metadata.backend_sha256
    }
    transport_verification = [ordered]@{
        result = if ($transportPass) { "PASS" } else { "FAIL" }
        hash_matches_metadata = $hashMatches
        rows = $rows.Count
        rows_match_metadata = $rowCountMatches
        cycles = $cycleGroups.Count
        cycles_match_metadata = $cycleCountMatches
        channels_per_complete_cycle = $expectedChannels
        complete_cycles = $completeCycleCount
        all_cycles_complete = $allCyclesComplete
        quality_counts = $qualityCounts
        all_rows_valid = $allRowsValid
        acceptance_passed_and_com_closed = $acceptancePassed
        queue_dropped = [long]$acceptance.queue_dropped
        write_errors = [long]$acceptance.write_errors
    }
    catalog_decision = [ordered]@{
        result = if ($transportPass -and $withheld.Count -gt 0) {
            "PARTIAL_SEMANTIC_HOLD"
        } elseif ($transportPass) {
            "PASS"
        } else {
            "FAIL"
        }
        total_parameters = $parameterReports.Count
        validated_live_observation = $observed.Count
        withheld = $withheld.Count
        note = "Transport-valid means present and parsed. It does not itself prove physical meaning or vehicle health."
    }
    idle_extended_evidence = Get-IdleExtendedEvidence $IdleExtendedReport
    interpretation_guards = @(
        "Do not diagnose a VANOS fault from constant batch angles without a dedicated dynamic IST/SOLL/REF trace.",
        "Do not diagnose a radiator outlet sensor fault from -48 GradC until a dedicated result and DME fault memory agree.",
        "Do not apply idle-integrator thresholds until its signed/wrapped representation is confirmed.",
        "Do not compare knock-sensor channel means as cylinder health or ignition-retard evidence.",
        "Do not diagnose MAF or a vacuum leak from mixed-drive averages alone; use operating-state matched evidence and dedicated adaptations."
    )
    parameters = $parameterReports
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputPath = Join-Path $OutputDirectory "bmw04-live-semantic-audit.json"
$report | ConvertTo-Json -Depth 8 | Set-Content `
    -LiteralPath $outputPath `
    -Encoding UTF8

Write-Host "BMW-04B LIVE SEMANTIC AUDIT"
Write-Host "TRANSPORT_RESULT=$($report.transport_verification.result)"
Write-Host "ROWS=$($report.transport_verification.rows)"
Write-Host "CYCLES=$($report.transport_verification.cycles)"
Write-Host "PARAMETERS=$($report.catalog_decision.total_parameters)"
Write-Host "VALIDATED_LIVE_OBSERVATION=$($report.catalog_decision.validated_live_observation)"
Write-Host "WITHHELD=$($report.catalog_decision.withheld)"
foreach ($item in $withheld) {
    Write-Host "HOLD=$($item.parameter_id)|$($item.semantic_status)"
}
Write-Host "REPORT=$([IO.Path]::GetFullPath($outputPath))"
Write-Host "RESULT=$($report.catalog_decision.result)"

if (-not $transportPass) { exit 1 }
exit 0
