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
│   ├── HybridAgentDeploy.Core/          # All logic. No UI references.
│   │   ├── Configuration/               # Path resolution, portable mode
│   │   ├── Deployment/                  # Orchestrator, transports, safety limits
│   │   ├── Discovery/                   # AD enumeration, text-file import
│   │   ├── Inventory/                   # SQLite schema, migrations, repositories
│   │   ├── Logging/                     # File sink, per-run run.log writer
│   │   ├── Models/
│   │   └── Msi/                         # MSI property extraction
│   └── HybridAgentDeploy.Cli/           # Console. Thin — presentation only.
└── tests/
    ├── HybridAgentDeploy.Core.Testing/  # SimulatedTransport, stub resolver
    ├── HybridAgentDeploy.Core.Tests/
    ├── HybridAgentDeploy.Cli.Tests/
    └── HybridAgentDeploy.Core.IntegrationTests/
```

`HybridAgentDeploy.Gui` (WinForms) arrives in Phase 5.

## CLI

```
hybridagentdeploy enumerate         [--domain <fqdn>] [--tag <name>]... [--user <account>]
hybridagentdeploy import            --file <path> [--tag <name>]... [--dry-run]
hybridagentdeploy list              [--tag <name>] [--site <name>] [--format table|csv|json]
hybridagentdeploy inspect-msi       --msi <path> [--format table|json]
hybridagentdeploy test-connectivity [--tag <name>]... [--host <fqdn>]... [--all]
hybridagentdeploy deploy            --msi <path> --org-id <id>
                                    [--tag <name>]... [--host <fqdn>]... [--all]
                                    [--no-cloud-mode] [--max-parallel 1-5]
                                    [--timeout-minutes 5-60] [--log-dir <path>]
                                    [--confirm] [--user <account>] [--use-https]
                                    [--format table|csv|json]
hybridagentdeploy history           [--run <guid>] [--host <fqdn>] [--format table|csv|json]
```

`deploy` refuses to run without `--confirm` when more than one DC is selected, which is what
stops a mistyped script pushing to an entire forest.

**Exit codes:** `0` all succeeded · `1` completed with failures · `2` halted by the circuit
breaker · `3` invalid arguments or pre-flight failure · `4` cancelled.

**Streams:** command output goes to stdout; progress, warnings, and errors go to stderr —
always, in every format. `--format json` on stdout is therefore parseable even while the
command is reporting progress.

**Credentials:** `--user DOMAIN\user` prompts for the password, or reads it from stdin when
redirected. There is deliberately no `--password` option: an argument is visible in process
listings, shell history, and script transcripts. Omitting `--user` uses the current identity.

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

One test does install the agent — the Phase 3 acceptance test in `EndToEndDeploymentTests`.
It is gated on `HAD_ALLOW_INSTALL=yes` in addition to `HAD_TEST_DC`, `HAD_TEST_MSI_PATH`,
and `HAD_TEST_ORG_ID`, so neither a default run nor an ordinary lab run can trigger it by
accident. Enable it only against a DC that may have software installed on it.

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
| 3 | `WinRmSmbTransport` | Complete |
| 4 | CLI | Complete (live deploy acceptance run deferred) |
| 5 | WinForms GUI | Not started |
| 6 | Packaging and signing | Not started |
