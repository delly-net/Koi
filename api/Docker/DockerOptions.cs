namespace Koi.Api.Docker;

/// <summary>Docker 容器管理配置，配置节 <c>Koi:Docker</c>。</summary>
/// <remarks>
/// Docker 访问等同于宿主 root 权限（可挂载宿主目录），因此默认只允许 <c>docker</c> 组成员使用。
/// 服务进程以服务账号（当前部署为 root）执行 <c>docker</c> 命令，登录用户的权限边界由组成员校验保证。
/// </remarks>
public sealed class DockerOptions
{
    public const string SectionName = "Koi:Docker";

    /// <summary>是否启用容器管理接口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// docker 可执行文件路径；留空时依次探测 <c>/usr/bin/docker</c>、<c>/usr/local/bin/docker</c>、
    /// <c>/bin/docker</c> 与 <c>PATH</c>。
    /// </summary>
    public string DockerPath { get; set; } = string.Empty;

    /// <summary>
    /// 允许访问容器管理的系统组名。登录用户必须属于其中至少一个组，否则返回 403。
    /// 空数组表示拒绝所有用户。
    /// </summary>
    public string[] AllowedGroups { get; set; } = ["docker"];

    /// <summary>普通 docker 命令（列表、启停、删除、日志、统计等）的超时时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>镜像拉取的超时时间（秒）；拉取可能持续数分钟。</summary>
    public int PullTimeoutSeconds { get; set; } = 600;

    /// <summary>单条命令标准输出/标准错误的字节上限，超限终止命令并返回 413。</summary>
    public long MaxOutputBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>容器日志的字节上限，超限返回 413 并提示缩小 tail。</summary>
    public long MaxLogBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>日志接口未指定 tail 时的默认行数。</summary>
    public int LogTailDefault { get; set; } = 500;

    /// <summary>日志接口允许的最大 tail 行数，超出按该值夹紧。</summary>
    public int LogTailMax { get; set; } = 5000;
}
