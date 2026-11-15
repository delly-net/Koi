using System.Text.RegularExpressions;

namespace Koi.Api.Packages;

/// <summary>Debian / Ubuntu 的 APT + dpkg 后端。</summary>
/// <remarks>
/// 已安装列表走 <c>dpkg-query</c>，搜索走 <c>apt-cache</c>，更新列表使用
/// <c>apt-get --just-print upgrade</c>（只打印计划，不修改系统）。
/// 变更命令统一追加 <c>Dpkg::Options</c>，在非交互模式下保留本地配置文件。
/// </remarks>
public sealed partial class AptPackageManager(
    string aptGetPath,
    string dpkgQueryPath,
    string aptCachePath) : PackageManagerBackend
{
    private static readonly string[] DpkgOptions =
    [
        "-o", "Dpkg::Options::=--force-confdef",
        "-o", "Dpkg::Options::=--force-confold",
    ];

    [GeneratedRegex(@"^Inst\s+(?<name>\S+)\s+\[(?<old>[^\]]+)\]\s+\((?<new>\S+)", RegexOptions.Compiled)]
    private static partial Regex UpgradeLineRegex();

    [GeneratedRegex(@"^Inst\s+(?<name>\S+)\s+\((?<new>\S+)", RegexOptions.Compiled)]
    private static partial Regex NewUpgradeLineRegex();

    public override string Name => "apt";

    public override string DisplayName => "APT";

    public override string ExecutablePath => aptGetPath;

    public override bool SupportsSourceWrite => true;

    public override PackageCommand VersionCommand => PackageCommand.Of(aptGetPath, "--version");

    public override PackageCommand InstalledCommand =>
        new(dpkgQueryPath, ["-W", "-f=${Package}\\t${Version}\\t${Status}\\n"]);

    public override PackageCommand SearchCommand(string query) =>
        PackageCommand.Of(aptCachePath, "search", "--names-only", query);

    public override PackageCommand UpdatesCommand =>
        PackageCommand.Of(aptGetPath, "--just-print", "upgrade");

    public override PackageCommand ActionCommand(string action, IReadOnlyList<string> packages)
    {
        switch (action)
        {
            case PackageActions.Install:
            {
                var arguments = new List<string> { "install", "-y" };
                arguments.AddRange(DpkgOptions);
                arguments.AddRange(packages);
                return new PackageCommand(aptGetPath, arguments);
            }

            case PackageActions.Remove:
            {
                var arguments = new List<string> { "remove", "-y" };
                arguments.AddRange(DpkgOptions);
                arguments.AddRange(packages);
                return new PackageCommand(aptGetPath, arguments);
            }

            case PackageActions.Upgrade:
            {
                var arguments = new List<string> { "install", "-y", "--only-upgrade" };
                arguments.AddRange(DpkgOptions);
                arguments.AddRange(packages);
                return new PackageCommand(aptGetPath, arguments);
            }

            case PackageActions.UpgradeAll:
            {
                var arguments = new List<string> { "upgrade", "-y" };
                arguments.AddRange(DpkgOptions);
                return new PackageCommand(aptGetPath, arguments);
            }

            case PackageActions.Refresh:
                return PackageCommand.Of(aptGetPath, "update");

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "不支持的包管理动作。");
        }
    }

    public override IReadOnlyList<PackageSummary> ParseInstalled(string output)
    {
        var packages = new List<PackageSummary>();
        foreach (var line in Lines(output))
        {
            // dpkg-query 输出：name \t version \t status，status 形如 “install ok installed”。
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var status = parts[2].Trim();
            if (!status.EndsWith(" installed", StringComparison.Ordinal))
            {
                continue;
            }

            var name = parts[0].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            packages.Add(new PackageSummary(name, parts[1].Trim(), string.Empty));
        }

        return packages;
    }

    public override IReadOnlyList<PackageSearchResult> ParseSearch(string output)
    {
        var results = new List<PackageSearchResult>();
        foreach (var line in Lines(output))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            // apt-cache 偶尔返回 “name, name-common” 形式的组合行，只取第一个名称。
            var comma = name.IndexOf(',');
            if (comma > 0)
            {
                name = name[..comma].Trim();
            }

            if (!PackageValidators.IsValidPackageName(name))
            {
                continue;
            }

            results.Add(new PackageSearchResult(name, string.Empty, line[(separator + 3)..].Trim(), false));
        }

        return results;
    }

    public override IReadOnlyList<PackageUpdate> ParseUpdates(string output)
    {
        var updates = new List<PackageUpdate>();
        foreach (var line in Lines(output))
        {
            var match = UpgradeLineRegex().Match(line);
            if (match.Success)
            {
                updates.Add(new PackageUpdate(
                    match.Groups["name"].Value,
                    match.Groups["old"].Value,
                    match.Groups["new"].Value));
                continue;
            }

            var newMatch = NewUpgradeLineRegex().Match(line);
            if (newMatch.Success)
            {
                updates.Add(new PackageUpdate(
                    newMatch.Groups["name"].Value,
                    string.Empty,
                    newMatch.Groups["new"].Value));
            }
        }

        return updates;
    }
}
