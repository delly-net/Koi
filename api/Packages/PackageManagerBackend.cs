using System.Text.RegularExpressions;

namespace Koi.Api.Packages;

/// <summary>
/// 包管理器后端抽象：封装命令构建与输出解析，保证上层端点与任务执行不感知发行版差异。
/// </summary>
/// <remarks>
/// 新增发行版只需实现本类并在 <see cref="PackageManagerDetector"/> 中注册；
/// 所有命令都使用绝对路径或 PATH 中的固定可执行文件，参数数组不经过 shell。
/// </remarks>
public abstract class PackageManagerBackend
{
    public abstract string Name { get; }

    public abstract string DisplayName { get; }

    /// <summary>主可执行文件路径（用于可用性判断与版本查询）。</summary>
    public abstract string ExecutablePath { get; }

    /// <summary>是否支持新增/删除/启停软件源；false 表示只读（pacman）。</summary>
    public abstract bool SupportsSourceWrite { get; }

    public abstract PackageCommand VersionCommand { get; }

    public abstract PackageCommand InstalledCommand { get; }

    public abstract PackageCommand SearchCommand(string query);

    public abstract PackageCommand UpdatesCommand { get; }

    /// <summary>按动作构建命令；<paramref name="action"/> 已由端点校验。</summary>
    public abstract PackageCommand ActionCommand(string action, IReadOnlyList<string> packages);

    public abstract IReadOnlyList<PackageSummary> ParseInstalled(string output);

    public abstract IReadOnlyList<PackageSearchResult> ParseSearch(string output);

    public abstract IReadOnlyList<PackageUpdate> ParseUpdates(string output);

    protected static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(line => line.TrimEnd('\r'));

    protected static string[] Tokens(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    protected static string NormalizeWhitespace(string text) =>
        string.Join(' ', Tokens(text));
}

/// <summary>按配置或可执行文件探测当前系统的包管理器。</summary>
public static class PackageManagerDetector
{
    private static readonly string[] DetectionOrder = ["apt", "dnf", "yum", "pacman", "zypper"];

    private static readonly string[] CandidateDirectories =
    [
        "/usr/bin",
        "/bin",
        "/usr/local/bin",
        "/usr/sbin",
        "/sbin",
    ];

    public static PackageManagerBackend? Detect(PackageOptions settings)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var configured = settings.Manager?.Trim().ToLowerInvariant() ?? string.Empty;
        if (configured.Length > 0)
        {
            return Create(configured, settings);
        }

        foreach (var name in DetectionOrder)
        {
            var backend = Create(name, settings);
            if (backend is not null)
            {
                return backend;
            }
        }

        return null;
    }

    private static PackageManagerBackend? Create(string name, PackageOptions settings)
    {
        switch (name)
        {
            case "apt":
            {
                var aptGet = Locate("apt-get");
                if (aptGet is null)
                {
                    return null;
                }

                return new AptPackageManager(
                    aptGet,
                    Locate("dpkg-query") ?? "dpkg-query",
                    Locate("apt-cache") ?? "apt-cache");
            }

            case "dnf":
            case "yum":
            {
                var executable = Locate(name);
                if (executable is null)
                {
                    return null;
                }

                return new RpmPackageManager(name, executable);
            }

            case "pacman":
            {
                var executable = Locate("pacman");
                return executable is null ? null : new PacmanPackageManager(executable);
            }

            case "zypper":
            {
                var executable = Locate("zypper");
                return executable is null ? null : new ZypperPackageManager(executable);
            }

            default:
                return null;
        }
    }

    /// <summary>定位可执行文件：常见目录优先，其次搜索 PATH。</summary>
    public static string? Locate(string name)
    {
        if (name.Contains('/'))
        {
            return File.Exists(name) ? name : null;
        }

        foreach (var directory in CandidateDirectories)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>单例解析并缓存当前包管理器后端；配置保存后自动失效并重新探测。</summary>
public sealed class PackageManagerResolver : IDisposable
{
    private readonly PackageSettingsService settings;
    private readonly object gate = new();
    private PackageManagerBackend? backend;
    private bool resolved;

    public PackageManagerResolver(PackageSettingsService settings)
    {
        this.settings = settings;
        settings.Changed += Invalidate;
    }

    public PackageManagerBackend? Backend
    {
        get
        {
            lock (gate)
            {
                if (!resolved)
                {
                    backend = PackageManagerDetector.Detect(settings.Current);
                    resolved = true;
                }

                return backend;
            }
        }
    }

    /// <summary>配置变化后丢弃缓存，下次读取时按新配置重新探测。</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            backend = null;
            resolved = false;
        }
    }

    public void Dispose() => settings.Changed -= Invalidate;
}

/// <summary>各后端共用的包名/仓库参数校验。</summary>
public static partial class PackageValidators
{
    /// <summary>包名：字母数字开头，允许 <c>+ . _ : @ / = -</c>，最长 200，禁止空格与控制字符。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9+._:@/=~-]{0,199}$", RegexOptions.Compiled)]
    private static partial Regex PackageNameRegex();

    /// <summary>仓库/源标识：字母数字开头，允许 <c>_ . -</c>，最长 81。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,80}$", RegexOptions.Compiled)]
    private static partial Regex SourceNameRegex();

    /// <summary>apt 的 suite/component 单个词。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9./+_~-]{0,99}$", RegexOptions.Compiled)]
    private static partial Regex AptTokenRegex();

    public static bool IsValidPackageName(string? name) =>
        !string.IsNullOrEmpty(name) && PackageNameRegex().IsMatch(name);

    public static bool IsValidSourceName(string? name) =>
        !string.IsNullOrEmpty(name) && SourceNameRegex().IsMatch(name);

    public static bool IsValidAptTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length is > 0 and <= 8 && tokens.All(token => AptTokenRegex().IsMatch(token));
    }

    /// <summary>仓库地址：只接受 http/https/ftp/file 绝对地址，长度不超过 500。</summary>
    public static bool IsValidRepositoryUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500)
        {
            return false;
        }

        if (value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https" or "ftp" or "file";
    }
}
