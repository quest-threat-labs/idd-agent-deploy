# Hybrid Audit Agent Deployment Utility

Deploys the Quest Identity Defense Hybrid Audit Agent to Active Directory domain
controllers, records which controller received which version, and keeps the per-target
install logs.

**This is a field utility, not a supported Quest product.** Read the warning at the bottom
before using it in a customer environment.

---

## Before you start

> **THIS BUNDLE IS NOT CODE-SIGNED.**
>
> AppLocker, WDAC, and endpoint protection may block it — most likely in exactly the
> hardened environments where it is most useful. If it will not launch, that is the first
> thing to check with the customer, not a defect in the tool.

**The targets are domain controllers.** Nothing here is reversible by this utility: it has
no uninstall and no rollback. Use **Validate targets** first.

---

## Installing it

There is nothing to install. Copy the whole folder and run it.

It runs from a local disk, a UNC share, or removable media. Running it from a share is the
expected case — one copy, many jump hosts.

**Copy the whole folder.** The two executables share the .NET runtime that sits beside them.
Copying only the `.exe` gives you a program that will not start.

| File | What it is |
|---|---|
| `HybridAgentDeploy.exe` | The window. Double-click this. |
| `hadeploy.exe` | The command line, for scripted or Server Core use. |
| `hybridagentdeploy.json.sample` | Optional. See **Keeping it portable**. |

Requires 64-bit Windows. Nothing needs to be installed on the machine you run it from — no
.NET, no PowerShell modules, no agent.

---

## What it needs to work

- **An account with local administrator rights on the target domain controllers.** It uses
  your current Windows identity by default, or an alternate account you type in.
- **SMB (TCP 445) and WinRM (TCP 5985) reachable** on each target.
- **Windows Server 2016 or later** on each target. The agent's installer refuses anything
  older, and validation reports it before you find out the hard way.
- **The agent MSI**, which this tool does not ship. Point it at your own copy.

It does not need CredSSP, and will not offer it. It does not change any setting on a domain
controller other than installing the agent.

---

## Using it

1. **Inventory** — *Enumerate from Active Directory*. This is the only way controllers enter
   the inventory. It is forest-wide and reads sites, OS versions, and RODC status.
2. **Inventory** — tick the controllers you want. Filters narrow the list; ticks survive
   changing a filter, and the tool tells you when your selection includes rows you cannot
   currently see.
3. **Deploy** — choose the MSI, fill in **Quest SMP Organization ID \ Change Auditor
   Installation Name**, and check **Cloud mode**:

   | Cloud mode | The agent reports to | That field holds |
   |---|---|---|
   | On | Identity Defense (cloud) | the SMP Organization ID — a GUID |
   | Off | Change Auditor (on-premises) | the installation name, e.g. `DEFAULT` |

   The tool remembers this value and fills it in next time — **one per mode**, so switching
   the checkbox swaps in the identifier that belongs to the other product rather than leaving
   the wrong kind of value in the box. It is stored in the inventory database beside the
   controllers it relates to, so a portable copy carries it along. Nothing secret is kept:
   this identifier is already written into every run log.

   Deploying with cloud mode **on** to a controller currently running in Change Auditor mode
   **migrates it to the cloud tenant**. That is supported and it is one-way. Validation says
   so before you start.

4. **Deploy** — press **Validate targets**. It installs nothing. It confirms each controller
   is reachable and writable, that your account can start a process on it, that its Windows
   version is supported, and what agent it already has — then tells you exactly what a
   deployment would do to each one.
5. **Deploy** — press **Start deployment** and confirm.
6. **Progress** — watch it. **Cancel** stops new controllers starting; anything already
   running finishes and cleans up after itself.
7. **History** — what happened, per run and per controller, with the msiexec log for each.

---

## What it does to each domain controller

1. Checks DNS, SMB, and WinRM answer.
2. Copies the MSI to `C:\Windows\Temp\HybridAgentDeploy\{run-guid}\` and verifies its
   SHA-256 **on the target** before doing anything with it.
3. Runs `msiexec` locally on the controller, against the local copy — never across the
   network. This is why no CredSSP or delegation change is needed.
4. Copies the msiexec log back.
5. Deletes the staging directory.

Never more than **5** controllers at once, and never more than **half the controllers in any
one Active Directory site** at once. Neither limit can be raised. A selection concentrated in
a small site will run more slowly than the parallel setting suggests, and the Progress tab
says so while it is happening.

---

## Where it keeps things

By default, under `%LOCALAPPDATA%\Quest\HybridAgentDeploy\`:

- `inventory.db` — the controller inventory, tags, and deployment history
- `logs\<timestamp>-<id>\` — one folder per deployment: `run.log`, `results.csv`, and the
  retrieved msiexec log for each controller
- `logs\<timestamp>-<id>-validate\` — the same for a validation. Nothing was installed, so
  these leave no entry in History.

### Keeping it portable

To keep the database and logs with the tool rather than on each machine you run it from —
useful on a USB stick or a share — rename `hybridagentdeploy.json.sample` to
`hybridagentdeploy.json` and leave it beside the executable. Relative paths in it are
resolved against that folder, so the whole thing travels together.

---

## When something goes wrong

Every failure names the controller, the stage that failed, and what to do next. Start there.

| What you see | Usually means |
|---|---|
| Fails at pre-flight | The controller is unreachable, or WinRM is not running on it |
| Fails at staging | Your account is not a local administrator on that controller |
| Exit code 1618 | Another installation was in progress. Retried automatically, three times. |
| Exit code 1638 | A newer agent is already installed. Not a failure of this tool. |
| Exit code 3010 or 1641 | The install **succeeded**; a reboot is pending. 1641 means a reboot was started. |
| The run halts on its own | Three consecutive controllers failed for the same environmental reason. Fix that, then retry. |

The run's own `run.log` records the exact msiexec command line that was used, so you can
show a change-control board what ran.

---

## What it will not do

No uninstall. No rollback. No post-install health check — it reports what msiexec returned,
not whether the agent later worked. No scheduling. No non-domain-controller targets.

---

## Support

**None.** This is a field utility. It is not a supported Quest product, it is not covered by
any support agreement, and it is not code-signed. Test it in a lab against your own domain
controllers before using it anywhere that matters.
