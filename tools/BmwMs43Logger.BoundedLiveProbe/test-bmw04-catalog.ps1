[CmdletBinding()]
param(
    [string]$EcuPath = "C:\EDIABAS\ECU",
    [string]$BackendPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot "config\ms430ds0-parameter-catalog.draft.json"
$packPath = Join-Path $repoRoot "config\ms430ds0-bounded-live-test-pack.json"

if ([string]::IsNullOrWhiteSpace($BackendPath)) {
    $BackendPath = Join-Path $repoRoot (
        "tools\BmwMs43Logger.BoundedLiveProbe\bin\Release\" +
        "net10.0-windows10.0.26100.0\EdiabasLib.dll"
    )
}

function Require-Equal {
    param(
        [Parameter(Mandatory)][AllowEmptyString()]$Actual,
        [Parameter(Mandatory)][AllowEmptyString()]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    if ($Actual -cne $Expected) {
        throw "$Label mismatch. Expected '$Expected', actual '$Actual'."
    }
}

function Require-FileHash {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label not found: $Path"
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    Require-Equal $actual $Expected "$Label SHA256"
}

if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    throw "Catalog not found: $catalogPath"
}
if (-not (Test-Path -LiteralPath $packPath -PathType Leaf)) {
    throw "Bounded live-test pack not found: $packPath"
}

$catalogText = [System.IO.File]::ReadAllText($catalogPath)
$packText = [System.IO.File]::ReadAllText($packPath)
$catalog = $catalogText | ConvertFrom-Json
$pack = $packText | ConvertFrom-Json

$expectedSgbd = "8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523"
$expectedBackend = "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59"
$expectedPack = "AB953E495EEBB294C17E1C6735D5B173C70E28BC4F8A19B19D15323BF97B8D55"
$expectedJobs = @(
    "STATUS_MOTORDREHZAHL",
    "STATUS_MESSWERTEBLOCK",
    "STATUS_DIGITAL",
    "STATUS_LAMBDA_ADD_1",
    "STATUS_LAMBDA_ADD_2",
    "STATUS_LAMBDA_MUL_1",
    "STATUS_LAMBDA_MUL_2",
    "STATUS_VANOS_NW_LAGE_IST_SOLL_REF_EINLASS",
    "STATUS_VANOS_NW_LAGE_IST_SOLL_REF_AUSLASS",
    "STATUS_LS_VKAT_SIGNAL_1",
    "STATUS_LS_VKAT_SIGNAL_2",
    "STATUS_LS_NKAT_SIGNAL_1",
    "STATUS_LS_NKAT_SIGNAL_2"
)

Require-Equal $catalog.schema_version "bmw04.parameter_catalog.v1" "Catalog schema"
Require-Equal $catalog.catalog_state "draft_pending_bounded_live_validation" "Catalog state"
Require-Equal $catalog.sgbd_sha256 $expectedSgbd "Catalog SGBD"
Require-Equal $catalog.backend_sha256 $expectedBackend "Catalog backend"
Require-Equal $catalog.unknown_parameter_policy "reject" "Unknown parameter policy"
Require-Equal $pack.schema_version "bmw03.bounded_live_test_pack.v1" "Pack schema"
Require-Equal $pack.profile_state "controlled_test_only" "Pack state"
Require-Equal $pack.sgbd_sha256 $expectedSgbd "Pack SGBD"
Require-Equal $pack.backend_sha256 $expectedBackend "Pack backend"
Require-Equal $pack.argument_policy "exact_empty_string_only" "Pack argument policy"
Require-Equal $pack.unknown_dispatch_policy "reject" "Unknown dispatch policy"

$normalizedPack = $packText.Replace("`r`n", "`n").Replace("`r", "`n")
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $packBytes = [System.Text.Encoding]::UTF8.GetBytes($normalizedPack)
    $packHashBytes = $sha.ComputeHash($packBytes)
    $packHash = [System.BitConverter]::ToString($packHashBytes).Replace("-", "")
}
finally {
    $sha.Dispose()
}
Require-Equal $packHash $expectedPack "Bounded pack semantic SHA256"

if ($catalog.entries.Count -ne 28) {
    throw "Expected 28 core catalog entries, found $($catalog.entries.Count)."
}

$idPattern = "^[a-z][a-z0-9]*(\.[a-z0-9]+)+$"
$seenIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($entry in $catalog.entries) {
    if ($entry.parameter_id -cnotmatch $idPattern) {
        throw "Invalid parameter ID: $($entry.parameter_id)"
    }
    if (-not $seenIds.Add([string]$entry.parameter_id)) {
        throw "Duplicate parameter ID: $($entry.parameter_id)"
    }
    Require-Equal $entry.data_type "real" "Data type for $($entry.parameter_id)"
    Require-Equal $entry.source.argument "" "Source argument for $($entry.parameter_id)"
    if ([string]::IsNullOrWhiteSpace([string]$entry.source.job) -or
        [string]::IsNullOrWhiteSpace([string]$entry.source.value_result) -or
        [string]::IsNullOrWhiteSpace([string]$entry.source.unit_result)) {
        throw "Incomplete source tuple for $($entry.parameter_id)."
    }
}

$approved = @($catalog.entries | Where-Object support_status -CEQ "approved")
$pending = @($catalog.entries | Where-Object support_status -CEQ "candidate_live_validation")
if ($approved.Count -ne 1 -or $pending.Count -ne 27) {
    throw "Unexpected support split: approved=$($approved.Count), pending=$($pending.Count)."
}
Require-Equal $approved[0].parameter_id "engine.rpm" "Approved parameter"
Require-Equal $approved[0].unit.state "validated_live" "RPM unit state"
Require-Equal $approved[0].unit.value "1/min" "RPM unit"
foreach ($entry in $pending) {
    Require-Equal $entry.unit.state "pending_runtime_observation" "Pending unit state"
    if ($null -ne $entry.unit.value) {
        throw "Pending unit was guessed for $($entry.parameter_id)."
    }
}

if ($pack.entries.Count -ne $expectedJobs.Count) {
    throw "Expected $($expectedJobs.Count) bounded jobs, found $($pack.entries.Count)."
}
for ($index = 0; $index -lt $expectedJobs.Count; $index++) {
    $entry = $pack.entries[$index]
    Require-Equal $entry.job $expectedJobs[$index] "Pack job $index"
    Require-Equal $entry.argument "" "Pack argument for $($entry.job)"
    if ($entry.result_names.Count -lt 2 -or $entry.result_names[0] -cne "JOB_STATUS") {
        throw "Invalid result allowlist for $($entry.job)."
    }
    if ($entry.job -cmatch "^(STEUERN_|START_|STOP_|TEST_)" -or
        $entry.job -cmatch "(LOESCHEN|PROGRAMMIEREN|SCHREIBEN|RUECKSETZEN|VERSTELLEN)") {
        throw "State-changing job entered the bounded pack: $($entry.job)"
    }
}

$sgbdPath = Join-Path $EcuPath "ms430ds0.prg"
Require-FileHash $sgbdPath $expectedSgbd "SGBD"
Require-FileHash $BackendPath $expectedBackend "EdiabasLib runtime assembly"

Write-Output "BMW-04 PARAMETER CATALOG PREFLIGHT"
Write-Output "COMMUNICATION_ATTEMPTED=False"
Write-Output "INTERFACE_INSTANCE=None"
Write-Output "CATALOG_ENTRIES=28"
Write-Output "APPROVED=1"
Write-Output "PENDING_LIVE_VALIDATION=27"
Write-Output "BOUNDED_TEST_JOBS=13"
Write-Output "PACK_SEMANTIC_SHA256=$expectedPack"
Write-Output "ALL_ARGUMENTS_EMPTY=True"
Write-Output "UNKNOWN_PARAMETER_POLICY=reject"
Write-Output "UNKNOWN_DISPATCH_POLICY=reject"
Write-Output "SGBD_SHA256=$expectedSgbd"
Write-Output "BACKEND_SHA256=$expectedBackend"
Write-Output "RESULT=PASS"
