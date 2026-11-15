namespace Koi.Api.Packages;

/// <summary>Arch Linux 的 pacman 后端。</summary>
/// <remarks>
/// 软件源定义在 <c>/etc/pacman.conf</c> 的仓库段与镜像文件中，结构改动风险较高，
/// 因此仅支持查看，不支持新增/删除/启停。
/// </remarks>
public sealed class PacmanPackageManager(string executablePath) : PackageManagerBackend
{
    public override string Name => "pacman";

    public override string DisplayName => "pacman";

    public override string ExecutablePath => executablePath;

    public override bool SupportsSourceWrite => false;

    public override PackageCommand VersionCommand => PackageCommand.Of(executablePath, "--version");

    public override PackageCommand InstalledCommand => PackageCommand.Of(executablePath, "-Q");

    public override PackageCommand SearchCommand(string query) =>
        PackageCommand.Of(executablePath, "-Ss", query);

    // 部分 pacman 版本在没有可用更新时以退出码 1 结束，空输出同样是有效结果。
    public override PackageCommand UpdatesCommand =>
        new(executablePath, ["-Qu"], AllowedExitCodes: [0, 1]);

    public override PackageCommand ActionCommand(string action, IReadOnlyList<string> packages)
    {
        switch (action)
        {
            case PackageActions.Install:
            {
                var arguments = new List<string> { "-S", "--noconfirm" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.Remove:
            {
                var arguments = new List<string> { "-Rns", "--noconfirm" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.Upgrade:
            {
                var arguments = new List<string> { "-S", "--noconfirm" };
                arguments.AddRange(packages);
                return new PackageCommand(executablePath, arguments);
            }

            case PackageActions.UpgradeAll:
                return PackageCommand.Of(executablePath, "-Syu", "--noconfirm");

            case PackageActions.Refresh:
                return PackageCommand.Of(executablePath, "-Sy", "--noconfirm");

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "不支持的包管理动作。");
        }
    }

    public override IReadOnlyList<PackageSummary> ParseInstalled(string output)
    {
        var packages = new List<PackageSummary>();
        foreach (var line in Lines(output))
        {
            var tokens = Tokens(line);
            if (tokens.Length < 2)
            {
                continue;
            }

            packages.Add(new PackageSummary(tokens[0], tokens[1], string.Empty));
        }

        return packages;
    }

    public override IReadOnlyList<PackageSearchResult> ParseSearch(string output)
    {
        var results = new List<PackageSearchResult>();
        PackageSearchResult? pending = null;

        foreach (var line in Lines(output))
        {
            // pacman -Ss 每个结果占两行：第一行 repo/name version [installed]，第二行缩进简介。
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (pending is not null && pending.Summary.Length == 0)
                {
                    pending = pending with { Summary = line.Trim() };
                }

                continue;
            }

            if (pending is not null)
            {
                results.Add(pending);
                pending = null;
            }

            var tokens = Tokens(line);
            if (tokens.Length < 2)
            {
                continue;
            }

            var qualified = tokens[0];
            var slash = qualified.LastIndexOf('/');
            if (slash < 0 || slash == qualified.Length - 1)
            {
                continue;
            }

            var name = qualified[(slash + 1)..];
            if (!PackageValidators.IsValidPackageName(name))
            {
                continue;
            }

            var installed = line.Contains("[installed]", StringComparison.Ordinal);
            pending = new PackageSearchResult(name, tokens[1], string.Empty, installed);
        }

        if (pending is not null)
        {
            results.Add(pending);
        }

        return results;
    }

    public override IReadOnlyList<PackageUpdate> ParseUpdates(string output)
    {
        var updates = new List<PackageUpdate>();
        foreach (var line in Lines(output))
        {
            // pacman -Qu 输出：name old -> new
            var tokens = Tokens(line);
            if (tokens.Length < 4 || tokens[2] != "->")
            {
                continue;
            }

            updates.Add(new PackageUpdate(tokens[0], tokens[1], tokens[3]));
        }

        return updates;
    }
}
