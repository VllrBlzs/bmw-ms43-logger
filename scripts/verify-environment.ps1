[CmdletBinding()]
param(
    [string]$OutputPath = "artifacts/BMW01_environment.json"
)

$ErrorActionPreference = "Stop"

$os = Get-CimInstance Win32_OperatingSystem
$ports = @(
    Get-CimInstance Win32_PnPEntity |
        Where-Object { $_.Name -match "\(COM\d+\)" } |
        ForEach-Object {
            [ordered]@{
                name = [string]$_.Name
                manufacturer = [string]$_.Manufacturer
                status = [string]$_.Status
            }
        }
)

$configs = @(
    foreach ($path in @(
        "C:\EDIABAS\BIN\EDIABAS.INI",
        "C:\EDIABAS\BIN\OBD.INI"
    )) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [ordered]@{
                path = $path
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                relevant_lines = @(
                    [System.IO.File]::ReadAllLines($path) |
                        Where-Object {
                            $_ -match "^\s*(Interface|Port|Hardware|Retry|EcuPath|ObdComPort)\s*="
                        } |
                        ForEach-Object { [string]$_.Trim() }
                )
            }
        }
    }
)

$files = @(
    foreach ($path in @(
        "C:\EDIABAS\BIN\API32.DLL",
        "C:\EDIABAS\BIN\API64.DLL",
        "C:\EDIABAS\BIN\IFHSRV32.EXE",
        "C:\EC-APPS\INPA\BIN\INPALOAD.EXE",
        "C:\EC-APPS\EDIABAS\ECU\ms430ds0.prg",
        "C:\EC-APPS\EDIABAS\ECU\D_MOTOR.grp"
    )) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path
            [ordered]@{
                path = $path
                size_bytes = [long]$item.Length
                file_version = [string]$item.VersionInfo.FileVersion
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            }
        }
    }
)

$result = [ordered]@{
    captured_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
    windows = [ordered]@{
        caption = [string]$os.Caption
        version = [string]$os.Version
        architecture = [string]$os.OSArchitecture
    }
    process_64_bit = [Environment]::Is64BitProcess
    dotnet_sdk = [string](& dotnet --version)
    com_ports = $ports
    configs = $configs
    files = $files
}

$absoluteOutput = Join-Path (Get-Location) $OutputPath
$outputDirectory = Split-Path -Parent $absoluteOutput
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$json = $result | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText(
    $absoluteOutput,
    $json,
    [System.Text.UTF8Encoding]::new($false)
)

Write-Host "Environment evidence written to: $absoluteOutput"
Write-Host "Bytes: $((Get-Item -LiteralPath $absoluteOutput).Length)"
