namespace Koi.Api.Services;

/// <summary>systemd 服务管理配置。</summary>
/// <remarks>
/// 配置不再来自 <c>appsettings.json</c>：默认值定义在代码中，运行期以 SQLite 通用配置表
/// （Feature = <c>services</c>，由 root 在服务管理设置抽屉中编辑）为准；数据库无记录时直接使用
/// 这里的默认值。保存后立即生效，无需重启服务。
/// <para>
/// <c>systemctl</c> 服务管理等同于宿主 root 权限（可启停任意系统服务），因此默认只允许管理员组成员使用。
/// 服务进程以服务账号（当前部署为 root）执行 systemctl，登录用户的权限边界由组成员校验保证。
/// 例外：uid 为 0 的账号（root）本身即宿主管理员，解析出 uid 0 后直接放行，不受 <see cref="AllowedGroups"/> 限制。
/// </para>
/// </remarks>
public sealed class ServiceOptions
{
    /// <summary>历史配置节名，仅用于语义标识；运行期不再读取该节。</summary>
    public const string SectionName = "Koi:Services";

    /// <summary>是否启用服务管理接口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// systemctl 可执行文件路径；留空时依次探测 <c>/usr/bin/systemctl</c>、<c>/bin/systemctl</c> 与 <c>PATH</c>。
    /// </summary>
    public string SystemctlPath { get; set; } = string.Empty;

    /// <summary>
    /// journalctl 可执行文件路径；留空时依次探测 <c>/usr/bin/journalctl</c>、<c>/bin/journalctl</c> 与 <c>PATH</c>。
    /// </summary>
    public string JournalctlPath { get; set; } = string.Empty;

    /// <summary>
    /// 允许访问服务管理的系统组名。登录用户必须属于其中至少一个组，否则返回 403。
    /// 空数组表示拒绝所有普通用户；uid 0（root）不受该列表限制，始终放行。
    /// </summary>
    public string[] AllowedGroups { get; set; } = ["sudo", "wheel"];

    /// <summary>
    /// systemctl / journalctl 命令的超时时间（秒）。
    /// 默认 120 秒，需覆盖 systemd 默认 90 秒的服务停止等待时间。
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>单条命令标准输出/标准错误的字节上限，超限终止命令并返回 413。</summary>
    public long MaxOutputBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>服务日志的字节上限，超限终止命令并返回 413。</summary>
    public long MaxLogBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>日志接口未指定 tail 时的默认行数。</summary>
    public int LogTailDefault { get; set; } = 500;

    /// <summary>日志接口允许的最大 tail 行数，超出按该值夹紧。</summary>
    public int LogTailMax { get; set; } = 5000;
}
