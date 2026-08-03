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
guest leased 172.31.25.153:22
connect command: ssh.exe -p 22 -l Administrator 172.31.25.153
ssh exit 255; stderr: Warning: Permanently added '172.31.25.153' (ED25519) to the list of known hosts.
Administrator@172.31.25.153: Permission denied (publickey,password,keyboard-interactive).
```

Boot to that line: 10 s.

**`Permission denied` is the pass condition.** Reaching authentication means the TCP connect, the
version exchange and the key exchange all completed against a real sshd. The suite holds no
credential for the fixture, and a test that needed one would be asserting the image's password
rather than reachability.

Two further things that run asserts, both against live state rather than a synthesized snapshot:

| Claim | How it is checked |
|---|---|
| The button is live exactly when a connection is possible | `UpdateState` evaluated against the real `Running` snapshot with the real allocation present — must be `Enabled` |
| The command line under test is the product's | The `ArgumentList` comes from `ConnectCommands.BuildSshStartInfo`; the test only *prefixes* batch-mode options, never rewrites it |

## Decisions worth recording

**`ssh -l USER HOST`, not `USER@HOST`.** The user name then never meets a delimiter it could
itself contain, so there is no escaping decision to get wrong.

**Arguments go through `ProcessStartInfo.ArgumentList`, and nothing here escapes argv by hand.**
`ArgumentList` was assumed unavailable alongside `UseShellExecute = true`; that was probed
instead of believed, and it works. A follow-up probe round-tripped a child process's `argv` and
found `has a space`, `has"quote`, `trailing\slash\`, `amp&caret^pipe|` and `Domain\User Name` all
arriving **unchanged** through the ShellExecuteEx path. Hand-rolling an escaper would have been
reimplementing something the framework already does correctly.

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

**Failures are reported, never silent.** A non-interactive AppHost (session 0, no desktop) is
detected rather than discovered: `Process.Start` would still succeed there, leaving an invisible
process and a dashboard claiming success.

## Not verified

- **The click itself.** That `Process.Start` puts a client window on the desktop needs a human to
  watch a window appear, so it is not asserted anywhere. The command line it would carry *is*
  asserted, live, above.
- **RDP against our own fixture image.** `tools/guest-images/windows` does not enable Remote
  Desktop — its provenance `edits` list records sshd and BCD/EMS changes and nothing else — so
  `WithRdpCommand` has no guest here to connect to. The host-side half (the generated file, the
  command line, the gating) is unit-tested; the guest-side half is untested against our image.
  **Do not read `WithRdpCommand` as "RDP works with the AspireHcs guest image".** It means "this
  opens mstsc pointed at the guest", which is a claim about the host.

## Still open

- Enabling Remote Desktop in the guest image bootstrap (`fDenyTSConnections=0` plus the firewall
  group), with a burn-in assertion in `New-WindowsGuestImage.ps1` next to the existing sshd one.
  That change only takes effect in a rebuilt image, so it should land together with a rebuild and
  a live RDP run — not before.
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
