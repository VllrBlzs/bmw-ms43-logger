# BMW-04B – live semantic review

## Decision

The 15-minute driving capture is a strong **transport and logging PASS**. It is not a blanket diagnostic verdict for every decoded field.

- 60,480/60,480 rows were recorded as transport-valid.
- 2,160/2,160 cycles contain all 28 selected parameters.
- The CSV SHA-256 matches session metadata.
- The acceptance report says `PASS`, COM5 was closed, and queue/write losses were zero.
- 22 parameter mappings have sufficient live-observation evidence for the logger UI.
- 6 mappings stay hidden from the normal approved list until their semantics are confirmed.

The word `valid` in the CSV means that the exact EDIABAS result was present and converted. It does **not** mean that a value is physically plausible, correctly named for end users, or proof of a vehicle defect.

## Withheld mappings

| Parameter | Observation | Decision | Required proof |
| --- | --- | --- | --- |
| `coolant.outlet.temperature` | Constant `-48 GradC` | Hold; sentinel-like, but not a sensor diagnosis | Dedicated `STATUS_KUEHLW_AUSL_TEMPERATUR`, INPA comparison, and separate DME fault-memory read |
| `ignition.cylinder1.advance` | Constant `72 GradKW` | Hold; convention/scaling unconfirmed | Dedicated `STATUS_ZUENDWINKEL` plus INPA comparison under two operating states |
| `idle.integrator` | Mostly near zero but reaches the 98 range | Hold; likely signed/wrapped semantics are possible | Dedicated `STATUS_LL_INTEGRATOR` and definition/convention check |
| `vanos.intake.actual.angle` | Batch field constant at `120 GradKW` | Hold dynamic claim | Dedicated intake IST/SOLL/REF trace during an RPM change |
| `vanos.exhaust.actual.angle` | Batch field constant at `-105 GradKW` | Hold dynamic claim | Dedicated exhaust IST/SOLL/REF trace during an RPM change |
| `cooling.fan.ac.request.stage` | Constant `5.078`, runtime unit `%` although the ID says “stage” | Hold naming/meaning | Dedicated result comparison and parameter rename if it is a percentage request |

At idle, the already captured dedicated VANOS jobs returned intake actual/target `120/120 GradKW` and exhaust actual/target `-105/-105 GradKW`. That observation contradicts a direct claim that the actuators were proven stuck; it still does not provide a dynamic VANOS proof.

## Comparison with the external AI analysis

| External claim | Review |
| --- | --- |
| Dataset dimensions, ranges, four constant fields | Correct calculation |
| Fixed VANOS values prove a mechanical/solenoid/sensor problem | Not proven. The batch mapping may be a default, endpoint, or unsuitable dynamic source; a dedicated IST/SOLL trace is needed. |
| `72 GradKW` is physically impossible and therefore an ignition fault | Not proven. The BMW result convention and scaling have not been validated. |
| `-48 GradC` proves an open/short radiator outlet sensor | Possible hypothesis only. Confirm with the dedicated job, INPA and fault memory. |
| Idle MAF proves a MAF/vacuum issue | Not supported by this mixed warm-up/road dataset alone. Accessory load and operating state are confounders. |
| Idle integrator near 98 proves control saturation | Unsupported until signed/wrapped encoding is resolved. |
| Higher knock-channel 2 mean proves mechanical noise or timing retard | Unsupported. Raw channel magnitudes over mixed RPM/load are not directly comparable as a fault test. |
| Lambda bank integrators track closely | Correct observation, but it does not independently rule out every intake or mixture issue. |
| Charging voltage and main coolant temperature look coherent | Reasonable descriptive observation, not a full component health test. |

## Project consequence

BMW-03, BMW-05 and BMW-08 can be closed from the collected evidence. BMW-04 is usable with a six-parameter semantic hold. BMW-09 may proceed because the GUI can expose only approved/live-observed parameters and visibly label pending ones; no automatic automotive diagnosis is added to v1.

## Run the audit in the repository

Copy `audit-bmw04-live-semantics.ps1` to `scripts\audit-bmw04-live-semantics.ps1`, then run:

```powershell
Set-Location "C:\Users\Valler\Desktop\Biznisz\BMW"

$run = ".\data\private\bmw08-driving\bmw08_live_driving_all28_20261008_163344_917_9b7607"
$idleExtended = ".\data\private\bmw03-live\bmw03_idle_extended_20261008_165639.json"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\audit-bmw04-live-semantics.ps1" `
  -RunDirectory $run `
  -IdleExtendedReport $idleExtended
```

Expected terminal decision:

```text
TRANSPORT_RESULT=PASS
PARAMETERS=28
VALIDATED_LIVE_OBSERVATION=22
WITHHELD=6
RESULT=PARTIAL_SEMANTIC_HOLD
```

`PARTIAL_SEMANTIC_HOLD` is an intentional safe outcome, not a failed logger run.
