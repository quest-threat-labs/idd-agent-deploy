# Manual test matrix (PRD §15.3)

PRD §15.3 says to document these rather than automate them. This is that document: what to
do, what should happen, and — importantly — which entries need lab conditions that do not
currently exist in the `titancorp.local` forest.

Everything here is driven through the GUI, which is what the Phase 5 acceptance criterion
asks for. The equivalent CLI commands are noted where they help set a case up.

**Nothing below should be run against production domain controllers.**

---

## Lab state as of Phase 5

| DC | OS | Domain | Site | Agent installed |
|---|---|---|---|---|
| `RnD-DC.research.titancorp.local` | Server 2025 | child | Titancorp-CVG | 7.7.34005.0 |
| `DC2.titancorp.local` | Server 2025 | root | Titancorp-CVG | 7.6.35001.2 |
| `dc4.titancorp.local` | Server 2019 | root | Titancorp-CVG | 7.6.35001.2 |
| `DC5.titancorp.local` | Server 2019 | root | Titancorp-CVG | 7.6.35001.2 |
| `DC6.titancorp.local` | Server 2022 | root | Titancorp-CVG | 7.6.35001.2 |

All five are in one site, so §R7.2 caps a full-forest run at **2 concurrent** whatever
max-parallel says. That is expected, not a defect — the run log states the limit in effect.

---

## The matrix

### 1. Deployment to an RODC

**Cannot be tested in this lab.** All five domain controllers are writable; the forest has no
read-only DC. The RODC column reads "no" for every row, which is correct but proves nothing.

To test: promote an RODC, re-enumerate, confirm the RODC column shows `yes`, then deploy to
it. Until then this row is untested and should be recorded as such.

### 2. Target already has a newer agent (expect 1638)

**Cannot be tested with the current package.** 1638 requires installing a package when a
*newer* version of the same product is present. The only MSI available is 7.7.34005.0, which
is the newest version in the forest.

To test: obtain the **7.6** agent MSI and deploy it to `RnD-DC` (which runs 7.7). The 7.7
package's `Upgrade` table has a downgrade-detect row covering versions above 7.7.34005.0, so
the older installer should refuse. This is also the safest possible install test — a blocked
downgrade changes nothing on the target.

Note that deploying 7.7 to any of the 7.6 DCs is a **major upgrade**, not a 1638: the upgrade
row matches, 7.6 is removed and 7.7 installed.

### 3. Target with a pending reboot (expect 3010)

Arrangeable. On a lab DC, create a pending-reboot condition (for example rename the computer
without restarting, or set
`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations`), then
deploy.

**Expected:** the target completes with exit code 3010, is counted as a **success**, and the
outcome column reads `success - reboot pending` in amber rather than green. The completion
dialog should not treat it as a failure.

### 4. Operator account lacking local admin on one DC of many

Arrangeable, and worth doing — it exercises the circuit breaker's most important case.

Remove the deploying account from local Administrators on **one** DC, select several, and
deploy.

**Expected:** that DC fails at pre-flight or staging with a message naming the host and
saying to confirm local administrator rights. The other DCs continue (§R7.4). The run does
**not** halt, because one failure is not three consecutive ones.

To see the breaker trip instead, remove rights on **three** DCs and deploy with max-parallel
1 so the failures are consecutive. Expected: the run halts after the third, remaining targets
are recorded as `Skipped`, and the completion dialog leads with the halt reason.

### 5. Target with WinRM disabled

Arrangeable. `Stop-Service WinRM` on one lab DC, then use **test-connectivity** before
deploying.

**Expected:** that DC reports unreachable with a message naming TCP 5985 and telling the
operator to confirm the WinRM service. Staging is never attempted, so nothing is written to
the target.

Remember to restart WinRM afterwards.

### 6. Mid-run cancellation

Arrangeable with no special setup, and the most important behaviour to see with your own eyes.

Start a multi-DC deployment, then press **Cancel** on the Progress tab while targets are in
flight.

**Expected:**
- The confirmation explains that in-flight targets will finish and be cleaned up.
- Targets already running complete or time out — they are **not** abandoned.
- Targets not yet started are recorded as `Skipped`.
- Every target that reached staging has its staging directory removed. Verify directly:
  `Get-ChildItem \\<dc>\C$\Windows\Temp\HybridAgentDeploy` should be empty.
- The window refuses to close while the run is still finishing.

---

## GUI checks not in §15.3 but worth doing once

| Check | Expected |
|---|---|
| Start with an empty inventory | Grid empty; Deploy tab blocks with "Select at least one domain controller" |
| Enumerate | All five DCs appear with site and OS; tags and history preserved across re-runs |
| Filter by tag, then Select all | Only visible rows are ticked; the status line reports hidden selections |
| Tick DCs, then change the filter | Selection count is unchanged; hidden count appears |
| Deploy with hidden selections | Confirmation dialog says "N of these are hidden by the current filter" |
| Pick a non-agent MSI | Warning about the product name, but deployment is still permitted (§5.5) |
| Org ID containing `"` | Start stays disabled with an explanation; other metacharacters only warn |
| Command preview | Matches the command in `run.log` after the run, exactly |
| Delete a tag | Tag disappears; the domain controllers and their history do not |
| Try to close mid-run | Refused, with an explanation |
| History → double-click a result | Opens that DC's retrieved msiexec log |
| History → Export run to CSV | Opens in Excel with the error detail intact in one cell |

---

## What automated tests already cover

Do not spend manual time re-checking these — 351 automated tests cover them, and the lab
tests run against real domain controllers:

- Exit-code mapping for every row of §10.1, including 1618 retry and the 3010/1641 successes.
- Concurrency never exceeding 5, and slots releasing only after cleanup.
- The site guard, including the two-DC case.
- Circuit breaker tripping on three consecutive operator-side failures and *not* on install
  failures.
- Command construction and injection neutralisation.
- Inventory filtering, selection across filter changes, and outcome colour mapping.
- SMB staging, remote SHA-256 verification, log retrieval, and cleanup against three real DCs.
- A full end-to-end install on a lab DC (gated behind `HAD_ALLOW_INSTALL=yes`).
