# Phase 6 acceptance — run this on TitancorpNPV02

PRD §16 Phase 6: *"the published output runs on a clean Windows Server with Desktop
Experience and no .NET SDK installed, from a UNC path."*

This has to be done by hand. WinRM is closed on `TitancorpNPV02` (5985 and 5986 both refuse),
and a GUI needs an interactive session anyway, so there is no way to drive it remotely. Ten
minutes over RDP.

The bundle is already staged at **`C:\HAD-acceptance\`** on the VM. Delete that folder when
you are done.

---

## What the VM actually is

Read over SMB before staging:

| | |
|---|---|
| OS | Windows Server 2025, build 26100 |
| Shell | Desktop Experience — `explorer.exe`, `ServerManager.exe`, `mmc.exe` all present |
| Domain | `titancorp.local` |
| Reachable | ping, SMB 445, RDP 3389 |
| **WinRM** | **closed** — 5985 and 5986 both refuse |

### It is not quite "clean", and that matters

`C:\Program Files\dotnet\` exists and contains:

```
shared\Microsoft.NETCore.App\10.0.5
shared\Microsoft.WindowsDesktop.App\10.0.5
shared\Microsoft.AspNetCore.App\10.0.5
packs\           (SDK reference and apphost packs)
templates\10.0.5
```

but **no `dotnet.exe` and no `host\fxr\`**. It looks like an SDK that was installed and then
partly removed.

There is no `sdk\` folder, so it satisfies the letter of "no .NET SDK installed". But the
runtimes being present is the sort of thing that quietly invalidates an acceptance test — if
a framework-dependent build would run here, then "the app started" proves nothing about
whether the bundle carries its own runtime.

Without `hostfxr.dll` it should not be able to launch a framework-dependent app at all. That
is an inference from the file layout, so **step 1 below turns it into evidence** rather than
leaving it as an assumption.

---

## The checklist

### 1. Negative control — this one is SUPPOSED to fail

```
C:\HAD-acceptance\control-framework-dependent\HybridAgentDeploy.exe
```

**Expected:** a dialog or console error along the lines of *"You must install .NET Desktop
Runtime to run this application"*, and no window.

- If it **fails** → the VM cannot run framework-dependent .NET, so step 2 succeeding means
  the bundle really is self-contained. Carry on.
- If it **starts** → stop. The leftover runtimes are usable, this VM cannot prove
  self-containment, and we need a genuinely clean one before Phase 6 can be signed off.

### 2. The bundle, run locally

```
C:\HAD-acceptance\HybridAgentDeploy\HybridAgentDeploy.exe
```

**Expected:** the window opens. Title *Hybrid Audit Agent Deployment Utility*, blue
download-arrow icon in the title bar and taskbar, five tabs.

### 3. The bundle, run from a UNC path — the actual acceptance criterion

```
\\TitancorpNPV02\C$\HAD-acceptance\HybridAgentDeploy\HybridAgentDeploy.exe
```

**Expected:** identical behaviour. It has already been verified from a UNC path on the
workstation; this confirms it on a server that has no .NET host of its own.

*(If you would rather test a genuine cross-machine share than the loopback admin share, copy
the folder to any file share the VM can reach and launch it from there. Same expectation.)*

### 4. The CLI

```
C:\HAD-acceptance\HybridAgentDeploy\hadeploy.exe --version
C:\HAD-acceptance\HybridAgentDeploy\hadeploy.exe enumerate
C:\HAD-acceptance\HybridAgentDeploy\hadeploy.exe list
```

**Expected:** `1.0.0+<commit>`, then enumeration finds the five lab domain controllers and
`list` prints them. This is the first time enumeration has run from anywhere other than the
workstation, so it is worth doing rather than assuming.

### 5. Real work, from the VM

On the **Inventory** tab press *Enumerate from Active Directory*, tick the controllers, go to
**Deploy**, browse to an agent MSI, and press **Validate targets**.

**Expected:** the same verdicts the workstation produces — four controllers reading
*migrates to cloud*, `RnD-DC` reading *ready - reinstall*. Nothing is installed.

This is the step that proves the bundle works end to end from a machine that has never had
the SDK on it: AD enumeration, SQLite, the `msi.dll` interop, SMB staging, and WinRM
remoting, all out of one copied folder.

### 6. Portable mode (optional)

Rename `hybridagentdeploy.json.sample` to `hybridagentdeploy.json`, restart the GUI, and
confirm a `data\` folder appears beside the executable holding `inventory.db` and `logs\`.

---

## Recording the result

Tick these off and the Phase 6 acceptance criterion is met:

- [ ] 1 — framework-dependent control **fails** to launch
- [ ] 2 — bundle launches locally
- [ ] 3 — bundle launches from a UNC path
- [ ] 4 — CLI reports 1.0.0 and enumerates
- [ ] 5 — validation runs against the lab and gives the expected verdicts
- [ ] 6 — portable mode works *(optional)*

Then delete `C:\HAD-acceptance\`.

---

## Still outstanding after this

**The bundle is unsigned.** PRD Q3 is deferred, so `tools/publish.ps1` leaves the signing
step as a hook and prints a warning. SEC9 requires an Authenticode signature before the tool
is given to a customer, and the operator README says so at the top. Passing this checklist
means the packaging works — not that the bundle is releasable.
