# BMW MS43 read-only logger

Windows-only, single-vehicle logger for a 2001 BMW E39 525i with M54B25 and
MS43 DME.

The v1 application will expose only approved parameter IDs, show live values,
and write timestamped CSV data. It will not expose ECU jobs, raw payloads,
coding, programming, resets, fault clearing, actuator tests, or shell/process
execution.

## Current state

- BMW-01: architecture and read-only boundary completed and locally build-verified.
- BMW-02: real MS43 connection and five-minute RPM proof of concept pending.
- No ECU dispatch exists in this repository yet.

## Fixed platform decision

- Windows 11 x64
- C# and .NET 10
- Direct managed EdiabasLib backend, pinned before use
- Local FTDI K+DCAN adapter on the configured COM port
- Locally installed BMW ECU descriptor files; these are never committed or
  redistributed

See `docs/BMW-01_ARCHITECTURE_CHARTER.md` and
`docs/BMW-02_POC_PLAN.md` before adding communication code.

## Build the safe scaffold

```powershell
dotnet new sln -n BmwMs43Logger --format sln
dotnet sln .\BmwMs43Logger.sln add .\src\BmwMs43Logger.Core\BmwMs43Logger.Core.csproj
dotnet sln .\BmwMs43Logger.sln add .\src\BmwMs43Logger.Poc\BmwMs43Logger.Poc.csproj
dotnet build .\BmwMs43Logger.sln
dotnet run --project .\src\BmwMs43Logger.Poc\BmwMs43Logger.Poc.csproj
```

The PoC executable currently prints its safety state and exits. It does not
open a COM port.

<!-- BMW15_STATUS_START -->
## Validated project status

- **BMW-13 official road test:** PASS - 1114.83 s, 22 approved parameters, 59048 rows, 100.00% valid, 2.408 Hz effective cycle rate.
- **BMW-14 live fault/recovery matrix:** PASS - cable loss/recovery and ignition loss/recovery, five distinct acquisition epochs, engine off.
- **BMW-15 final QA:** PASS - offline regression, Release build and diff check.

The release scope is a read-only BMW MS43 logger. Dispatch is restricted to a
pinned allowlist of status jobs with empty arguments; unknown and actuator/write
requests are rejected. See [BMW-15 final QA](docs/BMW-15_FINAL_QA.md) for evidence and limits.
<!-- BMW15_STATUS_END -->
