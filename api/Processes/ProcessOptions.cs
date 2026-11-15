namespace Koi.Api.Processes;

/// <summary>Linux 进程管理配置。</summary>
/// <remarks>
/// 配置不来自 <c>appsettings.json</c>：默认值定义在代码中，运行期以 SQLite 通用配置表
/// （Feature = <c>process</c>，由 root 在进程管理设置抽屉中编辑）为准；数据库无记录时直接使用
/// 这里的默认值。保存后立即生效，无需重启服务。
/// <para>
/// 进程列表会暴露全部账号与命令行，结束进程等同于向任意进程发送信号，因此默认只允许管理员组
/// （sudo / wheel）使用。uid 为 0 的账号本身即宿主管理员，解析出 uid 0 后直接放行。
/// </para>
/// </remarks>
public sealed class ProcessOptions
{
    /// <summary>历史配置节名，仅用于语义标识；运行期不再读取该节。</summary>
    public const string SectionName = "Koi:Processes";

    /// <summary>是否启用进程管理接口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 允许访问进程管理的系统组名。登录用户必须属于其中至少一个组，否则返回 403。
    /// 空数组表示拒绝所有普通用户；uid 0（root）不受该列表限制，始终放行。
    /// </summary>
    public string[] AllowedGroups { get; set; } = ["sudo", "wheel"];

    /// <summary>单次列表最多返回的进程数（按 CPU 降序截断），超出时响应标记 truncated。</summary>
    public int MaxProcesses { get; set; } = 2000;

    /// <summary>返回的命令行最大字符数，超出截断并追加省略号；避免巨型 argv 撑大响应。</summary>
    public int MaxCommandLineLength { get; set; } = 512;

    /// <summary>
    /// 首次采样或距上次采样超过 30 秒时，内部短采样窗口（毫秒）。
    /// CPU 占比必须依赖两次计数器的差值，该窗口决定首屏数据的精度。
    /// </summary>
    public int SampleWindowMilliseconds { get; set; } = 200;

    /// <summary>发送 SIGTERM 后等待进程退出的最长时间（秒）；超时由端点返回 409。</summary>
    public int TerminateTimeoutSeconds { get; set; } = 5;

    /// <summary>前端自动刷新间隔（秒），随进程列表响应返回，普通账号也可读取。</summary>
    public int RefreshIntervalSeconds { get; set; } = 5;
}
