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
dotnet test --filter "Category=Integration"     # requires a domain-joined machine
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

## Test fixtures

MSI parsing tests need a real MSI. **Do not commit an MSI to this repository** — size and
licensing both prohibit it. Set `HAD_TEST_MSI_PATH` to a local copy:

```powershell
$env:HAD_TEST_MSI_PATH = 'C:\path\to\Quest Change Auditor Agent (x64).msi'
```

When it is unset those tests skip with a message naming the variable. Any valid MSI
exercises the parser; the real agent MSI is only needed to sanity-check property values.

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
| 3 | `WinRmSmbTransport` | Not started |
| 4 | CLI | Not started |
| 5 | WinForms GUI | Not started |
| 6 | Packaging and signing | Not started |
