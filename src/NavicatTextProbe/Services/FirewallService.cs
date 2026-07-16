using System.Diagnostics;

namespace NavicatTextProbe.Services;

internal static class FirewallService
{
    private const string RuleName = "Assistant Mobile";

    public static async Task<bool> IsRuleEnabledAsync(int port, CancellationToken cancellationToken = default)
    {
        var script =
            $"$r=Get-NetFirewallRule -DisplayName '{RuleName}' -ErrorAction SilentlyContinue | Where-Object {{$_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow'}}; " +
            $"if($r){{$p=$r | Get-NetFirewallPortFilter | Where-Object {{$_.Protocol -eq 'TCP' -and $_.LocalPort -eq '{port}'}}; if($p){{exit 0}}}}; exit 1";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null) return false;
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0;
    }

    public static async Task<(bool Success, string Message)> EnsureRuleAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        var script =
            $"Get-NetFirewallRule -DisplayName '{RuleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
            $"New-NetFirewallRule -DisplayName '{RuleName}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} " +
            "-Profile Private,Public -RemoteAddress LocalSubnet | Out-Null";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            });
            if (process is null) return (false, "无法启动管理员权限配置程序。");
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? (true, $"已允许同一局域网访问 TCP {port}。")
                : (false, $"防火墙命令退出代码：{process.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "你取消了 Windows 管理员权限确认，防火墙未修改。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
