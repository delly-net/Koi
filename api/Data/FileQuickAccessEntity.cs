using System.ComponentModel.DataAnnotations;

namespace Koi.Api.Data;

/// <summary>
/// 文件管理的快速访问目录，按登录账号隔离保存。
/// </summary>
/// <remarks>
/// 每个账号可以维护自己的一份列表（新增 / 移除），(<see cref="UserName"/>, <see cref="Path"/>)
/// 唯一，避免同一目录重复出现。列表只是导航快捷方式，不构成任何权限授予：
/// 打开目录时仍按登录用户的系统权限与管理员配置的允许根目录校验。
/// </remarks>
public class FileQuickAccessEntity
{
    public long Id { get; set; }

    /// <summary>所属登录账号（PAM 认证的系统账号名）。</summary>
    [MaxLength(64)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>目录绝对路径，入库前经 <see cref="Path.GetFullPath(string)"/> 规范化。</summary>
    [MaxLength(4096)]
    public string Path { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
