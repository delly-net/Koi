namespace Koi.Api.Editor;

/// <summary>
/// 文本编辑配置。
/// </summary>
/// <remarks>
/// 运行期配置不再来自 <c>appsettings.json</c>，也不再读取前端 <c>conf/setting.json</c> 的
/// <c>fileAssociations.text</c>：由 <see cref="EditorSettingsService"/> 从 SQLite 通用配置表
/// （<c>Feature = editor</c>）读取。文本编辑页的设置抽屉仅允许 root 修改，所有登录用户通过
/// 只读接口读取同一份扩展名规则。
/// </remarks>
public sealed class EditorOptions
{
    /// <summary>
    /// 内置文本关联规则：接口不可用或数据库配置损坏时的兜底值，同时用于首次写入配置行。
    /// </summary>
    /// <remarks>
    /// 规则与前端 <c>DEFAULT_TEXT_EXTENSIONS</c> 保持一致，避免后端种子值与前端兜底值不同。
    /// </remarks>
    public static readonly string[] DefaultTextExtensions =
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

    /// <summary>
    /// 被视为文本的扩展名/文件名规则；空数组表示文件管理中不把任何文件视为文本。
    /// 以 <c>.</c> 开头的规则按完整文件名或后缀匹配，其余规则按完整文件名或
    /// <c>.规则</c> 后缀匹配。
    /// </summary>
    public string[] TextExtensions { get; set; } = [.. DefaultTextExtensions];
}
