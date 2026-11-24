namespace MCPanel;

/// <summary>
/// PowerShell functions for MCPanel's firewall rules, embedded into generated
/// scripts. They use the HNetCfg.FwPolicy2 COM API, available since Windows
/// Vista / Server 2008, instead of New-NetFirewallRule (Windows 8 / Server
/// 2012 and later only), which silently did nothing on Windows 7 and Server
/// 2008 R2. Adding a rule first removes rules with the same name, so repeated
/// installs no longer create duplicates.
/// </summary>
internal static class WindowsFirewallScript
{
    internal const string Functions = """
        function Remove-McPanelFirewallRule([string]$Name) {
            try {
                $policy = New-Object -ComObject HNetCfg.FwPolicy2
                for ($attempt = 0; $attempt -lt 32; $attempt++) {
                    $existing = @($policy.Rules | Where-Object { $_.Name -eq $Name })
                    if ($existing.Count -eq 0) { break }
                    $policy.Rules.Remove($Name)
                }
            } catch { Write-Output ('防火墙规则清理失败：' + $Name + '：' + $_.Exception.Message) }
        }

        function Remove-McPanelFirewallRules([string]$Pattern) {
            try {
                $policy = New-Object -ComObject HNetCfg.FwPolicy2
                $names = @($policy.Rules | Where-Object { $_.Name -like $Pattern } | ForEach-Object { $_.Name } | Select-Object -Unique)
                foreach ($name in $names) { Remove-McPanelFirewallRule $name }
            } catch { Write-Output ('防火墙规则清理失败：' + $Pattern + '：' + $_.Exception.Message) }
        }

        function Set-McPanelFirewallPort([string]$Name, [string]$Protocol, [int]$Port) {
            try {
                Remove-McPanelFirewallRule $Name
                $policy = New-Object -ComObject HNetCfg.FwPolicy2
                $rule = New-Object -ComObject HNetCfg.FWRule
                $rule.Name = $Name
                $rule.Protocol = $(if ($Protocol -eq 'UDP') { 17 } else { 6 })
                $rule.LocalPorts = [string]$Port
                $rule.Direction = 1
                $rule.Action = 1
                $rule.Profiles = 0x7FFFFFFF
                $rule.Enabled = $true
                $policy.Rules.Add($rule)
                Write-Output ('已放行防火墙入站端口：' + $Name)
            } catch { Write-Output ('防火墙规则配置失败：' + $Name + '：' + $_.Exception.Message) }
        }
        """;
}
