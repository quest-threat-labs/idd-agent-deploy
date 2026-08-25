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
│   │   ├── Logging/                     # File sink, per-run run.log and validation.log
│   │   ├── Models/
│   │   └── Msi/                         # MSI property extraction
│   ├── HybridAgentDeploy.Cli/           # Console. Thin — presentation only.
│   └── HybridAgentDeploy.Gui/           # WinForms. Thin — presentation only.
│       ├── Presentation/                # Testable: filters, selection, colours, form state
│       └── Views/                       # The five screens of §8
└── tests/
    ├── HybridAgentDeploy.Core.Testing/  # SimulatedTransport, stub resolver
    ├── HybridAgentDeploy.Core.Tests/
    ├── HybridAgentDeploy.Cli.Tests/
    ├── HybridAgentDeploy.Gui.Tests/
    └── HybridAgentDeploy.Core.IntegrationTests/
```

GUI layout is defined in code, not in generated `.Designer.cs` files, so diffs stay
reviewable and there is no designer round-trip to corrupt. The Visual Studio designer will
not render these forms — edit the C#. Logic where a defect could hide (filtering, selection
across filter changes, outcome colours, whether a run may start) lives in `Presentation/`
and is unit tested; the forms are thin wiring over it.

The manual test matrix that forms the Phase 5 acceptance criterion is in
[`docs/manual-test-matrix.md`](docs/manual-test-matrix.md).

## CLI

```
hadeploy enumerate         [--domain <fqdn>] [--tag <name>]... [--user <account>]
hadeploy import            --file <path> [--tag <name>]... [--dry-run]
hadeploy list              [--tag <name>] [--site <name>] [--format table|csv|json]
hadeploy inspect-msi       --msi <path> [--format table|json]
hadeploy test-connectivity [--tag <name>]... [--host <fqdn>]... [--all]
hadeploy deploy            --msi <path> --org-id <id>
                           [--tag <name>]... [--host <fqdn>]... [--all]
                           [--no-cloud-mode] [--max-parallel 1-5]
                           [--timeout-minutes 5-60] [--log-dir <path>]
                           [--confirm] [--user <account>] [--use-https]
                           [--format table|csv|json]
hadeploy history           [--run <guid>] [--host <fqdn>] [--format table|csv|json]
```

The CLI is `hadeploy.exe`, not `hybridagentdeploy.exe`. Both programs ship in one folder, and
`hybridagentdeploy.exe` and the GUI's `HybridAgentDeploy.exe` are the same filename on
Windows — publishing both silently leaves only one of them. The GUI keeps the descriptive
name because it is double-clicked; the CLI takes the short one because it is typed.

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

## Validating before you deploy

The Deploy tab has a **Validate targets** button beside Start. It runs the whole per-target
sequence a deployment runs — pre-flight, stage, execute, clean up — minus the one step that
changes the machine, and reports the result on the Progress tab exactly as a deployment
does. On each selected domain controller it:

- confirms DNS, SMB, and WinRM answer;
- writes a 4 KB file of random bytes to `C:\Windows\Temp\HybridAgentDeploy\validate-{guid}`
  and verifies its SHA-256 on the way back;
- opens a remoting session and runs `cmd /c exit 0`;
- reads the Windows build number, and reports anything below Server 2016 as not ready —
  the MSI refuses those outright through a launch condition;
- reads which agent is installed, from both registry views, and which product it reports to;
- deletes the file.

Nothing is installed, the package is never copied to a target, and msiexec is never run.
`ValidationRunner` contains no code path able to execute an installer, which is deliberate:
a flag on the deployment orchestrator would have put "do not install" one boolean away from
"install" on a Tier 0 host.

**Why an active probe rather than a port check.** Reaching TCP 445 proves a port is open,
not that the account may open a session; a readable `C$` proves nothing about writing to it.
Both pass happily on a domain controller that then fails at the staging step — after a 67 MB
installer has already been sent to it.

**It predicts what a deployment would do.** Comparing the installed agent's version against
the selected package yields a fresh install, an upgrade, a reinstall of the same build, or a
downgrade the installer will refuse with 1638. The last of those shows amber rather than
green: nothing is wrong with the host, but it is not a target for this package.

**It catches a mode change, which a version comparison alone cannot.** See below — an
upgrade that moves an agent between the two products changes which one it reports to, or
fails to move it at all, regardless of which version is newer. The mode outranks the version
in the verdict for that reason.

**Pacing is identical to a deployment's** — the ceiling of five (R7.1) and the site guard
(R7.2), which the Progress tab now states in words, because a run deliberately held at two
at a time otherwise looks like a hung one. The circuit breaker is deliberately *not*
applied: it exists to stop a bad deployment part-way through a forest, and an operator
validating sixty controllers wants all of the broken ones, not the first three.

An Org ID is not required — no installer runs, so there is nothing for one to configure.

**No deployment history is recorded.** A validation writes `validation.log` and
`results.csv` into its own timestamped folder suffixed `-validate`, and leaves
`deployment_run` untouched. Nothing was deployed; a history that said otherwise would
misreport what ran against the customer's domain controllers.

## The two agent modes

The agent installs in one of two modes, and the **Cloud mode (SG=1)** checkbox picks which:

| Checkbox | msiexec | Reports to | The identifier is |
|---|---|---|---|
| On | `SG=1` | Identity Defense (cloud) | the SMP Organization ID — a GUID |
| Off | `SG` omitted entirely | Change Auditor (on-premises) | the installation name, e.g. `DEFAULT` |

Deploying in Change Auditor mode is **out of scope for v1** — it is untested against a
Change Auditor server — but nothing in the tool forecloses it.

The field is labelled **Quest SMP Organization ID \ Change Auditor Installation Name**,
because it is a different thing in each mode and "Org ID" alone told an operator working
against on-premises Change Auditor nothing about what belonged in it. It still reaches the
installer as `INSTALLATION_NAME`, and the CLI flag is still `--org-id`.

**The tool remembers it, one value per mode.** Stored in `app_setting` in the inventory
database — beside the controllers it relates to, so a portable copy carries it along, and two
forests worked out of two folders never prefill each other's. Remembering a single value
would have been wrong: switching the checkbox would leave the other product's kind of
identifier in the box, and the mismatch warning would then fire on a value the tool itself
had just supplied. It is written when a run starts rather than when one succeeds, because a
failed deployment is exactly when the operator is about to try again. Nothing secret goes in
there — this identifier is already recorded in `deployment_run` and written into every run
log, and SEC1 credential material is still never persisted anywhere.

**Cloud mode off omits `SG` rather than passing `SG=0`.** These are not interchangeable: the
MSI branches on `NOT SG`, and in Windows Installer that means "undefined or empty", so
`SG=0` is truthy and takes a different path from omission. The installer's own cloud-mode
condition is `SG AND (SG="1")`, which omission satisfies correctly. Sending `SG=0` has not
been tested against a Change Auditor server, so the tool sends nothing rather than something
unverified to a Tier 0 host.

**Moving an existing agent between the two is asymmetric.** Per the agent's developer
documentation:

| Installed | Deployed with | Result |
|---|---|---|
| Change Auditor | `SG=1` | **Migrates** to Identity Defense. Supported, and one-way. |
| Identity Defense | `SG` omitted | Does **not** move back. The run leaves it on Identity Defense. |

Validation reads the installed mode from
`HKLM\SOFTWARE\Quest\ChangeAuditor\Agent\SgConnectionMode` — the same value the installer
reads — and reports both cases before the run, because neither is visible from a version
comparison. A migration is shown amber rather than green: it succeeds, but it changes which
product a domain controller reports to, and the checkbox driving it defaults to **on**, so it
can be reached by inaction rather than by decision.

The MSI's `UPGRADE_SG_MISMATCH` property is *not* a refusal, despite the name. Its only
consumer in the whole package is the condition deciding whether the previously registered
installation name is carried forward — which is exactly what a migration needs, since a
Change Auditor installation name is meaningless as an Identity Defense tenant GUID. Nothing
blocks on it. The unsupported direction comes from the documentation, not from anything the
package enforces.

**An Org ID that disagrees with the checkbox warns; it never blocks.** Both a GUID and a
short name are valid Org IDs, so nothing else can tell that the wrong one was pasted — and
the checkbox is as likely to be the wrong control as the text box. The warning names both
and leaves the operator to decide.

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

## Packaging

```powershell
.\tools\publish.ps1                      # unsigned, for lab use
.\tools\publish.ps1 -SignToolPath <path> -CertificateThumbprint <sha1>
```

Produces `artifacts\HybridAgentDeploy\` — **one folder, both programs, 143 MB, 314 files**,
self-contained on `win-x64`. Copy the folder; run `HybridAgentDeploy.exe` or `hadeploy.exe`.
Nothing needs installing on the machine it runs from.

**The whole folder is the deliverable.** It is copied as a unit — the `.exe` files are
apphosts, the code and the .NET runtime sit beside them. The only genuinely optional files
are the three `.pdb`s (152 KB), and they are what turn a stack trace in a log into something
readable, so they stay.

### Two things trimmed out of it

`Microsoft.PowerShell.SDK` contributes 27 MB the tool never touches. Removed, because NFR4
expects the bundle to live on a share where every megabyte is paid on each launch:

| Removed | Size | Why it is safe |
|---|---|---|
| 13 satellite locale folders | 20 MB | PowerShell's localised messages. This tool's interface and error text are English regardless, so a non-English host would otherwise get an English window containing one translated exception. `SatelliteResourceLanguages` in `Directory.Build.props`. |
| `ref\` — 167 reference assemblies | 7 MB | They exist so PowerShell's `Add-Type` can compile C# at runtime. This tool never calls it, and the SDK here is only the WinRM **client** — every remote script executes in the target controller's own Windows PowerShell. `Directory.Build.targets`. |

Verified rather than reasoned: a bundle with both removed ran a full validation against five
lab domain controllers — SMB staging, WinRM session, remote script execution, output parsing,
cleanup — with results identical to the untrimmed bundle.

`publish.ps1` asserts both trims still happened. Each is silent when it stops working: a
NuGet update that moves the contentFiles, or a project overriding
`SatelliteResourceLanguages`, would quietly put 27 MB back. Both assertions were checked by
breaking the thing they guard.

**One bundle, not two.** The GUI and CLI share one copy of the .NET runtime. Publishing them
separately would double the payload for no benefit, and NFR4 expects this to live on a share
where the size is paid on every launch.

**Not trimmed, not NativeAOT, not ReadyToRun.** PRD §5.1: `Microsoft.PowerShell.SDK` tolerates
none of it and resolves modules from disk.

**Not single-file — Q4 answered by measurement.** Both were published and compared:

| | Size | Top-level entries |
|---|---|---|
| folder | 168 MB untrimmed, 143 MB as shipped | many |
| single-file | 162 MB | 12 — a 166 MB exe *plus* eight native libraries and a `runtimes\` directory |

`PublishSingleFile` does not produce a single file here: `e_sqlite3.dll`, `pwrshplugin.dll`,
`sni.dll` and friends cannot be embedded. The operator copies a folder either way, so the
single-file build buys nothing and adds a first-launch extraction step. PRD §5.1 predicted
exactly this.

### Signing

PRD Q3 — whether Quest will sign a tool carrying unsupported status — is deferred, so the
pipeline is wired up and waiting for a certificate. SEC9 requires a signature before
distribution: an unsigned bundle is fine for a lab and is not releasable.

```powershell
.\tools\publish.ps1 `
    -SignToolPath 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' `
    -CertificateThumbprint <sha1> `
    -TimestampUrl http://timestamp.digicert.com     # default
```

**Only 13 of the 285 binaries get signed, and that is deliberate.** 272 of them already carry
valid Microsoft or .NET Foundation signatures, and `signtool` *replaces* an existing signature
rather than adding to it — so signing everything would strip Microsoft's attestation off 234
.NET runtime files and substitute ours, claiming authorship of code we did not write and
discarding the stronger signature to do it. The script signs what is unsigned: our five
outputs, plus eight third-party libraries that ship unsigned from NuGet (`SQLitePCLRaw`,
`e_sqlite3`, the `JsonSchema.Net` family, `Markdig`). Those do need covering, because
AppLocker publisher rules and some EDR products evaluate what gets *loaded*, not only what
gets launched.

**Timestamping is not optional in practice.** Without `/tr`, the signature stops validating
the day the certificate expires — on every copy already sitting on a customer's share. The
script warns loudly if you pass an empty `-TimestampUrl`.

**It verifies rather than trusting the exit code**: `signtool verify /pa` plus a census
confirming every binary ends up `Valid`. Checked by signing with a self-signed certificate,
which is well-formed but chains to nothing — the guard caught it.

**What a certificate would have to be.** Since 2023 the CA/Browser Forum requires
publicly-trusted code-signing keys to live on FIPS 140-2 Level 2 hardware, so no one will
hand over a `.pfx`. In practice that means one of: Quest's own signing service or signing
machine; a cloud signing service such as Azure Trusted Signing or DigiCert KeyLocker, which
`signtool` drives through a `/dlib` provider instead of `/sha1`; or — worth considering for a
field utility aimed at AD shops — **the customer's own internal CA**, whose certificate their
own AppLocker and WDAC policies already trust.

**The icon** is generated by `tools/make-icon.ps1` and committed as
`src/HybridAgentDeploy.Gui/appicon.ico` — the build does not depend on the script having run.
Seven frames from 16 to 256 pixels; the mark is a plain arrow-into-bar because nothing with
interior detail survives 16 pixels, which is the size that matters for picking the window out
of a taskbar.

The Phase 6 acceptance checklist is [`docs/phase-6-acceptance.md`](docs/phase-6-acceptance.md).

## Phase status

| Phase | Scope | State |
|---|---|---|
| 1 | Core foundation | Complete |
| 2 | Deployment orchestrator | Complete |
| 3 | `WinRmSmbTransport` | Complete |
| 4 | CLI | Complete (live deploy acceptance run deferred) |
| 5 | WinForms GUI | Complete (manual test matrix outstanding) |
| 6 | Packaging and signing | Built; acceptance run on TitancorpNPV02 outstanding, signing deferred (Q3) |
