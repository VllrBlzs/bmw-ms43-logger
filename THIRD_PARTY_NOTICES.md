# Third-party and local-only inputs

## EdiabasLib

- Upstream: https://github.com/uholeschak/ediabaslib
- Planned release pin: `binaries_20260607`
- Planned commit pin: `947dfbbc686290625e3fc9cad044e69af64d8fe9`
- Upstream license: GNU General Public License version 3
- Status in BMW-01: not bundled and not yet linked

The application is private/local first. If a combined application is conveyed,
the release must satisfy the applicable GPLv3 source, notice, and license
obligations. BMW-15 rechecks the final dependency graph before release.

## BMW Standard Tools and ECU descriptors

The local installation provides EDIABAS binaries and `.PRG`/`.GRP` ECU
descriptors. Their redistribution rights have not been established. Therefore:

- no BMW binary or ECU descriptor is committed;
- no BMW file is placed in a release archive;
- local paths are supplied through ignored runtime configuration;
- the approved profile records filename and SHA-256 only.

Known local descriptor hashes at BMW-01:

- `ms430ds0.prg`: `8C4714D638DE8E9D765A86EEC9746495883366D21126A454AB2EF28279E3A523`
- `D_MOTOR.grp`: `F5646D3147A8E96D6A4C9B40EAE746F8F1A0D23E0A7897F94C2583790BBAC73A`

These hashes identify the local files; they do not grant redistribution rights
and do not prove that the connected vehicle uses the selected SGBD variant.
