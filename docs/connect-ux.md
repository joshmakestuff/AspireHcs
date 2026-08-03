# Connect-to-VM UX

How [#26](https://github.com/joshmakestuff/AspireHcs/issues/26) is answered: one click in the
dashboard opens a session inside a running guest, without hunting for the leased address.

- Host: Windows 11 Enterprise 10.0.26200, Hyper-V enabled
- Account: a normal user in **Hyper-V Administrators**, running **unelevated**
- Guest: `tools/guest-images/windows` fixture — Windows Server 2025 Core, image build
  10.0.26100.6584, sshd Running (`winserver2025-core.provenance.json`)
- Base commit: `f44f1af`
- Date: 2026-08-03

## What a developer actually does

```csharp
builder.AddHcsVm("appliance")
    .WithVhdx(vhdx)
    .WithNatNetwork()
    .WithEndpoint("ssh", targetPort: 22)
    .WithSshCommand(userName: "Administrator");
```

A **Connect (SSH)** button appears on the resource in the dashboard. It is disabled until the
guest is running *and* its DHCP lease has surfaced; clicking it opens an SSH client on the host,
already pointed at the guest. `WithRdpCommand` is the same shape for Remote Desktop.

## Why the client runs host-side

In run mode the AppHost and the browser showing the dashboard are on the same machine — the
assumption the whole of Aspire's local dev loop already makes. So "one click into the guest" is a
process launch on the developer's own desktop, and the guest has to cooperate with exactly
nothing beyond serving SSH or RDP.

The rejected alternatives, both from the issue:

- **Published `ssh://` URLs.** Windows registers no default handler for the scheme, and there is
  no `rdp://` scheme at all. A link that does nothing on a stock box is worse than a button.
- **An embedded terminal in the dashboard.** The dashboard is not extensible that way today.

## Recorded results

`ConnectCommandLiveTests.Ssh_connect_command_line_reaches_the_guest_sshd`, run against the
fixture image:

```
guest leased 172.31.21.16:22
CurrentState("appliance") -> Running
connect command: ssh.exe -p 22 -l Administrator 172.31.21.16
ssh exit 255; stderr: Warning: Permanently added '172.31.21.16' (ED25519) to the list of known hosts.
Administrator@172.31.21.16: Permission denied (publickey,password,keyboard-interactive).
```

(The address is whatever the Default Switch leases that run; it differs between runs.)

Boot to that line: 10 s.

**`Permission denied` is the pass condition.** Reaching authentication means the TCP connect, the
version exchange and the key exchange all completed against a real sshd. The suite holds no
credential for the fixture, and a test that needed one would be asserting the image's password
rather than reachability.

Two further things that run asserts, both against live state rather than a synthesized snapshot:

| Claim | How it is checked |
|---|---|
| The button is live once the guest is running and has an address | `UpdateState` evaluated against the real `Running` snapshot with the real allocation present — must be `Enabled` |
| The command line under test is the product's | The `ArgumentList` comes from `ConnectCommands.BuildSshStartInfo`; the test only *prefixes* batch-mode options, never rewrites it |

Running plus an address is a *necessary* condition, not a sufficient one. An allocated endpoint
can still refuse TCP — that is precisely what `WithTcpHealthCheck` exists to detect — so an
enabled button means "there is an address to try", not "the service is listening".

## Decisions worth recording

**`ssh -l USER HOST`, not `USER@HOST`.** The user name then never meets a delimiter it could
itself contain, so there is no escaping decision to get wrong.

**Arguments go through `ProcessStartInfo.ArgumentList`, and nothing here escapes argv by hand.**
`ArgumentList` was assumed unavailable alongside `UseShellExecute = true`; that was probed
instead of believed, and it works. A follow-up probe round-tripped a child process's `argv` and
found `has a space`, `has"quote`, `trailing\slash\`, `amp&caret^pipe|` and `Domain\User Name` all
arriving **unchanged** through the ShellExecuteEx path. Hand-rolling an escaper would have been
reimplementing something the framework already does correctly.

That probe is committed as [`docs/probes/argv-roundtrip.cs`](probes/argv-roundtrip.cs) — run
`dotnet run docs/probes/argv-roundtrip.cs`; exit 0 means every argument round-tripped. It is a
probe rather than a unit test because asserting it needs a purpose-built child executable, and
the test projects have no such binary to launch. Recorded result on the reference host,
2026-08-03: `sent 6 args, got 6: IDENTICAL`.

**`UseShellExecute = true` is load-bearing, not incidental.** It gives the client its own console
instead of making it share the AppHost's, where it would compete for stdin and scribble over the
log stream. On Windows 11 that console is whatever the user set as their default terminal, which
is how Windows Terminal gets used without this code knowing Windows Terminal exists.

**The `.rdp` file is generated, because mstsc has no switch for the user name.** That makes it a
syntax boundary — the format is line-based `name:type:value`, so a value carrying a newline would
not be escaped, it would become another setting. Values therefore go through `RdpFile.Field`,
which rejects control characters rather than sanitizing them, and `WithRdpCommand` applies the
same check at model-build time so a bad user name fails the build rather than the click.

**Running is not sufficient to enable the button.** The guest reaches `Running` before its DHCP
lease surfaces. A button that is live during that window produces a failed connection instead of
a wait, so availability requires an allocated address as well — and the unit theory covers the
`Running` + not-yet-allocated case specifically.

**The availability gate is not the enforcement.** `UpdateState` governs what the dashboard
*offers*, but the command stays reachable through Aspire's command APIs, and the orchestrator
assigns `AllocatedEndpoint` and never clears it — so an allocation outlives the VM that earned
it. Without a second check, invoking the command on a stopped VM would launch a client at the
previous run's address. `Execute` therefore refuses on a known terminal state itself. It
deliberately does *not* refuse when the state cannot be determined: the resource id the state is
looked up by is not guaranteed to equal the resource name, and a lookup miss must not turn into a
feature that silently stops working.

**Failures are reported, never silent.** A non-interactive AppHost (session 0, no desktop) is
detected rather than discovered: `Process.Start` would still succeed there, leaving an invisible
process and a dashboard claiming success.

**An explicitly empty user name is rejected, not treated as unset.** `null` means "unspecified"
and is a supported choice; `""` means somebody meant to name an account and named none, and
falling back to the host account (ssh) or the last cached one (mstsc) would connect as somebody
other than who was asked for.

## Not verified

- **The click itself.** That `Process.Start` puts a client window on the desktop needs a human to
  watch a window appear, so it is not asserted anywhere. The command line it would carry *is*
  asserted, live, above.
- **RDP end to end.** Two images were built on 2026-08-03 with the RDP edit
  (`winserver2025-core-rdp.vhdx` and `winserver2025-desktop.vhdx`), both sealing with
  `burnIn.rdp: "Listening"`. **They are still not reachable on 3389 from the host.**
  `Rdp_connect_command_reaches_a_guest_that_serves_remote_desktop` boots one and retries for two
  minutes: every attempt times out, never refuses. A guest with nothing listening sends RST, so
  a silent drop is the firewall — while SSH on 22 from the same guest works.

  The cause and the lesson are the same thing: **a listener inside the guest is not
  reachability.** Local sockets are visible regardless of the firewall, so the listener probe
  could never have caught this, and the sentinel recorded how many firewall rules were *found*
  rather than how many were *enabled* — a check that cannot fail against the defect it exists
  for. The suspected mechanism is piping stale rule objects
  (`$captured | Set-NetFirewallRule -Profile Any` after `Enable-NetFirewallRule`) re-applying
  the captured `Enabled = False`; sshd's rule ships enabled, so the identical pattern was
  harmless there and hid it. Changed — not yet "fixed", since no image has been built from it —
  by addressing rules by group, enabling last, re-querying, and gating the seal on the enabled
  count and the profile.

  The first version of that gate was itself inert, which is worth recording. `Get-NetFirewallRule`
  returns `Enabled` as an **enum** (`True` = 1, `False` = 2), and every nonzero enum is truthy in
  PowerShell. Measured on the reference host: for a *disabled* rule, `[bool]$r.Enabled` is `True`,
  `-not $r.Enabled` is `False`, and `Where-Object Enabled` matches it. Written the natural way,
  the check counted disabled rules as enabled and could never have failed — the same
  cannot-fail-against-its-own-defect shape as the thing it was written to catch. Comparisons are
  now against `.ToString()`, verified to discriminate both ways.

  So `WithRdpCommand` still means "this opens mstsc pointed at the guest", a claim about the
  host. **Do not read it as "RDP works with the AspireHcs guest image."**

## Still open

- **Building an image from the RDP change and proving it live.** The bootstrap now sets
  `fDenyTSConnections=0`, opens the Remote Desktop firewall group by its canonical resource
  string, and records a listener witness that `New-WindowsGuestImage.ps1` gates sealing on. None
  of that is worth anything until an image is built from it and a host-side connection to 3389
  is observed. Until then the sample AppHost stays deliberately un-wired for RDP — shipping a
  button that cannot connect is the thing this is meant to fix.
- Credentials beyond a prefilled user name. mstsc prompts for the password and ssh prompts for
  everything; `IInteractionService` could supply them, at the cost of the AppHost holding guest
  credentials.
- Linux guests: the same `WithSshCommand` applies unchanged, but no Linux fixture image exists
  yet ([#28](https://github.com/joshmakestuff/AspireHcs/issues/28)).

## Reproducing

```powershell
$env:HCS_TEST_WINDOWS_VHDX = '<vhd>\aspirehcs-guests\winserver2025-core.vhdx'
dotnet test tests/AspireHcs.IntegrationTests/AspireHcs.IntegrationTests.csproj -c Release `
    --filter 'FullyQualifiedName~ConnectCommandLiveTests'
```

Without `HCS_TEST_WINDOWS_VHDX` the test skips, like the rest of the guest-dependent suite.
