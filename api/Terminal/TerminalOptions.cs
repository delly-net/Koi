namespace Koi.Api.Terminal;

/// <summary>在线终端配置，对应 appsettings.json 中的 Koi:Terminal 节。</summary>
public sealed class TerminalOptions
{
    public const string SectionName = "Koi:Terminal";

    /// <summary>是否启用终端能力；关闭后端点直接返回 503。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>指定登录 shell 的绝对路径；留空时使用系统账号的登录 shell。</summary>
    public string Shell { get; set; } = string.Empty;

    /// <summary>传递给 shell 的参数。</summary>
    public string[] ShellArguments { get; set; } = ["-l"];

    /// <summary>单个用户允许的并发终端会话数。</summary>
    public int MaxSessionsPerUser { get; set; } = 4;

    /// <summary>所有用户合计允许的并发终端会话数。</summary>
    public int MaxSessionsTotal { get; set; } = 16;

    /// <summary>单条客户端消息允许的最大字节数。</summary>
    public int MaxInputBytes { get; set; } = 65536;

    /// <summary>未上报尺寸时的默认列数。</summary>
    public int DefaultCols { get; set; } = 120;

    /// <summary>未上报尺寸时的默认行数。</summary>
    public int DefaultRows { get; set; } = 32;

    /// <summary>关闭会话时等待子进程退出的秒数，超时后强制终止。</summary>
    public int CloseTimeoutSeconds { get; set; } = 5;
}
