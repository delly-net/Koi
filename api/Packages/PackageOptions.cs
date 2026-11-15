namespace Koi.Api.Packages;

/// <summary>
/// Linux 包管理配置。默认值定义在代码中，运行期以 SQLite 通用配置表
/// （Feature = <c>packages</c>，由 root 在应用管理设置抽屉中编辑）为准。
/// </summary>
/// <remarks>
/// 包管理操作等同于宿主 root 权限（安装、卸载、修改软件源），因此默认只允许
/// <c>sudo</c> / <c>wheel</c> 组成员使用；uid 0（root）账号本身即宿主管理员，始终放行。
/// 包管理器命令由 Koi 服务进程执行，因此写操作还要求服务进程本身以 root 运行。
/// </remarks>
public sealed class PackageOptions
{
    /// <summary>是否启用应用管理接口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 强制使用的包管理器：<c>apt</c> / <c>dnf</c> / <c>yum</c> / <c>pacman</c> / <c>zypper</c>。
    /// 留空时按该顺序自动探测可执行文件。
    /// </summary>
    public string Manager { get; set; } = string.Empty;

    /// <summary>
    /// 允许访问应用管理的系统组名。登录用户必须属于其中至少一个组，否则返回 403。
    /// 空数组表示拒绝所有普通用户；uid 0（root）不受该列表限制。
    /// </summary>
    public string[] AllowedGroups { get; set; } = ["sudo", "wheel"];

    /// <summary>查询类命令（已安装列表、搜索、更新列表、源列表）的超时时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>安装/卸载/升级/刷新索引等后台任务的超时时间（秒）。</summary>
    public int JobTimeoutSeconds { get; set; } = 1800;

    /// <summary>查询类命令单个流的输出字节上限，超限终止命令并返回 413。</summary>
    public long MaxOutputBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>后台任务日志的字节上限，超限终止命令并标记任务失败。</summary>
    public long MaxLogBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>已结束任务在内存中保留的分钟数，超过后从任务表清理。</summary>
    public int JobRetentionMinutes { get; set; } = 30;

    /// <summary>单个任务允许的目标包数量上限。</summary>
    public int MaxPackagesPerJob { get; set; } = 50;

    /// <summary>已安装列表接口返回的最大条目数，超出时 <c>truncated=true</c>。</summary>
    public int MaxInstalledEntries { get; set; } = 5000;
}
