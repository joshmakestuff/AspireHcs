# Runs ONCE inside the guest, on the burn-in boot's single autologon (see
# unattend.template.xml). Configures the image's advertised behaviors, records what it did,
# and shuts the guest down so the builder can seal the disk. Every step lands in the base
# image, not in per-run child diffs — that is the point of the burn-in boot.
$ErrorActionPreference = 'Stop'
Start-Transcript -Path C:\Windows\Setup\Scripts\bootstrap-transcript.log

$result = [ordered]@{ startedUtc = (Get-Date).ToUniversalTime().ToString('o') }

try {
    # OpenSSH server is the image's positive TCP health-check fixture: it must listen on 22
    # at every boot with no further configuration.
    Set-Service -Name sshd -StartupType Automatic
    Start-Service -Name sshd
    $result.sshd = (Get-Service sshd).Status.ToString()

    # The capability's inbox firewall rule when present; otherwise create an equivalent.
    # Profile Any: the NAT network's profile classification must not decide reachability.
    $rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
    if ($rule) {
        $rule | Enable-NetFirewallRule
        $rule | Set-NetFirewallRule -Profile Any
    }
    else {
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' `
            -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Any | Out-Null
    }
    $result.firewallRule = 'OpenSSH-Server-In-TCP enabled, profile Any'

    # Remote Desktop, for the dashboard's Connect (RDP) command (#26). Server images ship with
    # it denied, so without this the image has nothing listening on 3389 and the command has
    # nowhere to connect.
    Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' `
        -Name fDenyTSConnections -Value 0 -Type DWord
    $result.fDenyTSConnections = (Get-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server').fDenyTSConnections

    # The canonical group resource string, NOT -DisplayGroup 'Remote Desktop': the display name
    # is localized, so on a non-English image that filter matches nothing and would silently
    # enable no rules at all while appearing to succeed.
    $rdpRules = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue)
    if ($rdpRules.Count -eq 0) {
        throw "No firewall rules found in the Remote Desktop group '@FirewallAPI.dll,-28752'; refusing to claim RDP is reachable."
    }
    $rdpRules | Enable-NetFirewallRule
    $rdpRules | Set-NetFirewallRule -Profile Any
    $result.rdpFirewallRules = $rdpRules.Count

    Set-Service -Name TermService -StartupType Automatic
    if ((Get-Service TermService).Status -ne 'Running') {
        Start-Service -Name TermService
    }
    else {
        # Already running: restart so it picks up fDenyTSConnections.
        Restart-Service -Name TermService -Force
    }

    # A RUNTIME WITNESS, not "the registry value was set": the image may only advertise RDP if
    # something is actually listening on 3389 while the burn-in is still running. The builder
    # turns this into a hard gate at seal time.
    $deadline = (Get-Date).AddSeconds(60)
    $listening = $null
    do {
        $listening = Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue
        if ($listening) { break }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    $result.rdp = if ($listening) { 'Listening' } else { 'NotListening' }

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
