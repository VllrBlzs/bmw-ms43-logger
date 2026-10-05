# BMW-02A offline MS43 metadata probe

This developer tool reads metadata from the already installed and hash-pinned
`ms430ds0.prg`. It does not create an EDIABAS interface object and cannot open
a serial port.

## Safety boundary

- exact SGBD SHA-256 is required before parsing;
- exact observed .NET 10 EdiabasLib assembly SHA-256 is required;
- `NoInitForVJobs` is set to `true`;
- `EdInterfaceClass` must remain `null` before and after every call;
- only `_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS`, and `_RESULTS` are accepted;
- only RPM-like job names are shortlisted;
- job count, candidate count, result count, value length, and total report size
  are bounded;
- no normal ECU job is selected or intentionally dispatched.

The shortlist is evidence for manual review. It is not an approved acquisition
profile and must not be copied into an `approved` profile without reviewing the
exact job, argument, result name, type, unit, and comments.

## Run

From the repository root:

```powershell
dotnet sln .\BmwMs43Logger.sln add .\tools\BmwMs43Logger.MetadataProbe\BmwMs43Logger.MetadataProbe.csproj
.\scripts\run-bmw02a-offline-metadata-probe.ps1
```

The bounded report is written under
`artifacts\bmw02a\ms430ds0-rpm-metadata.txt`; `artifacts` remains ignored by
Git. The same report is printed so it can be pasted into the project chat.
