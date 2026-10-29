namespace Koi.Api.Hosting;

/// <summary>
/// 前端静态文件托管配置（配置节 <c>Koi:StaticFiles</c>）。
/// </summary>
public sealed class KoiStaticFilesOptions
{
    public const string SectionName = "Koi:StaticFiles";

    /// <summary>是否启用静态文件托管，默认启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 静态文件根目录。相对路径基于内容根目录解析，默认 <c>wwwroot</c>。
    /// 目录中缺少 <c>index.html</c> 时自动降级为仅提供 API。
    /// </summary>
    public string RootPath { get; set; } = "wwwroot";
}
