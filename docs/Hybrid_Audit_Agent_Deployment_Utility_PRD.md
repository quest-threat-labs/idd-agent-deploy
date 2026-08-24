# PRD: Hybrid Audit Agent Deployment Utility

**Product:** Quest Identity Defense — Hybrid Audit Agent
**Component:** Standalone MSI deployment utility
**Status:** Draft for implementation
**Support tier:** Customer-run, unsupported utility
**Document purpose:** Implementation input for Claude Code

---

## 0. How to use this document

This PRD is written to be consumed directly by Claude Code as the specification for a
greenfield build. It is deliberately prescriptive about technology choices, project
layout, and error semantics, because those decisions have already been reasoned through
and re-litigating them mid-build wastes effort.

**Rules for the implementing agent:**

1. Build in the phase order given in §16. Do not start a later phase until the prior
   phase's acceptance criteria pass.
2. Where this document says MUST, treat it as a hard requirement. Where it says SHOULD,
   deviate only with a comment explaining why.
3. Section §17 lists open questions. Where an open question blocks code, implement the
   documented default and mark the site with `// PRD-OPEN-Q: <id>` so it is greppable.
4. Do not add features not described here. See §3.2 for explicit non-goals.
5. All remote operations MUST go through the `ITargetTransport` abstraction (§5.3) so the
   tool is testable without a live Active Directory forest.

---

## 1. Problem statement

Deploying the Hybrid Audit Agent to domain controllers is currently a manual,
per-DC exercise. An administrator must remote into each DC, copy the MSI, run msiexec
with the correct `INSTALLATION_NAME` (Org ID) and `SG=1` switch, confirm the exit code,
and record what was installed where. In a forest with 20–60 DCs this is slow, error-prone,
and produces no durable record of which DCs received which agent version.

There is also no mechanism for repeatable partial deployment. Administrators frequently
need to target a subset of DCs — a pilot group, a single site, the read-only DCs — and
then return to that same subset weeks later for an upgrade. Today that subset lives in
someone's text file or memory.

## 2. Goals

| # | Goal |
|---|------|
| G1 | Reduce multi-DC agent deployment from a per-host manual task to a single guided operation |
| G2 | Produce a durable, queryable record of deployment target, timestamp, agent version, and outcome |
| G3 | Support named, reusable target groups (tags) so the same subset can be redeployed to later |
| G4 | Fail safely and legibly against Tier 0 infrastructure — never deploy to more DCs at once than an administrator can reason about |
| G5 | Be trustworthy enough for a customer to point at their own domain controllers without a Quest engineer present |

### 2.1 Primary success metric

Time-to-complete for a 20-DC agent deployment, measured from tool launch to final log
write, compared against the manual baseline. Target: under 15 minutes of operator
attention.

### 2.2 Secondary metric

Percentage of deployment runs completing with zero unexplained failures — i.e. every
non-zero exit code is surfaced with its msiexec verbose log attached.

---

## 3. Scope

### 3.1 In scope

- Enumeration of domain controllers from Active Directory
- Import of a DC list from a plain-text file
- Tagging of DCs, many-to-many, persisted across sessions
- Local inspection of an MSI file to extract its version and identity
- Remote deployment of that MSI to selected DCs, capped at 5 concurrent
- Exit-code capture, verbose log retrieval, and per-run logging
- Deployment history: what was deployed, where, when, at what version, with what result
- A Windows GUI as the primary interface
- A headless CLI mode over the same core library

### 3.2 Non-goals

The implementing agent MUST NOT build these:

- **Agent uninstallation or rollback.** Out of scope for v1.
- **Agent health or connectivity verification post-install.** The tool confirms the MSI
  installed; it does not confirm the agent reached the Identity Defense cloud tenant.
- **Deployment to non-DC servers or workstations.** The inventory model is DC-shaped.
- **Group Policy or SCCM integration.** This is a push tool, not a policy tool.
- **Scheduling.** The CLI mode is scriptable; the tool does not contain a scheduler.
- **Multi-forest support.** Single forest, single set of operator credentials, per run.
- **Cross-platform support.** Windows only. Do not introduce Avalonia, MAUI, or any
  cross-platform UI framework.
- **Auto-update of the utility itself.**
- **Credential storage.** See §14.

---

## 4. Users and operating context

**Primary user:** A customer Active Directory administrator with Domain Admin or
equivalent delegated rights over the domain controllers being targeted.

**Where it runs:** A domain-joined privileged access workstation or Tier 0 jump host,
running Windows 10/11 or Windows Server with the Desktop Experience. The operator is
logged in with, or can supply, credentials that hold local administrator rights on the
target DCs.

**Assumed environment characteristics:**

- WinRM is listening on target DCs. It is enabled by default on Windows Server 2012 and
  later, which covers all supported DC operating systems.
- SMB access to the `C$` administrative share on target DCs is available to the operator.
- The operator's session can obtain Kerberos tickets for the target DCs.

**Constraint:** The tool targets Tier 0 infrastructure. Every design decision favours
predictability and legibility over speed or convenience.

---

## 5. Technical design

### 5.1 Technology stack

| Concern | Choice |
|---|---|
| Language | C# |
| Runtime | .NET, current LTS. Verify with `dotnet --list-sdks` before scaffolding and target the highest available LTS. |
| UI | WinForms (`net<ver>-windows`, `UseWindowsForms`) |
| Remote execution | PowerShell Remoting over WinRM via `Microsoft.PowerShell.SDK` |
| AD enumeration | `System.DirectoryServices.ActiveDirectory` |
| MSI inspection | `WindowsInstaller` COM, late-bound |
| Data store | SQLite via `Microsoft.Data.Sqlite` |
| Logging | `Microsoft.Extensions.Logging` to a file sink |
| Tests | xUnit |

**Rationale for the stack, recorded so it is not relitigated:**

C# over Rust and PowerShell. The bulk of the risk in this tool is the remote execution
path, not the UI or the data layer. `Microsoft.PowerShell.SDK` provides working WinRM
with Negotiate/Kerberos authentication as a library call; the Rust equivalent means
implementing WS-Management SOAP plus SPNEGO, which is weeks of work with a long tail of
failures that only reproduce in customer environments. AD enumeration and MSI inspection
are likewise single API calls in .NET. PowerShell as a standalone deliverable is ruled out
because a `.ps1` will hit execution policy, Constrained Language Mode, and AppLocker in
precisely the hardened environments this tool exists to serve; a signed executable does not.

WinForms over WPF and over a terminal UI. The operator workflow is tabular and
selection-driven: a grid of DCs with checkboxes, tag filters, a file picker, and live
progress. WinForms provides `DataGridView`, `OpenFileDialog`, and progress reporting as
built-in controls. WPF's MVVM overhead buys nothing at this scale. A terminal UI would
require hand-rolling multi-select tables and a file browser, and the only mouse-capable
.NET TUI framework is not mature enough for a customer-facing tool.

**Packaging note:** `Microsoft.PowerShell.SDK` does not tolerate IL trimming or NativeAOT,
and it resolves modules from disk, which makes true single-file publish awkward. Publish
as self-contained, non-trimmed, framework-dependent-on-nothing, and accept a folder or a
large single-file output. Do not spend effort fighting this. See §17 Q4.

### 5.2 Solution layout

```
HybridAgentDeploy.sln
├── src/
│   ├── HybridAgentDeploy.Core/          # No UI references. All logic.
│   │   ├── Inventory/                   # SQLite repositories, schema migration
│   │   ├── Discovery/                   # AD enumeration, text-file import
│   │   ├── Msi/                         # MSI property extraction
│   │   ├── Deployment/                  # Orchestrator, transport, exit-code mapping
│   │   ├── Logging/                     # Run log writer
│   │   └── Models/
│   ├── HybridAgentDeploy.Gui/           # WinForms. Thin. No business logic.
│   └── HybridAgentDeploy.Cli/           # Console entry point. Thin.
└── tests/
    ├── HybridAgentDeploy.Core.Tests/
    └── HybridAgentDeploy.Core.IntegrationTests/   # Requires a lab forest; excluded by default
```

**Hard rule:** `HybridAgentDeploy.Core` MUST NOT reference `System.Windows.Forms`. All
orchestration, validation, and state transitions live in Core and are unit-testable
without a window. The GUI and CLI are presentation only.

### 5.3 The transport abstraction

All remote work goes through one interface. This exists so the deployment orchestrator,
concurrency guard, retry logic, and exit-code handling can be tested without a domain.

```csharp
public interface ITargetTransport
{
    Task<StagingResult> StageFileAsync(
        string targetHost, string localPath, string remoteDirectory,
        CancellationToken ct);

    Task<ExecutionResult> ExecuteAsync(
        string targetHost, string commandLine, TimeSpan timeout,
        CancellationToken ct);

    Task<RetrievalResult> RetrieveFileAsync(
        string targetHost, string remotePath, string localDestination,
        CancellationToken ct);

    Task CleanupAsync(
        string targetHost, string remoteDirectory, CancellationToken ct);
}
```

Provide two implementations:

- `WinRmSmbTransport` — the production path. SMB for staging and retrieval, PowerShell
  Remoting for execution.
- `SimulatedTransport` — for development and unit tests. Configurable per-host outcomes,
  including injected 1618, 1603, 3010, timeouts, and connection failures. This is not a
  test double bolted on later; build it in Phase 1 so the orchestrator can be developed
  against it.

### 5.4 The deployment sequence

For each target DC, execute in this order. This sequence is deliberate; do not reorder it.

1. **Pre-flight.** Confirm the DC resolves in DNS, is reachable on TCP 445 and 5985, and
   that the `C$` share is accessible. Fail fast with a distinct error category if not.
2. **Stage.** Create `C:\Windows\Temp\HybridAgentDeploy\<run-guid>\` on the target over
   `\\<dc>\C$\`. Copy the MSI into it. Compute and verify a SHA-256 hash of the staged
   copy against the source before proceeding.
3. **Execute.** Open a PowerShell runspace to the DC and run msiexec against the *local*
   staged path:

   ```
   msiexec /i "C:\Windows\Temp\HybridAgentDeploy\<run-guid>\<msi-name>" ^
     SG=1 ^
     INSTALLATION_NAME="<org_id>" ^
     INSTALLATION_NAME_VALID="1" ^
     /qn ^
     /l*v "C:\Windows\Temp\HybridAgentDeploy\<run-guid>\install.log"
   ```

   Capture the process exit code. Do not rely on the remoting call's own success as a
   proxy for installation success.
4. **Retrieve.** Copy `install.log` back over SMB to the local run directory, named
   `<dc-fqdn>_install.log`. Do this whether the install succeeded or failed — the log is
   most valuable on failure.
5. **Clean up.** Remove the staging directory from the target. On cleanup failure, log a
   warning; do not fail the deployment.
6. **Record.** Write the outcome to the inventory database (§6) and the run log (§12).

**Why staging locally matters.** Running `msiexec /i \\server\share\agent.msi` inside a
remoting session fails in most environments because the remote session cannot delegate the
operator's Kerberos ticket to a third host — the classic double-hop problem. Copying the
MSI to the DC's local disk first, then executing entirely locally on that DC, sidesteps
delegation entirely and requires no CredSSP, no resource-based constrained delegation, and
no configuration changes on Tier 0 hosts. This is the single most important design decision
in the tool. Do not "optimise" it into a UNC execution.

### 5.5 MSI property extraction

On MSI selection, extract and display these properties by opening the MSI database
read-only and querying the `Property` table:

| Property | Use |
|---|---|
| `ProductVersion` | Displayed to operator; recorded in deployment history |
| `ProductName` | Displayed; used for the sanity check below |
| `ProductCode` | Recorded in history |
| `UpgradeCode` | Recorded in history |

Query pattern: `SELECT \`Value\` FROM \`Property\` WHERE \`Property\` = 'ProductVersion'`.

Open the database read-only and dispose the COM objects deterministically; a leaked
`Installer` object holds a file lock on the MSI.

**Sanity check.** If `ProductName` does not look like a Quest Change Auditor agent
package, warn the operator prominently but allow them to proceed. The tool should not
hard-block on a name string that may change between releases.

---

## 6. Data model

SQLite, single file, default location `%LOCALAPPDATA%\Quest\HybridAgentDeploy\inventory.db`.
Enable WAL mode. Version the schema in a `schema_version` table and write forward-only
migrations from the first commit.

```sql
CREATE TABLE domain_controller (
    id                INTEGER PRIMARY KEY,
    fqdn              TEXT NOT NULL UNIQUE COLLATE NOCASE,
    netbios_name      TEXT,
    domain            TEXT,
    site_name         TEXT,
    os_version        TEXT,
    is_read_only      INTEGER NOT NULL DEFAULT 0,
    is_global_catalog INTEGER NOT NULL DEFAULT 0,
    source            TEXT NOT NULL,          -- 'ad_enumeration' | 'file_import' | 'manual'
    first_seen_utc    TEXT NOT NULL,
    last_seen_utc     TEXT NOT NULL,
    is_active         INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE tag (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
    description   TEXT,
    created_utc   TEXT NOT NULL
);

CREATE TABLE dc_tag (
    dc_id       INTEGER NOT NULL REFERENCES domain_controller(id) ON DELETE CASCADE,
    tag_id      INTEGER NOT NULL REFERENCES tag(id) ON DELETE CASCADE,
    applied_utc TEXT NOT NULL,
    PRIMARY KEY (dc_id, tag_id)
);

CREATE TABLE deployment_run (
    id                  INTEGER PRIMARY KEY,
    run_guid            TEXT NOT NULL UNIQUE,
    started_utc         TEXT NOT NULL,
    completed_utc       TEXT,
    operator_account    TEXT NOT NULL,
    msi_path            TEXT NOT NULL,
    msi_file_name       TEXT NOT NULL,
    msi_sha256          TEXT NOT NULL,
    msi_product_version TEXT NOT NULL,
    msi_product_name    TEXT,
    msi_product_code    TEXT,
    org_id              TEXT NOT NULL,
    cloud_mode          INTEGER NOT NULL DEFAULT 1,   -- SG=1
    max_parallel        INTEGER NOT NULL,
    target_count        INTEGER NOT NULL,
    success_count       INTEGER NOT NULL DEFAULT 0,
    failure_count       INTEGER NOT NULL DEFAULT 0,
    was_halted          INTEGER NOT NULL DEFAULT 0,
    halt_reason         TEXT,
    log_directory       TEXT NOT NULL
);

CREATE TABLE deployment_result (
    id               INTEGER PRIMARY KEY,
    run_id           INTEGER NOT NULL REFERENCES deployment_run(id) ON DELETE CASCADE,
    dc_id            INTEGER NOT NULL REFERENCES domain_controller(id),
    started_utc      TEXT,
    completed_utc    TEXT,
    outcome          TEXT NOT NULL,   -- see §10.2
    stage            TEXT,            -- 'preflight'|'stage'|'execute'|'retrieve'|'cleanup'
    exit_code        INTEGER,
    attempt_count    INTEGER NOT NULL DEFAULT 1,
    error_category   TEXT,
    error_detail     TEXT,
    msi_log_path     TEXT
);

CREATE INDEX ix_deployment_result_dc ON deployment_result(dc_id);
CREATE INDEX ix_deployment_result_run ON deployment_result(run_id);
```

### 6.1 Derived view: last known state per DC

The requirement to retain "what was last deployed where, when, and at what version" is
served by a query, not a denormalised column. Provide it as a view so both UI and CLI
consume the same definition:

```sql
CREATE VIEW dc_last_deployment AS
SELECT dc.id AS dc_id, dc.fqdn, r.msi_product_version, r.org_id,
       res.completed_utc, res.outcome, res.exit_code, res.msi_log_path
FROM domain_controller dc
JOIN deployment_result res ON res.dc_id = dc.id
JOIN deployment_run r ON r.id = res.run_id
WHERE res.completed_utc = (
    SELECT MAX(r2.completed_utc) FROM deployment_result r2 WHERE r2.dc_id = dc.id
);
```

### 6.2 Enumeration and import semantics

- AD enumeration is **additive and non-destructive.** A DC that disappears from AD is
  marked `is_active = 0`; its row and its tags and history are retained.
- Text-file import accepts one host per line, ignoring blank lines and lines beginning
  with `#`. Accept FQDN, NetBIOS name, or IP. Attempt to resolve each to an FQDN; report
  unresolvable entries to the operator rather than silently discarding them.
- Import supports applying one or more tags to every host in the file, in a single
  operation. This is the requirement's primary import use case.
- Re-importing a file that contains an already-known host updates `last_seen_utc` and adds
  tags; it does not duplicate the row.

---

## 7. Deployment safety guards

These are the requirements that exist because the targets are domain controllers.

### R7.1 Concurrency cap — hard limit of 5

A maximum of **5 concurrent deployment operations**, enforced by a `SemaphoreSlim(5)`.

- A "deployment operation" spans the **entire** sequence in §5.4 — pre-flight through
  cleanup. A concurrency slot is released only when that whole sequence terminates,
  successfully or otherwise, or hits its timeout. Do not release the slot after the
  msiexec call returns.
- The cap of 5 is a **ceiling, not a default.** The operator may configure a lower value
  (1–5). There MUST be no code path, configuration file, environment variable, or CLI flag
  that raises it above 5. Encode 5 as a compile-time constant and clamp all input to it.
- Rejecting a request above 5 is not an error the operator has to work around; clamp
  silently to 5 and note it in the run log.

### R7.2 Site-concurrency guard

Within a single AD site, never occupy more than half the active DCs simultaneously,
rounded down, with a floor of 1. In a two-DC site this means one at a time. With a global
cap of 5 this rarely binds, but in small sites it prevents a bad MSI from touching both
DCs at once.

Implement as a secondary gate acquired after the global semaphore.

### R7.3 Circuit breaker

If the first **3** consecutive deployment operations all fail with an error category of
`Authentication`, `Connectivity`, or `Staging`, halt the run immediately, do not start
further targets, and surface the reason prominently.

Rationale: those three categories almost always indicate an operator-side problem —
wrong credentials, a firewall, an unreachable subnet — not a per-DC problem. Attempting
the remaining 40 DCs generates noise, wastes time, and in the authentication case risks
account lockout.

Failures categorised as `InstallFailure` (a non-zero msiexec exit code) do **not** trip
the breaker; those are genuinely per-host conditions.

### R7.4 Continue on failure

Absent a circuit-breaker trip, a single target's failure does not stop the run. Complete
all targets and present a summary.

### R7.5 Per-target timeout

Default 15 minutes per deployment operation, configurable 5–60 minutes. On timeout,
attempt cleanup, record `Timeout`, release the slot, continue.

### R7.6 No implicit retry of installs

Do not automatically retry a failed installation except for exit code 1618 (§10.1). A
silent retry of an MSI that failed for an unknown reason against a domain controller is
not acceptable behaviour. Failed targets are re-runnable by operator action.

---

## 8. GUI requirements

WinForms. Five screens or tabs. Prioritise legibility over polish.

### 8.1 Inventory

- `DataGridView` of DCs: FQDN, Domain, Site, OS, RODC flag, Tags, Last Deployed Version,
  Last Deployed (UTC), Last Outcome.
- Multi-select via checkbox column. Select-all and select-none.
- Filter controls: free-text on FQDN, dropdown filter by tag, filter by site, filter by
  last-outcome.
- Buttons: *Enumerate from Active Directory*, *Import from file…*, *Manage tags*.
- Outcome column colour-coded: success, reboot-pending, failure, never deployed.

### 8.2 Tag management

Create, rename, delete tags. Apply or remove tags on the current inventory selection.
Deleting a tag removes the association only; it never deletes DC rows or history.

### 8.3 Deployment setup

- MSI picker via `OpenFileDialog`, filtered to `*.msi`.
- On selection, display extracted `ProductName`, `ProductVersion`, `ProductCode`, file
  SHA-256, and file size. If extraction fails, block deployment.
- **Org ID** text field. Required. Non-empty, trimmed. Validate against the expected
  format once known (§17 Q2) — until then, require non-empty and warn on whitespace or
  characters that would need quoting.
- **Cloud mode (`SG=1`)** checkbox, **default checked**, with hover text explaining that
  it configures the agent to report to the Identity Defense cloud tenant. See §17 Q1.
- Max-parallel spinner, range 1–5, default 5.
- Per-target timeout spinner, 5–60 minutes, default 15.
- Target summary: count of selected DCs, broken down by site.
- A read-only preview of the exact msiexec command line that will run. This matters —
  operators deploying to Tier 0 want to see the command before it executes.
- *Start deployment* button, gated on a confirmation dialog that restates the target count
  and the Org ID.

### 8.4 Deployment progress

- Live grid, one row per target: DC, current stage, elapsed, outcome.
- Overall progress: completed / total, success and failure counts.
- Visible indicator of how many slots are currently occupied.
- *Cancel* button: stops starting new targets, allows in-flight targets to complete or
  time out. Never abandon an in-flight msiexec without attempting cleanup.
- On completion, offer *Open log folder* and *Retry failed targets*.

### 8.5 History

- Runs list: timestamp, operator, MSI version, Org ID, target count, success/failure
  counts, halted flag.
- Drill into a run for per-DC results, with a link opening that DC's retrieved msiexec
  verbose log.
- Export a run's results to CSV.

---

## 9. CLI requirements

The CLI exists for repeatability and to provide an escape hatch if the operator's jump
host has no desktop. It calls the same Core library.

```
hybridagentdeploy enumerate [--domain <fqdn>] [--tag <name>]...
hybridagentdeploy import --file <path> [--tag <name>]... [--dry-run]
hybridagentdeploy list [--tag <name>] [--site <name>] [--format table|csv|json]
hybridagentdeploy inspect-msi --msi <path>
hybridagentdeploy deploy --msi <path> --org-id <id>
                         [--tag <name>]... | [--host <fqdn>]... | [--all]
                         [--no-cloud-mode]
                         [--max-parallel <1-5>]
                         [--timeout-minutes <5-60>]
                         [--log-dir <path>]
                         [--confirm]
hybridagentdeploy history [--run <guid>] [--host <fqdn>] [--format table|csv|json]
```

- `deploy` MUST refuse to run without `--confirm` when the resolved target count exceeds
  1. This prevents an accidental forest-wide push from a mistyped script.
- Exit codes: `0` all succeeded; `1` completed with failures; `2` halted by circuit
  breaker; `3` invalid arguments or pre-flight configuration error; `4` cancelled.
- All output to stdout; diagnostics to stderr. `--format json` output must be
  machine-parseable with no interleaved human text.

---

## 10. Exit code and error handling

### 10.1 msiexec exit code mapping

| Code | Meaning | Treatment |
|---|---|---|
| 0 | Success | `Success` |
| 1602 | User cancelled | `Failure` / `InstallFailure` |
| 1603 | Fatal error during installation | `Failure` / `InstallFailure`. Retrieve log; the cause is in it. |
| 1605 | Product not installed | `Failure` / `InstallFailure` |
| 1618 | Another installation in progress | **Retryable.** Up to 3 attempts, 60s / 120s / 240s backoff. If still 1618, record `Failure` with category `Contended`. |
| 1619 | Package could not be opened | `Failure` / `Staging`. Indicates the staged copy is corrupt or inaccessible despite hash verification — investigate. |
| 1620 | Package could not be opened (invalid) | `Failure` / `Staging` |
| 1625 | Blocked by system policy | `Failure` / `Policy`. Software restriction policy or AppLocker on the target. |
| 1638 | Another version of this product is already installed | `Failure` / `AlreadyInstalled`. Surface distinctly; this is an upgrade-path problem, not a broken deployment. |
| 3010 | Success, reboot required | `SuccessRebootRequired`. Treat as success. Surface as a distinct outcome. |
| 1641 | Success, reboot initiated | `SuccessRebootRequired`, and **flag loudly** — a reboot initiated on a DC is significant. |
| Other | Unmapped | `Failure` / `InstallFailure`, recording the raw code. |

The Hybrid Audit Agent does not require a reboot to function. 3010 and 1641 therefore
generally indicate a reboot pending from an unrelated cause on the target, not from this
installation. Report them accurately without alarming the operator about this deployment.

Because `/qn` is used, 1602 should not occur; if it does, record it verbatim rather than
suppressing it.

### 10.2 Outcome values

`Success` · `SuccessRebootRequired` · `Failure` · `Timeout` · `Cancelled` · `Skipped`

### 10.3 Error categories

`Authentication` · `Connectivity` · `Staging` · `InstallFailure` · `Contended` ·
`Policy` · `AlreadyInstalled` · `Timeout` · `Internal`

Only the first three trip the circuit breaker (§R7.3).

### 10.4 Error message quality

Every failure surfaced to the operator MUST state which stage failed, which DC, and what
the next diagnostic step is. "Deployment failed" is not acceptable. "Staging failed on
DC01.corp.local: access denied writing to \\DC01.corp.local\C$\Windows\Temp — confirm the
running account holds local administrator rights on this DC" is.

---

## 11. Pre-flight validation

Before starting any deployment, validate and report as a single blocking checklist:

1. MSI file exists, is readable, and its properties extract successfully.
2. Org ID is present and non-empty.
3. At least one target selected.
4. Local run-log directory is writable.
5. For each target: DNS resolution succeeds.

Per-target reachability (TCP 445, 5985, `C$` accessibility) is checked in the target's own
pre-flight stage, not up front — probing 60 DCs serially before starting would be slow and
the result can change by the time the target is reached.

Offer a **Test connectivity** action that runs per-target pre-flight against the selection
without deploying. Operators will want this before their first real run.

---

## 12. Logging

### 12.1 Run log

One directory per run: `<log-root>\<yyyyMMdd-HHmmss>-<run-guid-short>\`, containing:

- `run.log` — human-readable chronological log of the whole run
- `results.csv` — one row per target with outcome, exit code, timings, error category
- `<dc-fqdn>_install.log` — the retrieved msiexec verbose log per target

Default log root: `%LOCALAPPDATA%\Quest\HybridAgentDeploy\logs\`. Configurable.

### 12.2 Content requirements

`run.log` MUST record: start and end timestamps in UTC and local, operator account, tool
version, MSI file name, version, product code and SHA-256, Org ID, cloud-mode flag, the
exact msiexec command template used, resolved target list with sites, max-parallel in
effect, and per-target stage transitions with timestamps.

The exact command line is logged for auditability — a customer administrator may need to
show a change-control board precisely what ran against their domain controllers.

### 12.3 What must never be logged

Passwords, credential material, or Kerberos tickets, in any form, in any file. If explicit
credentials are supplied, log the account name only.

---

## 13. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR1 | Deploying to 5 DCs concurrently must not exceed 300 MB working set in the utility process |
| NFR2 | UI must remain responsive during deployment; all I/O async, no blocking calls on the UI thread |
| NFR3 | Inventory operations on 500 DCs must render in under 1 second |
| NFR4 | The tool must run correctly from a UNC path or removable media |
| NFR5 | No installation required — copy and run |
| NFR6 | The inventory database must survive an abrupt process kill without corruption (WAL mode, transactional writes) |
| NFR7 | An interrupted run must leave no staging directories on targets that the tool cannot subsequently identify and clean up. Record staged paths in the DB before creating them so orphans are recoverable. |

---

## 14. Security requirements

| ID | Requirement |
|---|---|
| SEC1 | **No credential persistence.** Credentials are held in memory for the duration of a run only. Use `SecureString` or equivalent at the boundary and clear it. Do not write credentials to the database, config, or logs. |
| SEC2 | Default to the operator's current Windows identity (integrated authentication). Explicit alternate credentials are an option, not the default. |
| SEC3 | Use Negotiate/Kerberos authentication. **Do not implement, enable, or offer CredSSP.** The local-staging design makes it unnecessary, and enabling CredSSP against a DC is a security regression. |
| SEC4 | Do not offer or default to Basic authentication over HTTP. |
| SEC5 | Support WinRM over HTTPS (5986) as an option. Note in the UI that Negotiate over HTTP (5985) still applies message-level encryption to the payload, so 5985 is not plaintext — but let customers who require 5986 use it. |
| SEC6 | Verify the SHA-256 of the staged MSI on the target against the source before executing. A file that changed in transit must never be installed on a domain controller. |
| SEC7 | Staging directory under `C:\Windows\Temp\` inherits admin-only ACLs. Do not stage to a world-readable location, and do not loosen ACLs on the staging path. |
| SEC8 | Remove the staged MSI from every target after deployment. |
| SEC9 | The published binary MUST be Authenticode-signed before distribution. See §17 Q3. |
| SEC10 | Validate and quote the Org ID before interpolating it into the msiexec command line. Reject or escape quote characters and shell metacharacters. This is command construction against a domain controller — treat the input as untrusted. |

SEC10 deserves emphasis for the implementing agent: the Org ID is operator-supplied free
text that is concatenated into a command executed with elevated rights on a Tier 0 host.
Build the command line with proper argument quoting, not string interpolation, and add a
unit test that feeds it `"; Stop-Service NTDS; "` and asserts the resulting command is
inert.

---

## 15. Testing requirements

### 15.1 Unit tests (must pass without a domain)

- Exit-code mapping: every row in §10.1.
- Concurrency: assert never more than 5 in flight, using `SimulatedTransport` with
  artificial delays; assert slots release only after the full sequence.
- Site guard: two-DC site never has both in flight.
- Circuit breaker: trips on 3 consecutive `Authentication` failures; does **not** trip on
  3 consecutive `InstallFailure` results.
- 1618 retry: exactly 3 attempts, correct backoff, correct final categorisation.
- Command construction: correct quoting, injection attempt neutralised, `SG=1` present
  when cloud mode is on and absent when off.
- Import parsing: comments, blanks, duplicates, unresolvable hosts, mixed FQDN/NetBIOS.
- Schema migration from empty to current.
- Repository round-trips and the `dc_last_deployment` view against seeded data.

### 15.2 Integration tests (lab forest, excluded from default run)

Tag with `[Trait("Category","Integration")]`. Cover: real AD enumeration, real SMB
staging, real WinRM execution against a lab DC, log retrieval, cleanup verification.

### 15.3 Manual test matrix

Document, do not automate: deployment to an RODC; deployment where the target already has
a newer agent (expect 1638); deployment with a pending reboot on the target (expect 3010);
operator account lacking local admin on one DC of many; target with WinRM disabled;
mid-run cancellation.

---

## 16. Implementation phases

Build in this order. Each phase ends with its acceptance criteria met.

### Phase 1 — Core foundation

Scope: solution scaffold, SQLite schema and migrations, repositories, models,
`ITargetTransport` interface, `SimulatedTransport`, logging infrastructure, MSI property
extraction, text-file import parser, AD enumeration.

**Acceptance:** unit tests pass for schema migration, repository round-trips, import
parsing, and MSI extraction against a real sample MSI. `SimulatedTransport` can be driven
to produce every outcome in §10.2. No UI exists yet.

### Phase 2 — Deployment orchestrator

Scope: the §5.4 sequence, concurrency semaphore, site guard, circuit breaker, timeout
handling, retry for 1618, exit-code mapping, run log writing, cancellation.

Built entirely against `SimulatedTransport`.

**Acceptance:** every unit test in §15.1 passes. The orchestrator has never touched a real
domain controller and does not know whether it has.

### Phase 3 — Real transport

Scope: `WinRmSmbTransport`. SMB staging with hash verification, PowerShell Remoting
execution with exit-code capture, log retrieval, cleanup, per-target pre-flight.

**Acceptance:** integration tests pass against a lab DC. A deployment to a single lab DC
completes, the agent is installed, the verbose log is retrieved locally, and the staging
directory is gone.

### Phase 4 — CLI

Scope: all commands in §9. Argument parsing, `--confirm` gate, output formats, process
exit codes.

**Acceptance:** a scripted `import` then `deploy --tag pilot --confirm` completes against a
lab and produces correct JSON output and process exit code.

### Phase 5 — GUI

Scope: all five screens in §8.

**Acceptance:** the full manual test matrix in §15.3 can be executed through the GUI. UI
stays responsive throughout a 5-concurrent deployment.

### Phase 6 — Packaging

Scope: publish profile, version stamping, icon, signing hook, README for the operator.

**Acceptance:** the published output runs on a clean Windows Server with Desktop
Experience and no .NET SDK installed, from a UNC path.

---

## 17. Open questions

| ID | Question | Owner | Default until resolved |
|---|---|---|---|
| Q1 | Is `SG=1` ever legitimately omitted or set to 0 for this agent, or is cloud mode the only mode this utility will target? | PM | Expose as a checkbox defaulting to checked. If cloud mode is the only valid mode, simplify to a hardcoded `SG=1` and remove the control. |
| Q2 | What is the valid format of the Org ID — GUID, opaque string, length bounds? | PM / Change Auditor team | Require non-empty, trim, warn on characters needing quoting. Add strict validation once known. |
| Q3 | Will Quest code-sign this utility despite its unsupported status? | PM (research in progress) | Build the signing step into the publish pipeline as a no-op placeholder. Treat signing as a release gate. If Quest declines, the README MUST document that AppLocker and EDR may block execution in hardened environments — which is precisely where this tool is most needed. |
| Q4 | Is a large single-file publish acceptable, or is a folder deployment preferred? | PM | Publish self-contained single-file, untrimmed. Revisit only if size is objected to. |
| Q5 | Do any target customer jump hosts run Windows Server Core, where WinForms cannot launch? | PM / SE team | The CLI (Phase 4) covers this case. Confirm before deprioritising CLI polish. |
| Q6 | Product naming: the outline calls this the Identity Defense **Hybrid Audit Agent**, but the MSI is `Quest Change Auditor Agent (x64).msi`. Are these the same binary under different names, and which name should the UI use? | PM | UI refers to "Hybrid Audit Agent"; log and history record the MSI's actual `ProductName`. Flagging because a mismatch between the tool's language and the file the operator picks will cause support questions. |
| Q7 | Minimum supported DC OS version? Affects whether WinRM-enabled-by-default can be assumed. | PM | Assume Windows Server 2012 R2 and later. |

---

## 18. Requirements traceability

Mapping from the original outline to this document, to confirm nothing was dropped.

| Original requirement | Addressed in |
|---|---|
| Enumerate DCs | §5.1, §6.2, R8.1 |
| Import text list of DCs, apply tag to listed DCs | §6.2 |
| Multiple tags supported | §6 (`dc_tag` many-to-many), §8.2 |
| Deploy MSI to DCs over the network | §5.4 |
| Maintain list of tagged DCs for repeated use | §6, §8.1, §8.2 |
| Retain last-deployed target, timestamp, and agent version | §6, §6.1, §8.5 |
| Capture return codes, output success/failure log file | §10.1, §12 |
| SQLite inventory store | §6 |
| Standalone | NFR4, NFR5, §16 Phase 6 |
| Built in Rust unless good reason otherwise | **Superseded.** §5.1 records the reasoning for C#/.NET: the remote-execution path dominates project risk and is a library call in .NET versus a protocol implementation in Rust. |
| Mouse-compatible TUI | **Superseded.** §8 specifies a WinForms GUI, with the CLI (§9) covering headless hosts. Reasoning in §5.1. |
| Specific msiexec command pattern with SG=1, INSTALLATION_NAME, INSTALLATION_NAME_VALID | §5.4 step 3 |
| Org ID entered into the utility | §8.3, SEC10, Q2 |
| MSI location browsable and pickable | §8.3 |
| Tool provides the version of the MSI when loaded | §5.5, §8.3 |
| — | **Added:** concurrency cap of 5 (R7.1), site guard (R7.2), circuit breaker (R7.3), hash verification (SEC6), local staging to avoid double-hop (§5.4) |
