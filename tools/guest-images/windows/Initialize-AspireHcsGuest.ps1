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
