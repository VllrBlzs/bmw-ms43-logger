# BMW-02 controlled MS43/RPM proof of concept

## Goal

Prove real, repeatable communication with the user's MS43 and acquire one
reviewed RPM parameter for at least five minutes. This is the BMW-G0 hardware
gate, not broad parameter discovery.

## Preconditions

- Vehicle stationary in a ventilated location.
- Healthy vehicle battery; laptop powered sufficiently for the complete test.
- K+DCAN appears as FTDI COM5.
- INPA can connect using the current known setup, then INPA is closed.
- No TestO, Tool32, INPA, or other process owns COM5 during the PoC.
- EdiabasLib source is pinned to the charter commit.
- The installed INPA/BMW files are not replaced or patched.

## Execution sequence

1. Add the pinned EdiabasLib source under `third_party/ediabaslib` without
   committing BMW files.
2. Build only the necessary managed EdiabasLib project and record the produced
   assembly hash.
3. Create an ignored local EdiabasLib configuration for `STD:OBD`, COM5, trace
   output, and `C:\EC-APPS\EDIABAS\ECU`.
4. Inspect `ms430ds0.prg` job/result metadata offline.
5. Select exact identification and RPM read tuples. Do not approve based only on
   a job name or prefix.
6. Add only those reviewed tuples to a profile, record their evidence, then set
   the profile state to `approved`.
7. Make the PoC adapter verify profile state, SGBD SHA-256, backend pin, exact
   arguments, and exact requested results before opening COM5.
8. Read DME identification once and compare it with the expected MS43 profile.
9. Read RPM repeatedly for at least five minutes using one acquisition worker.
10. Record UTC time, monotonic time, cycle number, acquisition duration, value,
    unit, quality, timeout count, and backend errors.
11. Close/flush the run and confirm INPA can reconnect afterwards.

## Explicit exclusions

- no fault clearing or adaptation reset;
- no actuator/VANOS test;
- no coding/programming;
- no job enumeration by executing every job;
- no raw telegrams;
- no road test;
- no parameter-catalog expansion before this gate passes.

## GO criteria

- DME identification supports the expected MS43/SGBD profile.
- RPM is plausible and continuously readable for at least five minutes.
- Timeout/error is represented as a non-valid quality state, never zero.
- COM5 has one owner and closes cleanly.
- The run is reproducible from written commands/configuration.
- No operation outside the approved read tuples is dispatched.

## NO-GO criteria

- wrong or unconfirmed ECU/SGBD profile;
- unstable communication after bounded configuration/driver checks;
- required operation cannot be established as read-only;
- the PoC requires protocol reverse engineering or changing the installed INPA
  environment.

If the three-hour target is consumed by connection debugging, BMW-03 does not
start. A bounded follow-up or a documented project stop is preferable to
silently broadening scope.
