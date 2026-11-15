using System.ComponentModel.DataAnnotations;

namespace Koi.Api.Data;

/// <summary>
/// 已安装软件的数据库缓存。
/// </summary>
/// <remarks>
/// 缓存以包名为唯一键：刷新缓存时写入/更新系统当前已安装的软件，
/// 系统中已不存在的记录标记为 <see cref="PackageCacheStatuses.Removed"/> 并保留。
/// 「简介」由用户维护：仅在新记录首次写入时用包管理器描述预填，
/// 后续刷新不会覆盖（包括用户主动清空后的空字符串）。
/// </remarks>
public class PackageCacheEntity
{
    public long Id { get; set; }

    /// <summary>软件包名称，全局唯一。</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>最近一次刷新时系统报告的版本。</summary>
    [MaxLength(200)]
    public string Version { get; set; } = string.Empty;

    /// <summary>简介。首次缓存时预填包管理器描述，之后只由用户修改。</summary>
    [MaxLength(500)]
    public string Intro { get; set; } = string.Empty;

    /// <summary>安装状态，取值见 <see cref="PackageCacheStatuses"/>。</summary>
    [MaxLength(16)]
    public string Status { get; set; } = PackageCacheStatuses.Installed;

    /// <summary>写入该记录时的包管理器名称（apt / dnf / yum / pacman / zypper）。</summary>
    [MaxLength(16)]
    public string Manager { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>最近一次变更（刷新或简介编辑）时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>最近一次缓存刷新处理该记录的时间；简介编辑不改变该值。</summary>
    public DateTimeOffset RefreshedAt { get; set; }
}

/// <summary>缓存记录的安装状态（与前端约定）。</summary>
public static class PackageCacheStatuses
{
    public const string Installed = "installed";
    public const string Removed = "removed";
}
