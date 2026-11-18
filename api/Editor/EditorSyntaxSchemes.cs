namespace Koi.Api.Editor;

/// <summary>
/// 文本编辑内置着色方案标识与默认后缀映射。
/// </summary>
/// <remarks>
/// 方案标识必须与前端 <c>ui/src/config/editorFileTypes.ts</c> 的 <c>SYNTAX_SCHEMES</c>
/// 保持一致；默认后缀→方案映射也必须与前端保持一致，避免同一后缀在两端表现不同。
/// </remarks>
public static class EditorSyntaxSchemes
{
    public const string PlainText = "plaintext";
    public const string Config = "config";
    public const string Json = "json";
    public const string Yaml = "yaml";
    public const string Xml = "xml";
    public const string Shell = "shell";
    public const string Nginx = "nginx";
    public const string Systemd = "systemd";
    public const string Dockerfile = "dockerfile";
    public const string Env = "env";

    /// <summary>全部内置方案标识（顺序与前端展示顺序一致）。</summary>
    public static readonly string[] All =
    [
        PlainText,
        Config,
        Json,
        Yaml,
        Xml,
        Shell,
        Nginx,
        Systemd,
        Dockerfile,
        Env,
    ];

    /// <summary>
    /// 内置默认后缀规则（沿用原 <c>EditorOptions.DefaultTextExtensions</c>）。
    /// 仅在数据库没有迁移标记且旧配置缺失时用于首次种子化。
    /// </summary>
    public static readonly string[] DefaultExtensions =
    [
        ".txt",
        ".md",
        ".markdown",
        ".log",
        ".csv",
        ".tsv",
        ".json",
        ".jsonc",
        ".xml",
        ".yml",
        ".yaml",
        ".ini",
        ".conf",
        ".cfg",
        ".properties",
        ".toml",
        ".env",
        ".sh",
        ".bash",
        ".zsh",
        ".fish",
        ".py",
        ".js",
        ".mjs",
        ".cjs",
        ".jsx",
        ".ts",
        ".tsx",
        ".vue",
        ".html",
        ".htm",
        ".css",
        ".scss",
        ".less",
        ".sql",
        ".java",
        ".c",
        ".h",
        ".cpp",
        ".hpp",
        ".cs",
        ".go",
        ".rs",
        ".php",
        ".rb",
        ".pl",
        ".lua",
        ".service",
        ".timer",
        ".desktop",
        ".dockerfile",
        ".gitignore",
        ".gitattributes",
        ".editorconfig",
        "dockerfile",
        "makefile",
    ];

    /// <summary>规范化方案标识：去空白、转小写。</summary>
    public static string Normalize(string? scheme) =>
        scheme?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>判断方案标识是否为内置方案。</summary>
    public static bool IsKnown(string? scheme) =>
        Normalize(scheme) is var value && value.Length > 0 && All.Contains(value, StringComparer.Ordinal);

    /// <summary>按后缀/文件名规则给出默认着色方案；未识别的规则回退纯文本。</summary>
    public static string DefaultSchemeFor(string extension)
    {
        var value = extension?.Trim().ToLowerInvariant() ?? string.Empty;
        return value switch
        {
            ".json" or ".jsonc" => Json,
            ".yml" or ".yaml" => Yaml,
            ".xml" => Xml,
            ".ini" or ".conf" or ".cfg" or ".properties" or ".toml" or ".editorconfig" => Config,
            ".env" => Env,
            ".sh" or ".bash" or ".zsh" or ".fish" => Shell,
            ".service" or ".timer" or ".desktop" => Systemd,
            ".dockerfile" or "dockerfile" => Dockerfile,
            _ => PlainText,
        };
    }
}
