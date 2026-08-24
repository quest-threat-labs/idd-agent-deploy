# CLAUDE.md

Project-level instructions for Claude Code. Read this before doing anything else.

The full specification is `docs/Hybrid_Audit_Agent_Deployment_Utility_PRD.md`. That
document is authoritative for requirements. This file is authoritative for the build
environment and repo conventions. Where they appear to conflict, ask rather than guess.

---

## What this is

A standalone Windows utility that deploys the Quest Identity Defense Hybrid Audit Agent
MSI to Active Directory domain controllers, tracks which DCs received which version, and
captures per-target install results.

**The targets are domain controllers — Tier 0 infrastructure.** Every design decision
favours predictability and legibility over speed or convenience. When you face a choice
between a clever approach and an obvious one, take the obvious one.

---

## Build environment — do not deviate

| Fact | Value |
|---|---|
| Build OS | **Windows only.** Not Linux, not macOS, **not WSL.** |
| Target framework | `net<LTS>-windows` — run `dotnet --list-sdks` and use the highest installed LTS |
| Platform | x64 |
| UI framework | WinForms (`UseWindowsForms`) |
| Package source | nuget.org (`Microsoft.PowerShell.SDK` has a large transitive tree) |

**Why WSL will not work:** the solution requires the Windows Desktop targeting pack, which
Microsoft ships only on Windows. `System.DirectoryServices.ActiveDirectory` and the
WindowsInstaller COM interop are additionally Windows-only at runtime. If you find yourself
in a Linux environment, stop and say so rather than attempting workarounds, conditional
compilation, or cross-platform substitutes.

**Do not** introduce Avalonia, MAUI, WPF, or any cross-platform UI framework. Do not add
`<EnableWindowsTargeting>` or similar to coax a non-Windows build. Windows-only is a
deliberate constraint, not an oversight.

---

## Solution layout

```
HybridAgentDeploy.sln
├── src/
│   ├── HybridAgentDeploy.Core/     # All logic. No UI references.
│   ├── HybridAgentDeploy.Gui/      # WinForms. Presentation only.
│   └── HybridAgentDeploy.Cli/      # Console. Presentation only.
└── tests/
    ├── HybridAgentDeploy.Core.Tests/
    └── HybridAgentDeploy.Core.IntegrationTests/
```

### Architectural rules

1. **`HybridAgentDeploy.Core` MUST NOT reference `System.Windows.Forms`.** If you need a
   UI concern in Core, you have modelled something wrong. All orchestration, validation,
   and state transitions live in Core and are unit-testable without a window.
2. **All remote operations go through `ITargetTransport`.** No direct SMB, WinRM, or
   remoting calls anywhere else in the codebase. This is what makes the orchestrator
   testable without a domain.
3. The GUI and CLI are thin. If a code path can only be exercised through the UI, move it.

---

## Commands

```powershell
dotnet build
dotnet test --filter "Category!=Integration"    # default test run
dotnet test --filter "Category=Integration"     # requires a lab forest — see below
dotnet run --project src/HybridAgentDeploy.Cli
```

**Always exclude the `Integration` trait unless explicitly asked to run it.** Integration
tests require a live Active Directory lab with domain controllers, admin credentials, and
WinRM. They will fail on any normal dev machine. Those failures are environmental, not
bugs — do not attempt to "fix" them by changing production code.

---

## Phase discipline

Build in the order specified in PRD §16. Do not start a phase until the prior phase's
acceptance criteria pass.

| Phase | Scope | Needs a lab? |
|---|---|---|
| 1 | Core foundation: schema, repositories, MSI extraction, import, AD enumeration, `SimulatedTransport` | No |
| 2 | Deployment orchestrator: concurrency, circuit breaker, retries, exit-code mapping | No |
| 3 | `WinRmSmbTransport` — the real remote path | **Yes** |
| 4 | CLI | Lab for end-to-end verification |
| 5 | WinForms GUI | Lab for end-to-end verification |
| 6 | Packaging and signing | Clean VM |

**Phases 1 and 2 are fully developable on a standalone laptop.** The orchestrator is built
and tested entirely against `SimulatedTransport` and should have no idea whether a real
domain controller exists. Preserve that property.

---

## Test fixtures

MSI parsing tests need a real MSI file. **Do not commit an MSI to this repo** — size and
licensing both prohibit it.

Read the path from the `HAD_TEST_MSI_PATH` environment variable. When it is unset, skip
those tests with an explicit message naming the variable. Any valid MSI exercises the
parser; the real Change Auditor agent MSI is only needed to sanity-check property values.

---

## Non-negotiable behaviours

These exist for safety reasons and must survive refactoring. If a change would weaken one,
stop and raise it.

- **Concurrency ceiling of 5.** Encode as a compile-time constant. Clamp all input to it.
  There must be no configuration, environment variable, or CLI flag that raises it. A
  concurrency slot releases only after the **entire** per-target sequence completes —
  pre-flight through cleanup — not when msiexec returns.
- **Stage the MSI locally on each target, then execute locally.** Never run
  `msiexec /i \\unc\path` inside a remoting session. The local-staging design exists to
  avoid the Kerberos double-hop problem and is the most important decision in the tool.
  Do not "simplify" it into a UNC execution.
- **Never enable, offer, or implement CredSSP.** Local staging makes it unnecessary, and
  enabling it against a domain controller is a security regression.
- **Verify the SHA-256 of the staged MSI on the target before executing.**
- **Never auto-retry a failed install** except exit code 1618 (another installation in
  progress), which retries up to 3 times with backoff.
- **The Org ID is untrusted input** concatenated into an elevated command on a Tier 0 host.
  Build command lines with proper argument quoting, never string interpolation.
- **Never log credentials** in any form. Account names only.

---

## Error handling expectations

Every failure surfaced to an operator must state which stage failed, which DC, and the
next diagnostic step.

Not acceptable: `Deployment failed`

Acceptable: `Staging failed on DC01.corp.local: access denied writing to
\\DC01.corp.local\C$\Windows\Temp — confirm the running account holds local administrator
rights on this DC`

msiexec exit codes map per PRD §10.1. Notably: `3010` and `1641` are **successes**
(reboot pending, almost certainly from an unrelated cause — this agent does not require a
reboot). `1638` means a version is already installed and should be surfaced distinctly
rather than lumped in with generic failures.

---

## Style

- Async all the way through the I/O path. No blocking calls on the UI thread.
- `CancellationToken` on every async method that can run long.
- Deterministic disposal of COM objects — a leaked `WindowsInstaller.Installer` holds a
  file lock on the MSI.
- UTC in storage and logs; local time only in the UI, and labelled when shown.
- Explicit over clever. This code will be read by people auditing what ran against their
  domain controllers.

---

## Scope discipline

Do not build features not in the PRD. Explicitly out of scope for v1: uninstall/rollback,
post-install agent health checks, non-DC targets, GPO or SCCM integration, scheduling,
multi-forest support, cross-platform support, self-update, credential storage.

If a requirement seems missing, check PRD §17 (open questions) — it may have a documented
default. Mark any code written against an unresolved question with:

```csharp
// PRD-OPEN-Q: Q1 — cloud mode toggle vs hardcoded SG=1
```

so it stays greppable.
