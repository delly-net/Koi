using System.Xml;
using System.Xml.Linq;

namespace Koi.Api.Packages;

/// <summary>openSUSE / SLES 的 zypper 后端。</summary>
/// <remarks>
/// 查询统一使用 <c>--xmlout</c>，避免解析对齐后的文本表格；变更命令使用
/// <c>--non-interactive</c> 与 <c>-y</c>，保证不会等待交互确认。
/// </remarks>
public sealed class ZypperPackageManager(string executablePath) : PackageManagerBackend
{
    private static readonly string[] GlobalOptions =
        ["--xmlout", "--non-interactive", "--no-refresh"];

    public override string Name => "zypper";

    public override string DisplayName => "zypper";

    public override string ExecutablePath => executablePath;

    public override bool SupportsSourceWrite => true;

    public override PackageCommand VersionCommand => PackageCommand.Of(executablePath, "--version");

    public override PackageCommand InstalledCommand => new(
        executablePath,
        [.. GlobalOptions, "search", "--installed-only", "--type", "package"]);

    public override PackageCommand SearchCommand(string query) => new(
        executablePath,
        [.. GlobalOptions, "search", "--type", "package", query]);

    public override PackageCommand UpdatesCommand => new(
        executablePath,
        [.. GlobalOptions, "list-updates"]);

    public override PackageCommand ActionCommand(string action, IReadOnlyList<string> packages)
    {
        switch (action)
        {
            case PackageActions.Install:
            {
                var arguments = new List<string> { "--non-interactive", "install", "-y" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.Remove:
            {
                var arguments = new List<string> { "--non-interactive", "remove", "-y" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.Upgrade:
            {
                var arguments = new List<string> { "--non-interactive", "update", "-y" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.UpgradeAll:
                return PackageCommand.Of(executablePath, "--non-interactive", "update", "-y");

            case PackageActions.Refresh:
                return PackageCommand.Of(executablePath, "--non-interactive", "refresh");

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "不支持的包管理动作。");
        }
    }

    public override IReadOnlyList<PackageSummary> ParseInstalled(string output) =>
        ParseSolvables(output, installedOnly: true)
            .Select(item => new PackageSummary(item.Name, item.Version, item.Summary))
            .ToArray();

    public override IReadOnlyList<PackageSearchResult> ParseSearch(string output) =>
        ParseSolvables(output, installedOnly: false)
            .Select(item => new PackageSearchResult(item.Name, item.Version, item.Summary, item.Installed))
            .ToArray();

    public override IReadOnlyList<PackageUpdate> ParseUpdates(string output)
    {
        var updates = new List<PackageUpdate>();
        foreach (var element in SafeDocument(output)?.Descendants() ?? [])
        {
            if (!element.Name.LocalName.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Read(element, "name");
            if (!PackageValidators.IsValidPackageName(name))
            {
                continue;
            }

            updates.Add(new PackageUpdate(
                name,
                Read(element, "edition-old", "edition_old", "old"),
                Read(element, "edition", "version")));
        }

        return updates
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<SolvableInfo> ParseSolvables(string output, bool installedOnly)
    {
        var results = new List<SolvableInfo>();
        foreach (var element in SafeDocument(output)?.Descendants() ?? [])
        {
            var localName = element.Name.LocalName;
            if (!localName.Equals("solvable", StringComparison.OrdinalIgnoreCase) &&
                !localName.Equals("package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Read(element, "name");
            if (!PackageValidators.IsValidPackageName(name))
            {
                continue;
            }

            var status = Read(element, "status");
            var installed = status.Contains("installed", StringComparison.OrdinalIgnoreCase) &&
                            !status.Contains("not-installed", StringComparison.OrdinalIgnoreCase);

            if (installedOnly && !installed)
            {
                continue;
            }

            results.Add(new SolvableInfo(
                name,
                Read(element, "edition", "version"),
                Read(element, "summary"),
                installed));
        }

        return results
            .GroupBy(item => $"{item.Name}\n{item.Version}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static XDocument? SafeDocument(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(output, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>读取属性（优先）或子元素文本，名称不区分大小写。</summary>
    private static string Read(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var attribute = element.Attributes()
                .FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null)
            {
                return attribute.Value.Trim();
            }

            var child = element.Elements()
                .FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (child is not null)
            {
                return child.Value.Trim();
            }
        }

        return string.Empty;
    }

    private sealed record SolvableInfo(string Name, string Version, string Summary, bool Installed);
}
