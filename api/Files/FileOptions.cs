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

    /// <summary>普通元数据操作（新建目录/新建文件/重命名）的超时时间（秒）。</summary>
    public int HelperTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 目录列举的超时时间（秒）。大目录逐条目读取属性在慢盘或网络文件系统上可能较慢，
    /// 因此单独配置且默认值高于普通元数据操作。
    /// </summary>
    public int ListTimeoutSeconds { get; set; } = 300;

    /// <summary>删除/复制/移动（含递归与跨文件系统回退）的超时时间（秒）。</summary>
    public int TransferTimeoutSeconds { get; set; } = 1800;

    /// <summary>上传/下载在没有任何数据流动时允许的最长空闲时间（秒）。</summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 上传/下载的总时长上限（秒）；<c>0</c> 表示不限制，仅依靠空闲超时兜底。
    /// 慢速客户端在持续有数据流动时不应被固定墙钟误杀。
    /// </summary>
    public int StreamMaxSeconds { get; set; }
}
