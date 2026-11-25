# BMW-15 - final QA and release evidence

## Accepted evidence

- BMW-13 official road test: **PASS**
- Duration: **1114.83 seconds**
- Approved live parameters: **22**
- Acquisition cycles: **2684**
- Logged rows: **59048**
- Valid rows: **100.00%**
- Effective cycle rate: **2.408 Hz**
- CSV hash, schema round-trip, coverage, cadence, road dynamics and privacy: **PASS**
- BMW-14 live fault/recovery matrix: **PASS**
- Fault/recovery phases: **5**
- Distinct acquisition epochs: **5**
- Cable loss/recovery and ignition loss/recovery: **PASS**
- Engine state during BMW-14: **OFF**
- Offline regression gate: **PASS**
- Final Release build: **PASS**
- Working-tree diff check: **PASS**

## Safety boundary

The application is a read-only MS43 data logger. Live dispatch is restricted to
the pinned status-job allowlist with empty arguments. Unknown jobs, unknown
parameters and actuator/write requests are rejected.

## Validated scope

BMW-13 proves the already completed live GUI and real-road logging path. BMW-14
proves bounded transport failure handling, CSV/metadata finalization, interface
release and clean reconnection into new acquisition epochs. BMW-15 adds no new
vehicle test; it closes the regression, documentation and release gates.

## Known limits

- 22 catalog parameters are approved for live use; withheld parameters remain
  excluded until their semantics are separately validated.
- The logger is not a substitute for BMW fault-memory diagnosis or mechanical
  inspection.
- Effective acquisition rate depends on the selected EDIABAS jobs and transport
  latency.

## Evidence hashes

- BMW-13 acceptance report SHA-256: B96F0A73B7427BF08E79D64F8971DBA460537D001D6613A2F2DFE30BE84DFF34
- BMW-14 matrix report SHA-256: F97062B5FEF774BDD471B9EF7DA0CC00F1E409E4A3B7A9A91A3DD3BD655BAB04
