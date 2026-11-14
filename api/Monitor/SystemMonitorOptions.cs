namespace Koi.Api.Monitor;

/// <summary>服务器状态采集配置，配置节 <c>Koi:SystemMonitor</c>。</summary>
/// <remarks>
/// 指标全部来自 Linux <c>/proc</c> 与 statvfs，只包含聚合数据（不含用户名、进程明细等敏感信息），
/// 因此接口对所有已登录用户开放，不额外做组成员限制。
/// </remarks>
public sealed class SystemMonitorOptions
{
    public const string SectionName = "Koi:SystemMonitor";

    /// <summary>是否启用服务器状态接口。</summary>
    public bool Enabled { get; set; } = true;
}
