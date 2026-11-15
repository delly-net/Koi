namespace Koi.Api.Packages;

/// <summary>后台任务支持的动作（固定白名单）。</summary>
public static class PackageActions
{
    public const string Install = "install";
    public const string Remove = "remove";
    public const string Upgrade = "upgrade";
    public const string UpgradeAll = "upgrade-all";
    public const string Refresh = "refresh";

    public static readonly string[] All = [Install, Remove, Upgrade, UpgradeAll, Refresh];

    public static bool IsValid(string? action) =>
        action is not null && All.Contains(action, StringComparer.Ordinal);

    /// <summary>该动作是否必须显式指定目标包。</summary>
    public static bool RequiresPackages(string action) =>
        action is Install or Remove or Upgrade;
}

/// <summary>一次包管理器命令：可执行文件 + 固定参数数组。</summary>
/// <remarks>
/// 参数始终经 <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> 传递，不拼接 shell，
/// 因此用户输入只会成为普通参数。部分查询命令（如 <c>dnf check-update</c>）以非 0 退出码表示
/// “有更新”，通过 <see cref="AllowedExitCodes"/> 显式放行。
/// </remarks>
public sealed record PackageCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<int>? AllowedExitCodes = null)
{
    public bool IsSuccess(int exitCode) =>
        (AllowedExitCodes ?? [0]).Contains(exitCode);

    public static PackageCommand Of(string fileName, params string[] arguments) =>
        new(fileName, arguments);
}

/// <summary>包管理器命令的执行结果。</summary>
public sealed record PackageCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTooLarge)
{
    /// <summary>命令正常结束且退出码在允许范围内。</summary>
    public bool IsSuccess(PackageCommand command) =>
        !TimedOut && !OutputTooLarge && command.IsSuccess(ExitCode);
}

/// <summary>已安装/可安装的软件包摘要。</summary>
public sealed record PackageSummary(string Name, string Version, string Summary);

/// <summary>搜索结果。</summary>
public sealed record PackageSearchResult(
    string Name,
    string Version,
    string Summary,
    bool Installed);

/// <summary>可升级的软件包。</summary>
public sealed record PackageUpdate(
    string Name,
    string CurrentVersion,
    string NewVersion);

/// <summary>软件源条目。</summary>
/// <param name="Id">稳定标识（文件/条目内容的 SHA-256）。</param>
/// <param name="Kind">apt / rpm / zypper / pacman。</param>
/// <param name="Name">显示名称。</param>
/// <param name="Uri">仓库地址；无法解析时为空。</param>
/// <param name="Suite">apt 发行版（suites）。</param>
/// <param name="Components">apt 组件。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="File">来源文件；zypper 为 <c>zypper</c>。</param>
/// <param name="Togglable">是否支持启用/禁用。</param>
/// <param name="Removable">是否支持删除。</param>
/// <param name="Managed">是否由 Koi 创建（文件名/别名以 koi- 开头）。</param>
public sealed record PackageSource(
    string Id,
    string Kind,
    string Name,
    string Uri,
    string Suite,
    string Components,
    bool Enabled,
    string File,
    bool Togglable,
    bool Removable,
    bool Managed);

/// <summary>新增软件源请求。</summary>
public sealed record PackageSourceAddRequest(
    string? Name,
    string? Uri,
    string? Suite,
    string? Components,
    bool? GpgCheck);

public sealed record PackageStatusResponse(
    bool Available,
    string Manager,
    string DisplayName,
    string Version,
    bool Root,
    bool CanModify,
    bool SourceManagement);

public sealed record PackageListResponse(
    string Manager,
    int Total,
    bool Truncated,
    PackageSummary[] Packages);

/// <summary>已安装软件缓存中的一条记录。</summary>
/// <param name="Id">缓存记录 Id（简介编辑按此定位）。</param>
/// <param name="Name">软件包名称。</param>
/// <param name="Version">最近一次刷新时的版本。</param>
/// <param name="Intro">简介，首次缓存时预填，之后只由用户修改。</param>
/// <param name="Status">安装状态：<c>installed</c> / <c>removed</c>。</param>
/// <param name="UpdatedAt">最近一次变更（刷新或简介编辑）时间。</param>
public sealed record PackageCacheEntry(
    long Id,
    string Name,
    string Version,
    string Intro,
    string Status,
    DateTimeOffset UpdatedAt);

/// <summary>缓存分页查询结果。</summary>
public sealed record PackageCacheListResponse(
    string Manager,
    int Total,
    int Page,
    int PageSize,
    string Status,
    DateTimeOffset? LastRefreshedAt,
    PackageCacheEntry[] Packages);

/// <summary>缓存刷新结果。</summary>
public sealed record PackageCacheRefreshResponse(
    string Manager,
    int Total,
    int Added,
    int Updated,
    int Removed,
    DateTimeOffset RefreshedAt);

/// <summary>修改缓存记录简介的请求。</summary>
public sealed record PackageCacheIntroRequest(string? Intro);

/// <summary>缓存状态筛选值（与前端约定）。</summary>
public static class PackageCacheStatusFilters
{
    public const string All = "all";
    public const string Installed = "installed";
    public const string Removed = "removed";

    public static bool IsValid(string? status) =>
        status is All or Installed or Removed;
}

public sealed record PackageSearchResponse(
    string Manager,
    PackageSearchResult[] Results,
    bool Truncated);

public sealed record PackageUpdatesResponse(
    string Manager,
    PackageUpdate[] Packages);

public sealed record PackageJobStartRequest(string? Action, string[]? Packages);

public sealed record PackageJobStartResponse(
    string JobId,
    string Action,
    string[] Packages);

public sealed record PackageJobResponse(
    string Id,
    string Action,
    string[] Packages,
    string Status,
    int? ExitCode,
    string Log,
    int NextOffset,
    bool Truncated,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public sealed record PackageJobCancelResponse(bool Ok);

public sealed record PackageSourceListResponse(
    string Manager,
    bool Supported,
    bool ReadOnly,
    PackageSource[] Sources);

public sealed record PackageSourceActionResponse(bool Ok);

/// <summary>后台任务状态字符串（与前端约定）。</summary>
public static class PackageJobStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>带 HTTP 状态码的包管理业务错误，由端点统一转换为 ProblemDetails。</summary>
public sealed class PackageOperationException(int statusCode, string title, string? detail)
    : Exception(detail ?? title)
{
    public int StatusCode { get; } = statusCode;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;
}

/// <summary>把包管理器命令失败映射为业务错误（400/403/404/409/413/502/503/504）。</summary>
public static class PackageErrors
{
    public static (int StatusCode, string Title, string Detail) Map(
        PackageCommandResult result,
        string title)
    {
        if (result.TimedOut)
        {
            return (
                StatusCodes.Status504GatewayTimeout,
                $"{title}超时",
                "包管理器命令执行时间超过上限，已终止。可稍后重试或减少操作范围。");
        }

        if (result.OutputTooLarge)
        {
            return (
                StatusCodes.Status413PayloadTooLarge,
                $"{title}输出过大",
                "包管理器命令输出超过上限，已终止。请减少目标数量或缩小查询范围。");
        }

        var stderr = result.StandardError.Trim();
        var lower = stderr.ToLowerInvariant();

        if (result.ExitCode == -1)
        {
            return (
                StatusCodes.Status503ServiceUnavailable,
                "包管理器不可用",
                string.IsNullOrEmpty(stderr) ? "无法执行包管理器命令。" : stderr);
        }

        if (lower.Contains("permission denied", StringComparison.Ordinal) ||
            lower.Contains("are you root", StringComparison.Ordinal) ||
            lower.Contains("you need to be root", StringComparison.Ordinal) ||
            lower.Contains("not root", StringComparison.Ordinal) ||
            lower.Contains("superuser", StringComparison.Ordinal) ||
            lower.Contains("operation not permitted", StringComparison.Ordinal))
        {
            return (StatusCodes.Status403Forbidden, $"{title}权限不足", stderr);
        }

        if (lower.Contains("unable to locate package", StringComparison.Ordinal) ||
            lower.Contains("no package named", StringComparison.Ordinal) ||
            lower.Contains("no match for argument", StringComparison.Ordinal) ||
            lower.Contains("target not found", StringComparison.Ordinal) ||
            lower.Contains("not found in sync db", StringComparison.Ordinal) ||
            lower.Contains("package was not found", StringComparison.Ordinal) ||
            lower.Contains("no such package", StringComparison.Ordinal))
        {
            return (StatusCodes.Status404NotFound, $"{title}失败", stderr);
        }

        if (lower.Contains("could not get lock", StringComparison.Ordinal) ||
            lower.Contains("waiting for cache lock", StringComparison.Ordinal) ||
            lower.Contains("is locked by another process", StringComparison.Ordinal) ||
            lower.Contains("another app is currently holding", StringComparison.Ordinal) ||
            lower.Contains("unable to lock database", StringComparison.Ordinal) ||
            lower.Contains("held packages", StringComparison.Ordinal) ||
            lower.Contains("nothing provides", StringComparison.Ordinal) ||
            lower.Contains("conflicting", StringComparison.Ordinal) ||
            lower.Contains("breaks dependency", StringComparison.Ordinal) ||
            lower.Contains("problem: ", StringComparison.Ordinal) ||
            lower.Contains("dpkg was interrupted", StringComparison.Ordinal))
        {
            return (StatusCodes.Status409Conflict, $"{title}冲突", stderr);
        }

        if (lower.Contains("could not resolve", StringComparison.Ordinal) ||
            lower.Contains("failed to fetch", StringComparison.Ordinal) ||
            lower.Contains("temporary failure", StringComparison.Ordinal) ||
            lower.Contains("network is unreachable", StringComparison.Ordinal) ||
            lower.Contains("could not retrieve", StringComparison.Ordinal) ||
            lower.Contains("failed to download", StringComparison.Ordinal))
        {
            return (StatusCodes.Status502BadGateway, $"{title}失败", stderr);
        }

        return (
            StatusCodes.Status500InternalServerError,
            $"{title}失败",
            string.IsNullOrEmpty(stderr) ? "包管理器命令执行失败。" : stderr);
    }

    public static PackageOperationException FromResult(PackageCommandResult result, string title)
    {
        var (statusCode, errorTitle, detail) = Map(result, title);
        return new PackageOperationException(statusCode, errorTitle, detail);
    }
}
