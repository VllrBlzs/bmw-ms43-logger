# BMW-05 – offline polling planner checkpoint

## Scope

This checkpoint implements the planner without opening an interface or executing an
EDIABAS job. It does not claim a measured ECU cadence.

The planner:

- accepts stable `ParameterId` values only;
- rejects unknown parameters;
- rejects conditional parameters in normal runtime mode;
- permits them only in explicit controlled-test planning mode;
- groups selected parameters by exact reviewed job and argument tuple;
- uses the complete reviewed result allowlist from the pinned bounded test pack;
- removes duplicate parameter selections;
- emits an ordered plan for one serial acquisition worker.

With the current 28-entry core draft, all 27 measurement-block candidates collapse to
one `STATUS_MESSWERTEBLOCK` request. RPM remains on its already validated individual job,
so selecting all 28 parameters produces two sequential jobs, not 28 requests.

## Benchmark boundary

The offline benchmark is a deterministic virtual cost model. Its milliseconds and Hz are
test-fixture values used to compare plan shapes; they are not measured communication
performance and must not appear in product claims.

BMW-05 remains open until the car test measures cycle time, effective Hz and latency for
representative channel selections using the same serial plans.
