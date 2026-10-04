# BMW-01 environment baseline

Captured on 2026-10-04.

| Item | Observed value |
| --- | --- |
| Operating system | Microsoft Windows 11 Pro, build `10.0.26200`, 64-bit |
| PowerShell process | 64-bit |
| .NET SDK | `10.0.401` |
| Existing runtimes before SDK install | .NET/Windows Desktop 6.0.36, 7.0.20, 8.0.31 |
| BMW API32/API64 | file version `7.3.0` |
| IFHSRV32 | file version `3.9.0.0` |
| INPA | file version `5.0.6` |
| Interface | `STD:OBD` |
| Configured adapter | `COM5`, `USB`, retry on |
| Enumerated adapter | `USB Serial Port (COM5)`, FTDI, status OK |
| Active configured EcuPath | `C:\EC-APPS\EDIABAS\ECU` |

The same `ms430ds0.prg` and `D_MOTOR.grp` contents were found under both
`C:\EDIABAS\ECU` and `C:\EC-APPS\EDIABAS\ECU`; their hashes matched.

The existing BMW Standard Tools installation remains untouched. The logger will
use its own ignored runtime configuration and will never replace the installed
`API32.DLL`, `API64.DLL`, `EDIABAS.INI`, or `OBD.INI`.
