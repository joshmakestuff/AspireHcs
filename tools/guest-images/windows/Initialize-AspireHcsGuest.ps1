# Runs ONCE inside the guest, on the burn-in boot's single autologon (see
# unattend.template.xml). Configures the image's advertised behaviors, records what it did,
# and shuts the guest down so the builder can seal the disk. Every step lands in the base
# image, not in per-run child diffs — that is the point of the burn-in boot.
$ErrorActionPreference = 'Stop'
Start-Transcript -Path C:\Windows\Setup\Scripts\bootstrap-transcript.log

$result = [ordered]@{ startedUtc = (Get-Date).ToUniversalTime().ToString('o') }

# A service reporting Running is not the same as something accepting connections on its port —
# the exact gap WithTcpHealthCheck exists to catch on the host side. Both fixtures this image
# advertises are held to the listening standard, not the service-state one.
function Wait-ForListener {
    param(
        [Parameter(Mandatory)][int]$Port,
        # The service that must own the socket. Without this, any process squatting on the port
        # would satisfy the probe.
        [Parameter(Mandatory)][string]$ServiceName,
        [int]$TimeoutSeconds = 60
    )

    $expectedPid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").ProcessId
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        # The error is captured, not discarded: SilentlyContinue alone would report a provider
        # or access failure as 'NotListening', sending whoever reads the sentinel after a
        # connectivity problem that does not exist.
        $listener = Get-NetTCPConnection -LocalPort $Port -State Listen `
                -ErrorAction SilentlyContinue -ErrorVariable probeError |
            Where-Object { $_.OwningProcess -eq $expectedPid } | Select-Object -First 1
        if ($listener) { return 'Listening' }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    # Get-NetTCPConnection errors when nothing matches, so an error alone does not mean the
    # probe broke — only one that is not the no-matching-object case does. Classified by ERROR
    # CATEGORY, never by message text: this file rejects the localized firewall display name a
    # few lines below for exactly this reason, and matching an English substring here would be
    # the same mistake, mislabelling an idle port as a broken probe on a localized image.
    $realFailure = $probeError |
        Where-Object { $_.CategoryInfo.Category -ne [System.Management.Automation.ErrorCategory]::ObjectNotFound } |
        Select-Object -First 1
    if ($realFailure) {
        return "ProbeFailed: $($realFailure.Exception.Message)"
    }

    # Distinguish "nothing there" from "something else there": they need different fixes. The
    # owner is NAMED rather than just flagged, because a burn-in is a ~30-minute round trip and
    # "who has the port" should not cost a second one.
    # Same treatment as the loop above — this second call can fail on its own account, and a
    # masked failure here would report 'NotListening' just as misleadingly.
    $any = @(Get-NetTCPConnection -LocalPort $Port -State Listen `
        -ErrorAction SilentlyContinue -ErrorVariable ownerProbeError)
    $ownerFailure = $ownerProbeError |
        Where-Object { $_.CategoryInfo.Category -ne [System.Management.Automation.ErrorCategory]::ObjectNotFound } |
        Select-Object -First 1
    if ($ownerFailure) { return "ProbeFailed: $($ownerFailure.Exception.Message)" }
    if ($any.Count -eq 0) { return 'NotListening' }

    $owners = $any | ForEach-Object {
        $proc = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        "$($proc.ProcessName)(pid $($_.OwningProcess))"
    }
    return "ListeningButNot${ServiceName}: expected pid $expectedPid, found $($owners -join ', ')"
}

try {
    # OpenSSH server is the image's positive TCP health-check fixture: it must listen on 22
    # at every boot with no further configuration.
    Set-Service -Name sshd -StartupType Automatic
    Start-Service -Name sshd
    $result.sshd = (Get-Service sshd).Status.ToString()

    # The capability's inbox firewall rule when present; otherwise create an equivalent.
    # Profile Any: the NAT network's profile classification must not decide reachability.
    # Addressed by name rather than by piping the captured object, for the same reason as the
    # RDP rules below: a stale object piped into Set-NetFirewallRule can undo the enable. This
    # rule ships enabled so the old pattern happened to work, which is precisely why it went
    # unnoticed — closing it here too rather than leaving the sibling to fail later.
    $rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
    if ($rule) {
        Set-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -Profile Any
        Enable-NetFirewallRule -Name 'OpenSSH-Server-In-TCP'
    }
    else {
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' `
            -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Any | Out-Null
    }
    $sshRuleAfter = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP'
    if (-not $sshRuleAfter.Enabled) {
        throw "OpenSSH-Server-In-TCP is still disabled after enabling it."
    }
    $result.firewallRule = "OpenSSH-Server-In-TCP enabled=$($sshRuleAfter.Enabled), profile $($sshRuleAfter.Profile)"

    # Held to the same standard as RDP below — added when RDP's probe exposed that sshd's only
    # witness was its service state, which cannot distinguish "running" from "serving".
    $result.sshdListening = Wait-ForListener -Port 22 -ServiceName 'sshd'

    # Remote Desktop, for the dashboard's Connect (RDP) command (#26). Server images ship with
    # it denied, so without this the image has nothing listening on 3389 and the command has
    # nowhere to connect.
    Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' `
        -Name fDenyTSConnections -Value 0 -Type DWord
    $result.fDenyTSConnections = (Get-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server').fDenyTSConnections

    # The canonical group resource string, NOT -DisplayGroup 'Remote Desktop': the display name
    # is localized, so on a non-English image that filter matches nothing and would silently
    # enable no rules at all while appearing to succeed.
    # The error is captured rather than discarded: SilentlyContinue alone would let a real
    # failure (provider unavailable, access denied) be reported as "no rules found", which
    # points at the wrong fix entirely.
    $rdpRules = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752' `
        -ErrorAction SilentlyContinue -ErrorVariable rdpRuleError)
    if ($rdpRules.Count -eq 0) {
        $because = if ($rdpRuleError) { " Get-NetFirewallRule failed: $($rdpRuleError[0].Exception.Message)" } else { '' }
        throw "No firewall rules found in the Remote Desktop group '@FirewallAPI.dll,-28752'; refusing to claim RDP is reachable.$because"
    }
    # Addressed BY GROUP, never by piping the objects captured above. Piping a stale rule object
    # into Set-NetFirewallRule can re-apply the state it was captured with — so
    # `Enable-NetFirewallRule` followed by `$captured | Set-NetFirewallRule -Profile Any` can
    # switch the rules straight back off. sshd's rule ships ENABLED, so the same pattern was
    # harmless there and hid the problem; the RDP rules ship DISABLED, and the 2026-08-03
    # images sealed with RDP listening in-guest but unreachable from the host.
    Set-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -Profile Any
    Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752'

    # RE-QUERIED, not assumed. The previous version recorded how many rules were FOUND, which
    # cannot fail against a rule that failed to enable — and that is exactly the defect it
    # missed. A count of matches is not evidence of a state.
    $rdpAfter = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752')
    $rdpDisabled = @($rdpAfter | Where-Object { -not $_.Enabled })
    if ($rdpDisabled.Count -gt 0) {
        throw "Remote Desktop firewall rules still disabled after enabling: $(($rdpDisabled.Name) -join ', ')."
    }
    $result.rdpFirewallRules = $rdpAfter.Count
    $result.rdpFirewallEnabled = @($rdpAfter | Where-Object Enabled).Count
    $result.rdpFirewallProfiles = (($rdpAfter | ForEach-Object { $_.Profile.ToString() }) | Sort-Object -Unique) -join ','

    # Automatic rather than its default trigger-start, the intent being that the image serves
    # RDP on every boot rather than only when something pokes the trigger. NOTE that the
    # every-boot part is an intent, not a result: the burn-in witnesses this boot only, and
    # nothing here boots the sealed image again. The host-side check after a real VM start is
    # what would establish it.
    Set-Service -Name TermService -StartupType Automatic

    # NOT Restart-Service. MEASURED on the 2026-08-03 burn-in of this image: stopping
    # TermService is denied to Administrator ("Cannot open TermService service on computer '.':
    # Access is denied"), so Restart-Service fails outright. That is one recorded denial on this
    # SKU as Administrator — enough to rule the approach out here, not enough to claim no
    # context anywhere could ever stop the service.
    #
    # EXPECTED, not yet measured: that fDenyTSConnections is honoured dynamically and that
    # enabling the RDP firewall rules trips TermService's start trigger, making a restart
    # unnecessary. If either is wrong the sentinel will say rdp='NotListening' and the build
    # will refuse to seal — which is the test.
    if ((Get-Service TermService).Status -ne 'Running') {
        Start-Service -Name TermService
    }
    $result.termService = (Get-Service TermService).Status.ToString()

    # A RUNTIME WITNESS, not "the registry value was set": the image may only advertise RDP if
    # TermService is actually listening on 3389 while the burn-in is still running. The builder
    # turns this into a hard gate at seal time.
    $result.rdp = Wait-ForListener -Port 3389 -ServiceName 'TermService'

    # The cached answer file carries the (redacted-by-setup, but still) admin credentials
    # config; the sealed base has no reason to keep it.
    Remove-Item C:\Windows\Panther\unattend.xml -Force -ErrorAction SilentlyContinue
    $result.pantherUnattendRemoved = $true

    $result.completedUtc = (Get-Date).ToUniversalTime().ToString('o')
    $result.ok = $true
}
catch {
    $result.error = $_.Exception.ToString()
    $result.ok = $false
    throw
}
finally {
    # The sentinel is what the builder verifies offline after the guest powers off — written
    # in both outcomes so a failed bootstrap is diagnosable, with ok=false making it loud.
    $result | ConvertTo-Json | Set-Content -Path C:\aspirehcs-init.json -Encoding UTF8
    Stop-Transcript
    shutdown.exe /s /t 5 /c "AspireHcs burn-in complete"
}
