namespace Koi.Api.Files;

/// <summary>文件管理配置，配置节 <c>Koi:Files</c>。</summary>
public sealed class FileOptions
{
    public const string SectionName = "Koi:Files";

    /// <summary>是否启用文件管理接口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>允许访问的根目录；默认整个文件系统，越权仍由登录用户的系统权限兜底。</summary>
    public string[] AllowedRoots { get; set; } = ["/"];

    /// <summary>单个上传文件的最大字节数。</summary>
    public long MaxUploadBytes { get; set; } = 104_857_600;

    /// <summary>单次目录列举返回的最大条目数。</summary>
    public int MaxListEntries { get; set; } = 5000;

    /// <summary>单次文件辅助进程调用的超时时间（秒）。</summary>
    public int HelperTimeoutSeconds { get; set; } = 120;
}
