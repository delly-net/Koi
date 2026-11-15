namespace Koi.Api.Terminal;

/// <summary>
/// 终端配置的规范化与校验。配置来自界面输入，必须由服务端做范围与合法性检查。
/// </summary>
public static class TerminalSettingsValidator
{
    private const int MaxShellLength = 1024;
    private const int MaxArgumentCount = 16;
    private const int MaxArgumentLength = 256;
    private const int MaxInputByteLimit = 4 * 1024 * 1024;

    /// <summary>规范化：Shell 去空白，启动参数逐项去空白并丢弃空项。</summary>
    public static TerminalOptions Normalize(TerminalOptions settings)
    {
        settings.Shell = settings.Shell?.Trim() ?? string.Empty;
        settings.ShellArguments = (settings.ShellArguments ?? [])
            .Select(argument => argument?.Trim() ?? string.Empty)
            .Where(argument => argument.Length > 0)
            .ToArray();

        return settings;
    }

    /// <summary>校验配置；返回 <c>null</c> 表示合法，否则返回中文错误详情。</summary>
    public static string? Validate(TerminalOptions settings)
    {
        var shell = settings.Shell ?? string.Empty;
        if (shell.Length > MaxShellLength)
        {
            return $"Shell 路径不能超过 {MaxShellLength} 个字符。";
        }

        if (shell.Length > 0)
        {
            if (!shell.StartsWith('/'))
            {
                return "Shell 必须是绝对路径；留空表示使用系统账号的登录 Shell。";
            }

            if (shell.Contains('\0') || shell.Any(char.IsControl))
            {
                return "Shell 路径不能包含控制字符。";
            }

            // 非 Linux 主机同样允许保存配置，仅 Linux 上校验文件是否存在。
            if (OperatingSystem.IsLinux() && !File.Exists(shell))
            {
                return $"Shell 路径“{shell}”不存在。";
            }
        }

        var arguments = settings.ShellArguments ?? [];
        if (arguments.Length > MaxArgumentCount)
        {
            return $"Shell 启动参数最多 {MaxArgumentCount} 项。";
        }

        foreach (var argument in arguments)
        {
            if (argument.Length > MaxArgumentLength)
            {
                return $"启动参数“{argument}”超过 {MaxArgumentLength} 个字符。";
            }

            if (argument.Contains('\0') || argument.Any(char.IsControl))
            {
                return "启动参数不能包含控制字符。";
            }
        }

        if (settings.MaxSessionsPerUser is < 1 or > 128)
        {
            return "每个账号的并发终端数必须在 1 到 128 之间。";
        }

        if (settings.MaxSessionsTotal is < 1 or > 1024)
        {
            return "终端会话总数上限必须在 1 到 1024 之间。";
        }

        if (settings.MaxInputBytes is < 1024 or > MaxInputByteLimit)
        {
            return "单条输入上限必须在 1 KiB 到 4096 KiB 之间。";
        }

        if (settings.DefaultCols is < 1 or > 1000)
        {
            return "默认列数必须在 1 到 1000 之间。";
        }

        if (settings.DefaultRows is < 1 or > 1000)
        {
            return "默认行数必须在 1 到 1000 之间。";
        }

        if (settings.CloseTimeoutSeconds is < 1 or > 120)
        {
            return "关闭会话超时必须在 1 到 120 秒之间。";
        }

        return null;
    }
}
