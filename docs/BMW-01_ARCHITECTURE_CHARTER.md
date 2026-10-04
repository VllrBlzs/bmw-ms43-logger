# BMW-01 architecture charter

**Status:** completed and locally build-verified  
**Scope:** one BMW E39 525i, M54B25, 2001 facelift, MS43 DME  
**Platform:** Windows 11 x64, C#/.NET 10

## Objective

Build a local Windows application that presents every reviewed and practically
available MS43 live motor parameter, lets the user select channels, and writes
timestamped CSV plus session metadata. The first release supports this one
vehicle/backend/SGBD profile only.

## Architecture decision

Use the managed EdiabasLib implementation directly and pin it to release
`binaries_20260607`, commit
`947dfbbc686290625e3fc9cad044e69af64d8fe9` before the first backend build.
Do not P/Invoke the installed BMW `API32.DLL`/`API64.DLL`, replace the existing
INPA installation, or implement BMW protocols from scratch.

Reasons:

- direct access to `.PRG/.GRP` interpretation and the FTDI/COM transport;
- one managed architecture instead of a Python/native/bitness bridge;
- a narrow internal adapter can hide general EDIABAS capabilities;
- the dependency and exact source revision can be reproduced and reviewed.

Python is not part of the v1 runtime. Later offline analysis may consume CSV,
but it remains separate from vehicle acquisition.

## Data path

```mermaid
flowchart LR
    UI["GUI / PoC"] --> API["Parameter-ID API"]
    API --> Guard["Approved profile guard"]
    Guard --> Adapter["Internal Ediabas adapter"]
    Adapter --> Port["FTDI COM5"]
    Port --> DME["MS43 DME"]
```

The UI and future logger never see job names or raw arguments. Only the adapter
may translate an approved parameter ID into one exact job/argument/result tuple.

## Component boundaries

| Component | Responsibility | Forbidden responsibility |
| --- | --- | --- |
| Core | Parameter IDs, samples, quality states, public acquisition interface | SGBD jobs, COM details, raw payloads |
| Profile loader | Validate schema, approval state, hashes, backend pin | Editing or auto-approving entries |
| Ediabas adapter | Execute exact approved read tuples through one worker | General job runner, write/reset/active test |
| Sampling/logger | Timing, freshness, quality, queues, CSV/metadata | Treating missing data as zero |
| GUI | Selection, state, live values, later plots | Direct backend access |

## Fixed runtime and bitness decision

- Build target: `net10.0-windows`.
- Development SDK baseline: `10.0.401`, roll forward only within the installed
  .NET 10 patch line.
- Development and release process: 64-bit Windows.
- PoC uses framework-dependent execution; publishing and self-contained
  packaging are deferred until BMW-15.

## Local configuration decision

- Transport: `STD:OBD`.
- Adapter baseline: FTDI `USB Serial Port (COM5)`.
- ECU descriptor baseline: local `ms430ds0.prg` with the SHA-256 recorded in
  `THIRD_PARTY_NOTICES.md`.
- Runtime path values stay in ignored local configuration/environment variables.
- The installed INPA configuration is evidence only; the logger does not edit
  it.

## Dependency and distribution decision

EdiabasLib is GPLv3. Private use is the immediate target. Any conveyed combined
application must meet the applicable GPLv3 obligations. BMW ECU descriptors and
BMW Standard Tools binaries are local-only inputs because redistribution rights
have not been established. They are excluded by `.gitignore` and from every
release package.

## Read-only design rule

`IReadOnlyAcquisition.ReadAsync` accepts parameter IDs only. A generic
`ExecuteJob`, raw-payload, shell, process, reset, clear, adaptation, coding, or
actuator interface must not exist in public or GUI code. See
`READ_ONLY_POLICY.md` and `config/read_only_allowlist.schema.json`.

## BMW-01 evidence

- Windows/bitness/runtime decision recorded.
- Existing EDIABAS/INPA versions, COM5 FTDI adapter, SGBD paths, and hashes
  recorded.
- Backend/revision and dependency route selected.
- Read-only boundary and forbidden operations recorded.
- Public parameter-ID-only API scaffolded.
- Hash-bound draft profile and schema added.
- Reproducible BMW-02 plan added.

## Remaining BMW-G0 evidence

BMW-01 does not prove communication with the vehicle. BMW-02 must identify the
connected DME/SGBD profile and read a reviewed RPM value for at least five
minutes. Until then the project remains at the pre-communication gate.
