using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Koi.Api.Packages;

/// <summary>
/// 软件源读写：apt（sources.list / sources.list.d）、dnf+yum（yum.repos.d）与 zypper。
/// </summary>
/// <remarks>
/// 源文件位于 <c>/etc</c>，改写一律使用同目录临时文件 + 原子替换，并保留原文件权限；
/// 符号链接文件直接跳过，避免通过链接改写非预期目标。pacman 的仓库定义在
/// <c>/etc/pacman.conf</c>，仅提供查看。所有变更操作要求 Koi 服务以 root 运行。
/// </remarks>
public sealed partial class PackageSourceService(
    PackageCommandRunner runner,
    PackageSettingsService settings,
    ILogger<PackageSourceService> logger)
{
    private const string AptMainList = "/etc/apt/sources.list";
    private const string AptPartsDir = "/etc/apt/sources.list.d";
    private const string YumReposDir = "/etc/yum.repos.d";
    private const string YumLegacyDir = "/etc/yum/repos.d";
    private const string PacmanConf = "/etc/pacman.conf";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex(@"^\s*enabled\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RpmEnabledLineRegex();

    private PackageOptions Settings => settings.Current;

    public async Task<PackageSourceListResponse> ListAsync(
        PackageManagerBackend backend,
        CancellationToken cancellationToken)
    {
        return backend.Name switch
        {
            "apt" => new PackageSourceListResponse(
                backend.Name, true, false, ReadAptEntries().Select(entry => entry.Source).ToArray()),
            "dnf" or "yum" => new PackageSourceListResponse(
                backend.Name, true, false, ReadRpmEntries().Select(entry => entry.Source).ToArray()),
            "zypper" => new PackageSourceListResponse(
                backend.Name, true, false,
                (await ReadZypperEntriesAsync(backend, cancellationToken)).Select(entry => entry.Source).ToArray()),
            "pacman" => new PackageSourceListResponse(
                backend.Name, false, true, ReadPacmanSources().ToArray()),
            _ => new PackageSourceListResponse(backend.Name, false, true, []),
        };
    }

    public async Task<PackageSource> AddAsync(
        PackageManagerBackend backend,
        PackageSourceAddRequest request,
        CancellationToken cancellationToken)
    {
        if (!backend.SupportsSourceWrite)
        {
            throw new PackageOperationException(
                StatusCodes.Status400BadRequest,
                "不支持修改软件源",
                $"{backend.DisplayName} 的软件源需手动编辑 /etc/pacman.conf，应用管理仅提供查看。");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            return backend.Name switch
            {
                "apt" => AddAptSource(request),
                "dnf" or "yum" => AddRpmSource(request),
                "zypper" => await AddZypperSourceAsync(backend, request, cancellationToken),
                _ => throw new PackageOperationException(
                    StatusCodes.Status400BadRequest,
                    "不支持修改软件源",
                    "当前包管理器不支持软件源管理。"),
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<PackageSource> SetEnabledAsync(
        PackageManagerBackend backend,
        string id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!backend.SupportsSourceWrite)
        {
            throw new PackageOperationException(
                StatusCodes.Status400BadRequest,
                "不支持修改软件源",
                $"{backend.DisplayName} 的软件源需手动编辑 /etc/pacman.conf，应用管理仅提供查看。");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            return backend.Name switch
            {
                "apt" => SetAptEnabled(id, enabled),
                "dnf" or "yum" => SetRpmEnabled(id, enabled),
                "zypper" => await SetZypperEnabledAsync(backend, id, enabled, cancellationToken),
                _ => throw new PackageOperationException(
                    StatusCodes.Status400BadRequest,
                    "不支持修改软件源",
                    "当前包管理器不支持软件源管理。"),
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RemoveAsync(
        PackageManagerBackend backend,
        string id,
        CancellationToken cancellationToken)
    {
        if (!backend.SupportsSourceWrite)
        {
            throw new PackageOperationException(
                StatusCodes.Status400BadRequest,
                "不支持修改软件源",
                $"{backend.DisplayName} 的软件源需手动编辑 /etc/pacman.conf，应用管理仅提供查看。");
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            switch (backend.Name)
            {
                case "apt":
                    RemoveAptSource(id);
                    break;
                case "dnf":
                case "yum":
                    RemoveRpmSource(id);
                    break;
                case "zypper":
                    await RemoveZypperSourceAsync(backend, id, cancellationToken);
                    break;
                default:
                    throw new PackageOperationException(
                        StatusCodes.Status400BadRequest,
                        "不支持修改软件源",
                        "当前包管理器不支持软件源管理。");
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---------------- apt ----------------

    private List<AptEntry> ReadAptEntries()
    {
        var entries = new List<AptEntry>();
        ReadAptListFile(AptMainList, entries);

        if (Directory.Exists(AptPartsDir))
        {
            foreach (var path in Directory.EnumerateFiles(AptPartsDir).Order(StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(path);
                if (fileName.EndsWith(".list", StringComparison.OrdinalIgnoreCase))
                {
                    ReadAptListFile(path, entries);
                }
                else if (fileName.EndsWith(".sources", StringComparison.OrdinalIgnoreCase))
                {
                    ReadAptDeb822File(path, entries);
                }
            }
        }

        return entries;
    }

    private void ReadAptListFile(string path, List<AptEntry> entries)
    {
        if (!TryReadLines(path, out var lines))
        {
            return;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var commented = trimmed.StartsWith('#');
            var candidate = commented ? trimmed.TrimStart('#').TrimStart() : trimmed;
            if (!candidate.StartsWith("deb ", StringComparison.Ordinal) &&
                !candidate.StartsWith("deb-src ", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            var uri = tokens[1];
            var suite = tokens.Length > 2 ? tokens[2] : string.Empty;
            var components = tokens.Length > 3 ? string.Join(' ', tokens[3..]) : string.Empty;
            var key = NormalizeWhitespace(candidate);

            entries.Add(new AptEntry(
                new PackageSource(
                    ComputeId("apt", path, key),
                    "apt",
                    BuildSourceName(uri, suite),
                    uri,
                    suite,
                    components,
                    !commented,
                    path,
                    true,
                    true,
                    IsManagedFile(path)),
                index,
                -1,
                -1,
                Deb822: false));
        }
    }

    private void ReadAptDeb822File(string path, List<AptEntry> entries)
    {
        if (!TryReadLines(path, out var lines))
        {
            return;
        }

        foreach (var (start, end) in EnumerateBlocks(lines))
        {
            var fields = ParseDeb822(lines, start, end);
            if (!fields.TryGetValue("uris", out var uris) || string.IsNullOrWhiteSpace(uris))
            {
                continue;
            }

            // 只展示启用的 deb 源；deb-src 单独成块时仍然列出。
            var types = fields.GetValueOrDefault("types", "deb");
            if (!types.Contains("deb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suites = fields.GetValueOrDefault("suites", string.Empty);
            var components = fields.GetValueOrDefault("components", string.Empty);
            var enabled = !fields.TryGetValue("enabled", out var enabledText) ||
                          !IsDisabledValue(enabledText);
            var key = NormalizeWhitespace($"{uris}|{suites}");
            var firstUri = uris.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? uris;

            entries.Add(new AptEntry(
                new PackageSource(
                    ComputeId("apt", path, key),
                    "apt",
                    BuildSourceName(firstUri, suites),
                    uris,
                    suites,
                    components,
                    enabled,
                    path,
                    true,
                    true,
                    IsManagedFile(path)),
                -1,
                start,
                end,
                Deb822: true));
        }
    }

    private PackageSource AddAptSource(PackageSourceAddRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (!PackageValidators.IsValidSourceName(name))
        {
            throw BadRequest("源标识只能包含字母、数字、下划线、点和短横线，且不能以短横线开头。");
        }

        var uri = request.Uri?.Trim() ?? string.Empty;
        if (!PackageValidators.IsValidRepositoryUri(uri))
        {
            throw BadRequest("仓库地址必须是 http/https/ftp/file 开头的绝对地址，且不能包含空白字符。");
        }

        var suite = request.Suite?.Trim() ?? string.Empty;
        if (!PackageValidators.IsValidAptTokens(suite))
        {
            throw BadRequest("发行版（suite）不能为空，只能包含字母、数字和 . / + _ ~ -。");
        }

        var components = request.Components?.Trim() ?? string.Empty;
        if (!PackageValidators.IsValidAptTokens(components))
        {
            throw BadRequest("组件（components）不能为空，只能包含字母、数字和 . / + _ ~ -。");
        }

        Directory.CreateDirectory(AptPartsDir);
        var file = Path.Combine(AptPartsDir, $"koi-{name.ToLowerInvariant()}.list");
        if (File.Exists(file))
        {
            throw Conflict("软件源已存在", $"文件 {file} 已存在，请换一个源标识或先删除旧源。");
        }

        var line = $"deb {uri} {suite} {components}";
        WriteNewFile(file, [line]);
        return new PackageSource(
            ComputeId("apt", file, NormalizeWhitespace(line)),
            "apt",
            BuildSourceName(uri, suite),
            uri,
            suite,
            components,
            true,
            file,
            true,
            true,
            true);
    }

    private PackageSource SetAptEnabled(string id, bool enabled)
    {
        var entry = ReadAptEntries().FirstOrDefault(item => item.Source.Id == id)
                    ?? throw SourceNotFound();

        if (!TryReadLines(entry.Source.File, out var lines))
        {
            throw SourceNotFound();
        }

        if (!entry.Deb822)
        {
            var uncommented = lines[entry.LineIndex].TrimStart().TrimStart('#').TrimStart();
            lines[entry.LineIndex] = enabled ? uncommented : $"# {uncommented}";
        }
        else
        {
            var fieldIndex = FindFieldLine(lines, entry.BlockStart, entry.BlockEnd, "Enabled");
            var field = $"Enabled: {(enabled ? "yes" : "no")}";
            if (fieldIndex >= 0)
            {
                lines[fieldIndex] = field;
            }
            else
            {
                lines.Insert(entry.BlockEnd, field);
            }
        }

        WriteAtomic(entry.Source.File, lines);
        return entry.Source with { Enabled = enabled };
    }

    private void RemoveAptSource(string id)
    {
        var entry = ReadAptEntries().FirstOrDefault(item => item.Source.Id == id)
                    ?? throw SourceNotFound();
        if (!TryReadLines(entry.Source.File, out var lines))
        {
            throw SourceNotFound();
        }

        if (!entry.Deb822)
        {
            lines.RemoveAt(entry.LineIndex);
        }
        else
        {
            lines.RemoveRange(entry.BlockStart, entry.BlockEnd - entry.BlockStart);
            if (entry.BlockEnd < lines.Count && string.IsNullOrWhiteSpace(lines[entry.BlockEnd]))
            {
                lines.RemoveAt(entry.BlockEnd);
            }
        }

        if (entry.Source.File != AptMainList && HasNoContent(lines))
        {
            TryDelete(entry.Source.File);
            return;
        }

        WriteAtomic(entry.Source.File, lines);
    }

    // ---------------- dnf / yum ----------------

    private List<RpmEntry> ReadRpmEntries()
    {
        var entries = new List<RpmEntry>();
        foreach (var directory in new[] { YumReposDir, YumLegacyDir })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.repo").Order(StringComparer.Ordinal))
            {
                ReadRpmFile(path, entries);
            }
        }

        return entries;
    }

    private void ReadRpmFile(string path, List<RpmEntry> entries)
    {
        if (!TryReadLines(path, out var lines))
        {
            return;
        }

        var sectionStart = -1;
        string? sectionId = null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush(int end)
        {
            if (sectionStart < 0 || string.IsNullOrEmpty(sectionId))
            {
                return;
            }

            var enabled = !fields.TryGetValue("enabled", out var enabledText) ||
                          !IsDisabledValue(enabledText);
            var uri = FirstToken(fields.GetValueOrDefault("baseurl"))
                      ?? FirstToken(fields.GetValueOrDefault("metalink"))
                      ?? FirstToken(fields.GetValueOrDefault("mirrorlist"))
                      ?? string.Empty;
            var name = fields.GetValueOrDefault("name")?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = sectionId;
            }

            entries.Add(new RpmEntry(
                new PackageSource(
                    ComputeId("rpm", path, sectionId),
                    "rpm",
                    name,
                    uri,
                    string.Empty,
                    string.Empty,
                    enabled,
                    path,
                    true,
                    true,
                    IsManagedFile(path)),
                sectionStart,
                end));

            sectionStart = -1;
            sectionId = null;
            fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                Flush(index);
                sectionId = trimmed[1..^1].Trim();
                sectionStart = index;
                fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (sectionStart < 0)
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            fields[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        Flush(lines.Count);
    }

    private PackageSource AddRpmSource(PackageSourceAddRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (!PackageValidators.IsValidSourceName(name))
        {
            throw BadRequest("仓库标识只能包含字母、数字、下划线、点和短横线，且不能以短横线开头。");
        }

        var uri = request.Uri?.Trim() ?? string.Empty;
        if (!PackageValidators.IsValidRepositoryUri(uri))
        {
            throw BadRequest("仓库地址必须是 http/https/ftp/file 开头的绝对地址，且不能包含空白字符。");
        }

        var gpgCheck = request.GpgCheck ?? true;
        var sectionId = name.ToLowerInvariant();
        Directory.CreateDirectory(YumReposDir);
        var file = Path.Combine(YumReposDir, $"koi-{sectionId}.repo");
        if (File.Exists(file))
        {
            throw Conflict("软件源已存在", $"文件 {file} 已存在，请换一个仓库标识或先删除旧源。");
        }

        WriteNewFile(file,
        [
            $"[{sectionId}]",
            $"name={name}",
            $"baseurl={uri}",
            "enabled=1",
            $"gpgcheck={(gpgCheck ? "1" : "0")}",
            "repo_gpgcheck=0",
        ]);

        return new PackageSource(
            ComputeId("rpm", file, sectionId),
            "rpm",
            name,
            uri,
            string.Empty,
            string.Empty,
            true,
            file,
            true,
            true,
            true);
    }

    private PackageSource SetRpmEnabled(string id, bool enabled)
    {
        var entry = ReadRpmEntries().FirstOrDefault(item => item.Source.Id == id)
                    ?? throw SourceNotFound();
        if (!TryReadLines(entry.Source.File, out var lines))
        {
            throw SourceNotFound();
        }

        var fieldIndex = -1;
        for (var index = entry.SectionStart + 1; index < entry.SectionEnd; index++)
        {
            if (RpmEnabledLineRegex().IsMatch(lines[index]))
            {
                fieldIndex = index;
                break;
            }
        }

        var value = $"enabled={(enabled ? "1" : "0")}";
        if (fieldIndex >= 0)
        {
            lines[fieldIndex] = value;
        }
        else
        {
            lines.Insert(entry.SectionStart + 1, value);
        }

        WriteAtomic(entry.Source.File, lines);
        return entry.Source with { Enabled = enabled };
    }

    private void RemoveRpmSource(string id)
    {
        var entry = ReadRpmEntries().FirstOrDefault(item => item.Source.Id == id)
                    ?? throw SourceNotFound();
        if (!TryReadLines(entry.Source.File, out var lines))
        {
            throw SourceNotFound();
        }

        lines.RemoveRange(entry.SectionStart, entry.SectionEnd - entry.SectionStart);
        if (HasNoSections(lines))
        {
            TryDelete(entry.Source.File);
            return;
        }

        WriteAtomic(entry.Source.File, lines);
    }

    // ---------------- zypper ----------------

    private async Task<List<ZypperEntry>> ReadZypperEntriesAsync(
        PackageManagerBackend backend,
        CancellationToken cancellationToken)
    {
        var command = new PackageCommand(backend.ExecutablePath, ["--non-interactive", "repos", "-u"]);
        var result = await runner.RunAsync(command, CommandTimeout(), MaxOutput(), cancellationToken);
        if (!result.IsSuccess(command))
        {
            throw PackageErrors.FromResult(result, "读取软件源");
        }

        var entries = new List<ZypperEntry>();
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith('#') ||
                trimmed.StartsWith('-') ||
                trimmed.StartsWith("Repository priorities", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split('|');
            if (parts.Length < 4)
            {
                continue;
            }

            var alias = parts[1].Trim();
            var name = parts[2].Trim();
            var enabledText = parts[3].Trim();
            var uri = parts.Length >= 7 ? parts[6].Trim() : string.Empty;
            if (!PackageValidators.IsValidSourceName(alias))
            {
                continue;
            }

            var enabled = enabledText.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                          enabledText == "1";
            var source = new PackageSource(
                ComputeId("zypper", "zypper", alias),
                "zypper",
                name.Length > 0 ? name : alias,
                uri,
                string.Empty,
                string.Empty,
                enabled,
                "zypper",
                true,
                true,
                alias.StartsWith("koi-", StringComparison.OrdinalIgnoreCase));
            entries.Add(new ZypperEntry(source, alias));
        }

        return entries;
    }

    private async Task<PackageSource> AddZypperSourceAsync(
        PackageManagerBackend backend,
        PackageSourceAddRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (!PackageValidators.IsValidSourceName(name))
        {
            throw BadRequest("仓库别名只能包含字母、数字、下划线、点和短横线，且不能以短横线开头。");
        }

        var uri = request.Uri?.Trim() ?? string.Empty;
        if (!PackageValidators.IsValidRepositoryUri(uri))
        {
            throw BadRequest("仓库地址必须是 http/https/ftp/file 开头的绝对地址，且不能包含空白字符。");
        }

        var command = new PackageCommand(
            backend.ExecutablePath,
            ["--non-interactive", "addrepo", uri, name]);
        var result = await runner.RunAsync(command, CommandTimeout(), MaxOutput(), cancellationToken);
        if (!result.IsSuccess(command))
        {
            throw PackageErrors.FromResult(result, "添加软件源");
        }

        return new PackageSource(
            ComputeId("zypper", "zypper", name),
            "zypper",
            name,
            uri,
            string.Empty,
            string.Empty,
            true,
            "zypper",
            true,
            true,
            name.StartsWith("koi-", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<PackageSource> SetZypperEnabledAsync(
        PackageManagerBackend backend,
        string id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var entry = (await ReadZypperEntriesAsync(backend, cancellationToken))
                        .FirstOrDefault(item => item.Source.Id == id)
                    ?? throw SourceNotFound();

        var command = new PackageCommand(
            backend.ExecutablePath,
            ["--non-interactive", "modifyrepo", enabled ? "-e" : "-d", entry.Alias]);
        var result = await runner.RunAsync(command, CommandTimeout(), MaxOutput(), cancellationToken);
        if (!result.IsSuccess(command))
        {
            throw PackageErrors.FromResult(result, enabled ? "启用软件源" : "禁用软件源");
        }

        return entry.Source with { Enabled = enabled };
    }

    private async Task RemoveZypperSourceAsync(
        PackageManagerBackend backend,
        string id,
        CancellationToken cancellationToken)
    {
        var entry = (await ReadZypperEntriesAsync(backend, cancellationToken))
                        .FirstOrDefault(item => item.Source.Id == id)
                    ?? throw SourceNotFound();

        var command = new PackageCommand(
            backend.ExecutablePath,
            ["--non-interactive", "removerepo", entry.Alias]);
        var result = await runner.RunAsync(command, CommandTimeout(), MaxOutput(), cancellationToken);
        if (!result.IsSuccess(command))
        {
            throw PackageErrors.FromResult(result, "删除软件源");
        }
    }

    // ---------------- pacman ----------------

    private List<PackageSource> ReadPacmanSources()
    {
        var sources = new List<PackageSource>();
        if (!TryReadLines(PacmanConf, out var lines))
        {
            return sources;
        }

        string? section = null;
        var uri = string.Empty;

        void Flush()
        {
            if (section is null || section.Equals("options", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            sources.Add(new PackageSource(
                ComputeId("pacman", PacmanConf, section),
                "pacman",
                section,
                uri,
                string.Empty,
                string.Empty,
                true,
                PacmanConf,
                false,
                false,
                false));
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                Flush();
                section = trimmed[1..^1].Trim();
                uri = string.Empty;
                continue;
            }

            if (section is null || uri.Length > 0)
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            if (key.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                uri = trimmed[(separator + 1)..].Trim();
            }
        }

        Flush();
        return sources;
    }

    // ---------------- 公共辅助 ----------------

    private TimeSpan CommandTimeout()
    {
        var seconds = Settings.CommandTimeoutSeconds > 0 ? Settings.CommandTimeoutSeconds : 120;
        return TimeSpan.FromSeconds(seconds);
    }

    private long MaxOutput() => Settings.MaxOutputBytes > 0 ? Settings.MaxOutputBytes : 4 * 1024 * 1024;

    private bool TryReadLines(string path, out List<string> lines)
    {
        lines = [];
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                logger.LogWarning("跳过符号链接形式的软件源文件：{Path}", path);
                return false;
            }

            lines = [.. File.ReadAllLines(path)];
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "读取软件源文件失败：{Path}", path);
            return false;
        }
    }

    private static IEnumerable<(int Start, int End)> EnumerateBlocks(List<string> lines)
    {
        var index = 0;
        while (index < lines.Count)
        {
            while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            var start = index;
            while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            if (index > start)
            {
                yield return (start, index);
            }
        }
    }

    private static Dictionary<string, string> ParseDeb822(List<string> lines, int start, int end)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;

        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if ((line[0] == ' ' || line[0] == '\t') && currentKey is not null)
            {
                fields[currentKey] = $"{fields[currentKey]} {line.Trim()}".Trim();
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                currentKey = null;
                continue;
            }

            currentKey = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            fields[currentKey] = fields.TryGetValue(currentKey, out var existing)
                ? $"{existing} {value}".Trim()
                : value;
        }

        return fields;
    }

    private static int FindFieldLine(List<string> lines, int start, int end, string field)
    {
        for (var index = start; index < end; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf(':');
            if (separator > 0 && trimmed[..separator].Trim().Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsDisabledValue(string? value) =>
        value is not null &&
        (value.Trim().Equals("no", StringComparison.OrdinalIgnoreCase) ||
         value.Trim().Equals("0", StringComparison.OrdinalIgnoreCase) ||
         value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));

    private static bool HasNoContent(List<string> lines) =>
        lines.All(line => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'));

    private static bool HasNoSections(List<string> lines) =>
        !lines.Any(line =>
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith('[') && trimmed.EndsWith(']');
        });

    private static string? FirstToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private static string BuildSourceName(string uri, string suite)
    {
        var host = uri;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            host = parsed.IsFile ? parsed.LocalPath : parsed.Host;
        }

        return string.IsNullOrWhiteSpace(suite) ? host : $"{host} {suite}";
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ComputeId(string kind, string file, string key)
    {
        var bytes = Encoding.UTF8.GetBytes($"{kind}\n{file}\n{key}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool IsManagedFile(string path) =>
        Path.GetFileName(path).StartsWith("koi-", StringComparison.OrdinalIgnoreCase);

    private static void WriteNewFile(string path, IEnumerable<string> lines)
    {
        var content = string.Join('\n', lines) + "\n";
        var temp = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.koi-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    temp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            File.Move(temp, path, overwrite: false);
        }
        catch (IOException ex)
        {
            TryDelete(temp);
            throw new PackageOperationException(
                StatusCodes.Status409Conflict,
                "软件源已存在",
                $"无法创建 {path}：{ex.Message}");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void WriteAtomic(string path, List<string> lines)
    {
        var mode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(path) : (UnixFileMode?)null;
        var content = string.Join('\n', lines);
        if (lines.Count > 0 && !content.EndsWith('\n'))
        {
            content += "\n";
        }

        var temp = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.koi-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (mode is { } unixMode && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(temp, unixMode);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理临时文件失败不影响主流程。
        }
    }

    private static PackageOperationException BadRequest(string detail) =>
        new(StatusCodes.Status400BadRequest, "软件源参数不合法", detail);

    private static PackageOperationException Conflict(string title, string detail) =>
        new(StatusCodes.Status409Conflict, title, detail);

    private static PackageOperationException SourceNotFound() =>
        new(
            StatusCodes.Status404NotFound,
            "软件源不存在",
            "该软件源已被删除、被其他程序修改，或不在应用管理可识别的范围内。");

    private sealed record AptEntry(
        PackageSource Source,
        int LineIndex,
        int BlockStart,
        int BlockEnd,
        bool Deb822);

    private sealed record RpmEntry(
        PackageSource Source,
        int SectionStart,
        int SectionEnd);

    private sealed record ZypperEntry(PackageSource Source, string Alias);
}
