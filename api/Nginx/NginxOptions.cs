namespace Koi.Api.Nginx;

/// <summary>Nginx 配置管理配置。</summary>
/// <remarks>
/// 与其它系统模块一致，配置不再来自 <c>appsettings.json</c>：默认值定义在代码中，运行期以
/// SQLite 通用配置表（Feature = <c>nginx</c>，由 root 在 Nginx 管理设置抽屉中编辑）为准；
/// 数据库无记录时直接使用这里的默认值，保存后立即生效。
/// <para>
/// Nginx 配置文件的读写由服务进程（当前部署为 root）直接执行，等同宿主管理员能力，
/// 因此默认只允许管理员组成员使用。uid 为 0 的账号本身即宿主管理员，始终放行。
/// </para>
/// </remarks>
public sealed class NginxOptions
{
    /// <summary>历史配置节名，仅用于语义标识；运行期不再读取该节。</summary>
    public const string SectionName = "Koi:Nginx";

    /// <summary>是否启用 Nginx 管理接口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// nginx 可执行文件路径；留空时依次探测 <c>/usr/sbin/nginx</c>、
    /// <c>/usr/local/nginx/sbin/nginx</c>、<c>/usr/bin/nginx</c>、<c>/usr/local/bin/nginx</c>、
    /// <c>/sbin/nginx</c> 与 <c>PATH</c>。
    /// </summary>
    public string NginxPath { get; set; } = string.Empty;

    /// <summary>可在线查看与编辑的配置目录（仅该目录顶层的 <c>*.conf</c> 文件）。</summary>
    public string ConfDirectory { get; set; } = "/etc/nginx/conf.d";

    /// <summary>
    /// nginx 主配置路径；留空时使用 nginx 编译期默认配置。
    /// 非空时 <c>nginx -t</c> 与 <c>nginx -s reload</c> 都会追加 <c>-c &lt;path&gt;</c>。
    /// </summary>
    public string MainConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// 允许访问 Nginx 管理的系统组名。登录用户必须属于其中至少一个组，否则返回 403。
    /// 空数组表示拒绝所有普通用户；uid 0（root）不受该列表限制，始终放行。
    /// </summary>
    public string[] AllowedGroups { get; set; } = ["sudo", "wheel"];

    /// <summary>nginx 命令（检测、热加载、版本查询）的超时时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>单条 nginx 命令输出（stdout + stderr）的字节上限，超限终止命令并返回 413。</summary>
    public long MaxOutputBytes { get; set; } = 1024 * 1024;

    /// <summary>单个配置文件允许读取与保存的字节上限，超限返回 413。</summary>
    public long MaxFileBytes { get; set; } = 1024 * 1024;

    /// <summary>配置文件清单的最大条目数，超出按该值截断并标记 truncated。</summary>
    public int MaxListEntries { get; set; } = 1000;
}
