# Read-only policy

## Safety invariant

The application may send only a reviewed request that is present in an
approved, hash-bound profile. Every other operation is rejected before a
backend call is made.

Read-only means approved diagnostic read requests. It does not mean passive bus
sniffing, and it does not make an arbitrary EDIABAS job safe.

## Public API boundary

The public acquisition API accepts only stable `ParameterId` values. It has no
method that accepts any of the following:

- SGBD/PRG filename;
- EDIABAS job name;
- job argument or binary argument;
- result name;
- raw telegram or payload;
- executable, script, or shell command.

The internal adapter resolves a parameter ID through one approved profile. A
missing parameter ID, draft profile, file-hash mismatch, backend-version
mismatch, unapproved argument, or unapproved result list causes a hard failure
before dispatch.

## Permanently forbidden application operations

- coding or programming;
- ECU flash or firmware update;
- adaptation reset or service reset;
- fault-memory clearing;
- actuator or active tests;
- calibration or learned-value changes;
- arbitrary job execution;
- raw telegram/payload execution;
- subprocess or shell execution;
- runtime editing of the allowlist.

These operations are outside v1 and cannot be enabled by a hidden setting or a
future GUI toggle.

## Approval workflow

1. Identify the exact local SGBD file and calculate SHA-256.
2. Inspect job metadata offline. A name or `STATUS_` prefix is not evidence of
   read-only behavior.
3. Record one exact tuple: SGBD hash, job, argument bytes/string, requested
   results, types, units, and review evidence.
4. Keep the profile in `draft` state while any field is uncertain.
5. Change it to `approved` only after offline review and a controlled stationary
   test.
6. Review any changed SGBD hash or backend commit as a new profile.

There is no bulk "execute every job" discovery against the vehicle.

## Runtime controls

- One acquisition worker owns the interface.
- INPA, TestO, Tool32, and the logger do not use the adapter concurrently.
- The SGBD hash is checked before the port is opened.
- The backend release/commit must match the profile.
- Result names not listed in the operation are ignored and reported.
- A timeout, missing value, stale value, or backend error is never converted to
  numeric zero.
- Reconnect creates a new acquisition epoch; old values are not emitted as new.
- VIN and local user paths are excluded from normal logs and demos.

## Change control

Any new parameter requires a profile change and review. Code review must reject
public methods containing general job execution, raw arguments, raw payloads,
process launch, or mutable allowlist entry points.
