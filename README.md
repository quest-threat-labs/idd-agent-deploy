# idd-agent-deploy

Advanced local deployment for Identity Defense agents.

A standalone Windows utility that deploys the Quest Identity Defense Hybrid Audit Agent MSI
to Active Directory domain controllers, tracks which DCs received which version, and
captures per-target install results.

The authoritative specification is
[`docs/Hybrid_Audit_Agent_Deployment_Utility_PRD.md`](docs/Hybrid_Audit_Agent_Deployment_Utility_PRD.md).
Repository conventions and build environment are in [`CLAUDE.md`](CLAUDE.md).

**The targets are domain controllers — Tier 0 infrastructure.** Every design decision
favours predictability and legibility over speed or convenience.

## Build

Windows only. Not Linux, not macOS, not WSL — the solution needs the Windows Desktop
targeting pack, and `System.DirectoryServices.ActiveDirectory` plus the `msi.dll` interop
are Windows-only at runtime.

```powershell
dotnet build
dotnet test --filter "Category!=Integration"    # default test run
dotnet test --filter "Category=Integration"     # requires a lab forest; see below
```

Targets `net10.0-windows`, x64. Verify with `dotnet --list-sdks`.

## Layout

```
HybridAgentDeploy.slnx
├── src/
│   └── HybridAgentDeploy.Core/          # All logic. No UI references.
│       ├── Configuration/               # Path resolution, portable mode
│       ├── Deployment/                  # ITargetTransport, safety limits
│       ├── Discovery/                   # AD enumeration, text-file import
│       ├── Inventory/                   # SQLite schema, migrations, repositories
│       ├── Logging/                     # File sink for Microsoft.Extensions.Logging
│       ├── Models/
│       └── Msi/                         # MSI property extraction
└── tests/
    ├── HybridAgentDeploy.Core.Testing/  # SimulatedTransport, stub resolver
    ├── HybridAgentDeploy.Core.Tests/
    └── HybridAgentDeploy.Core.IntegrationTests/
```

`HybridAgentDeploy.Gui` (WinForms) and `HybridAgentDeploy.Cli` arrive in Phases 5 and 4.

## How the inventory works

**Active Directory enumeration is the only way a domain controller enters the inventory.**
Enumeration is forest-wide and records each DC's site, OS version, RODC flag, and global
catalog role. It is an explicit operator action — a button in the GUI, `enumerate` in the
CLI — and the tool warns when the inventory is empty or more than seven days old rather
than contacting AD on its own.

**Text-file import selects and tags; it does not discover.** A file names domain
controllers that are already in the inventory so they can be grouped for repeated
deployment — a pilot ring, a site, the read-only DCs. Entries may be written as an FQDN, a
NetBIOS name, or an IP address. An entry that matches no known DC is reported with its line
number and a suggestion to re-enumerate; it is never added to the inventory.

This departs from the literal wording of PRD §6.2, which implies import can create hosts.
Creating one yields a DC record with no site, no OS version, and an RODC flag defaulted to
false — a half-populated Tier 0 target assembled from a line of text. It also breaks the
§R7.2 site guard, which needs a real site to be meaningful. The reasoning is recorded on
`FileImportService`.

```
# pilot-ring.txt — first wave, agreed with the AD team
dc01.corp.local
dc02.corp.local
DC07              # NetBIOS names work too
```

## Test fixtures

MSI parsing tests need a real MSI. **Do not commit an MSI to this repository** — size and
licensing both prohibit it. Set `HAD_TEST_MSI_PATH` to a local copy:

```powershell
$env:HAD_TEST_MSI_PATH = 'C:\path\to\Quest Change Auditor Agent (x64).msi'
```

When it is unset those tests skip with a message naming the variable. Any valid MSI
exercises the parser; the real agent MSI is only needed to sanity-check property values.

### Lab tests

The `WinRmSmbTransport` tests need a domain controller and an account holding local
administrator rights on it. Point `HAD_TEST_DC` at one:

```powershell
$env:HAD_TEST_DC = 'dc01.corp.local'
dotnet test --filter "Category=Integration"
```

**These tests install nothing.** They exercise pre-flight, SMB staging, remote SHA-256
verification, retrieval, and cleanup; the execution tests run `cmd /c exit` and `cmd /c
echo`, which change nothing on the target. Teardown asserts the staging directory is gone,
so a run that litters a domain controller fails rather than passing quietly.

## Configuration

By default the inventory database and run logs live under
`%LOCALAPPDATA%\Quest\HybridAgentDeploy\`. To make an installation portable — NFR4 allows
running from a UNC path or removable media — place a `hybridagentdeploy.json` beside the
executable:

```json
{
  "databasePath": "data/inventory.db",
  "logRootPath": "data/logs"
}
```

Relative paths are anchored to the directory holding the config file, so the whole layout
travels together.

## Phase status

| Phase | Scope | State |
|---|---|---|
| 1 | Core foundation | Complete |
| 2 | Deployment orchestrator | Complete |
| 3 | `WinRmSmbTransport` | Complete (install path pending approval) |
| 4 | CLI | Not started |
| 5 | WinForms GUI | Not started |
| 6 | Packaging and signing | Not started |
