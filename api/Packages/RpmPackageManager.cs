using System.Text.RegularExpressions;

namespace Koi.Api.Packages;

/// <summary>RHEL / Fedora / CentOS 系的 dnf 或 yum 后端。</summary>
/// <remarks>
/// 两者命令形态基本一致，仅可执行文件不同；输出均为 “名称.架构 版本 仓库” 的文本表格。
/// 已安装/搜索/更新解析时会去掉常见的架构后缀，便于用户阅读与按名称卸载。
/// </remarks>
public sealed partial class RpmPackageManager(
    string managerName,
    string executablePath) : PackageManagerBackend
{
    [GeneratedRegex(@"\.(x86_64|i[3-6]86|noarch|aarch64|armv7hl|armv6hl|ppc64le|ppc64|s390x|riscv64)$", RegexOptions.Compiled)]
    private static partial Regex ArchSuffixRegex();

    public override string Name => managerName;

    public override string DisplayName => managerName.ToUpperInvariant();

    public override string ExecutablePath => executablePath;

    public override bool SupportsSourceWrite => true;

    public override PackageCommand VersionCommand => PackageCommand.Of(executablePath, "--version");

    public override PackageCommand InstalledCommand =>
        PackageCommand.Of(executablePath, "-q", "list", "installed");

    public override PackageCommand SearchCommand(string query) =>
        PackageCommand.Of(executablePath, "-q", "search", query);

    public override PackageCommand UpdatesCommand =>
        PackageCommand.Of(executablePath, "-q", "list", "updates");

    public override PackageCommand ActionCommand(string action, IReadOnlyList<string> packages)
    {
        switch (action)
        {
            case PackageActions.Install:
            {
                var arguments = new List<string> { "install", "-y" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.Remove:
            {
                var arguments = new List<string> { "remove", "-y" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.Upgrade:
            {
                var arguments = new List<string> { "upgrade", "-y" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.UpgradeAll:
                return PackageCommand.Of(executablePath, "upgrade", "-y");

            case PackageActions.Refresh:
                return PackageCommand.Of(executablePath, "makecache");

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "不支持的包管理动作。");
        }
    }

    public override IReadOnlyList<PackageSummary> ParseInstalled(string output)
    {
        var packages = new List<PackageSummary>();
        foreach (var line in Lines(output))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("Installed Packages", StringComparison.Ordinal) ||
                trimmed.StartsWith("Available Packages", StringComparison.Ordinal) ||
                trimmed.StartsWith("Last metadata expiration", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = Tokens(trimmed);
            if (tokens.Length < 2)
            {
                continue;
            }

            var name = StripArch(tokens[0]);
            if (name.Length == 0)
            {
                continue;
            }

            packages.Add(new PackageSummary(name, tokens[1], string.Empty));
        }

        return packages;
    }

    public override IReadOnlyList<PackageSearchResult> ParseSearch(string output)
    {
        var results = new List<PackageSearchResult>();
        foreach (var line in Lines(output))
        {
            var separator = line.IndexOf(" : ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = StripArch(line[..separator].Trim());
            if (!PackageValidators.IsValidPackageName(name))
            {
                continue;
            }

            results.Add(new PackageSearchResult(
                name,
                string.Empty,
                line[(separator + 3)..].Trim(),
                false));
        }

        return results;
    }

    public override IReadOnlyList<PackageUpdate> ParseUpdates(string output)
    {
        var updates = new List<PackageUpdate>();
        foreach (var line in Lines(output))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("Available Upgrades", StringComparison.Ordinal) ||
                trimmed.StartsWith("Last metadata expiration", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = Tokens(trimmed);
            if (tokens.Length < 2)
            {
                continue;
            }

            var name = StripArch(tokens[0]);
            if (name.Length == 0)
            {
                continue;
            }

            // dnf/yum 的 list updates 只给出可升级版本，当前版本留空由前端显示为“—”。
            updates.Add(new PackageUpdate(name, string.Empty, tokens[1]));
        }

        return updates;
    }

    private static string StripArch(string name) => ArchSuffixRegex().Replace(name, string.Empty);
}
