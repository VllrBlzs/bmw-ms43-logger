# BMW-03 – MS43 read-only job/result review

## Result

- Source profile: `ms430ds0.prg` / `8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523`
- Backend: `A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59`
- Communication attempted during inventory: **no**
- Jobs reviewed: **230**
- Live validated: **1** (`STATUS_MOTORDREHZAHL`)
- Offline-reviewed live candidates pending bounded car proof: **85**
- Excluded: **144**
- Candidate jobs with arguments: **0**
- Metadata errors: **0**

BMW-03 is complete at the offline-review level. Only the RPM tuple is admitted to the normal runtime allowlist. The 85 candidates remain non-dispatchable until a bounded live test explicitly promotes them.

## Safety classification

- State-changing, reset, actuator, programming, active-test and diagnostic-session-control jobs stay excluded.
- Fault-memory, identification, calibration, arbitrary-memory and security jobs are outside the v1 live-data surface even when technically read-only.
- `STATUS_SYSTEMCHECK_*` and VANOS measurement-result jobs are excluded because their semantics depend on active tests that v1 will not initiate.
- A STATUS prefix alone was not accepted as proof. Candidate admission required zero arguments plus typed result metadata and manual scope review.
- Manual review removed 42 automatic conditional classifications that were static/raw diagnostics, active-test-dependent results or otherwise outside the v1 live-data surface.
- The first automatic classifier falsely rejected 17 read-only status jobs because words such as `ADAPTION` appeared in comments or result names. Manual review recovered them as conditional candidates without promoting them to runtime-approved status.

## Bounded live-test pack

The next car visit is limited to the exact empty-argument tuples in `ms430ds0-bounded-live-test-pack.json`. It covers:

- validated RPM baseline;
- the 28-value `STATUS_MESSWERTEBLOCK` batch;
- digital switch/state inputs;
- additive and multiplicative fuel adaptations for both banks;
- intake/exhaust VANOS actual/target/reference positions;
- pre-cat and post-cat lambda signals for both banks.

No arbitrary job name, argument or result request is accepted. This pack is approval for a bounded test, not general runtime support.

## BMW-04 handoff

- `bmw04-parameter-catalog.raw.csv`: 253 typed raw result candidates, including aliases and duplicates.
- `bmw04-core-parameter-catalog.draft.csv`: 28 curated core parameters from the measurement block; RPM uses its already validated individual source.
- `ms430ds0-parameter-catalog.draft.json`: machine-readable BMW-04 draft with source, type, unit state, scaling state, availability and support evidence.
- `bmw04-catalog-coverage.json`: explicit raw/core/approved/pending coverage counts.
- Units remain `runtime_metadata` until observed on the car. They are not guessed from names or comments.

## Recovered conditional jobs

- `STATUS_ADAP_AKTIV`
- `STATUS_ADD`
- `STATUS_ADD_2`
- `STATUS_DIGITAL`
- `STATUS_DK_ADAP_BEDARF`
- `STATUS_GEBERRAD_ADAPTION`
- `STATUS_KR`
- `STATUS_LAMBDA_ADD_1`
- `STATUS_LAMBDA_ADD_2`
- `STATUS_LAMBDA_MUL_1`
- `STATUS_LAMBDA_MUL_2`
- `STATUS_LL_ADAPTION`
- `STATUS_LL_FS_ADAPTION`
- `STATUS_LL_KK_ADAPTION`
- `STATUS_MUL`
- `STATUS_MUL_2`
- `STATUS_VANOS_NW_FLANKENADAPTION`
