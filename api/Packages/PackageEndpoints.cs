using System.Text.RegularExpressions;
using Koi.Api.Auth;

namespace Koi.Api.Packages;

/// <summary>
/// 应用管理端点：包管理器状态、已安装/搜索/更新查询、后台任务与软件源管理。
/// </summary>
/// <remarks>
/// 所有操作都通过外部包管理器命令行完成，动作与子命令由后端固定白名单；
/// 用户输入仅作为经过校验的包名、源标识或仓库地址，不会执行任意命令或 shell。
/// 安装/卸载/升级等长耗时操作以后台任务执行，前端轮询增量日志。
/// </remarks>
public static class PackageEndpoints
{
    private static readonly Regex JobIdPattern = new(
        "^[0-9a-f]{16}$",
        RegexOptions.Compiled);

    private const int MaxSearchResults = 200;

    public static IEndpointRouteBuilder MapPackageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/packages")
            .WithTags("Packages")
            .AddEndpointFilter<RequireSessionFilter>();

        group.MapGet("/status", GetStatusAsync);
        group.MapGet("/installed", ListInstalledAsync);
        group.MapGet("/search", SearchAsync);
        group.MapGet("/updates", ListUpdatesAsync);

        group.MapPost("/jobs", StartJobAsync);
        group.MapGet("/jobs/{id}", GetJobAsync);
        group.MapPost("/jobs/{id}/cancel", CancelJobAsync);

        // 设置端点仅 root 可用，且不受 Enabled / 包管理器可用性影响，保证配置可被修复。
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettingsAsync);

        group.MapGet("/sources", ListSourcesAsync);
        group.MapPost("/sources", AddSourceAsync);
        group.MapPost("/sources/{id}/enable", (HttpContext context, string id,
                PackageManagerResolver resolver, PackageSourceService sourceService,
                PackageSettingsService settingsService, CancellationToken cancellationToken) =>
            SetSourceEnabledAsync(context, id, true, resolver, sourceService, settingsService, cancellationToken));
        group.MapPost("/sources/{id}/disable", (HttpContext context, string id,
                PackageManagerResolver resolver, PackageSourceService sourceService,
                PackageSettingsService settingsService, CancellationToken cancellationToken) =>
            SetSourceEnabledAsync(context, id, false, resolver, sourceService, settingsService, cancellationToken));
        group.MapDelete("/sources/{id}", RemoveSourceAsync);

        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        PackageCommandRunner runner,
        PackageManagerResolver resolver,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings, backend, _, isRoot) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var command = backend!.VersionCommand;
        var result = await runner.RunAsync(
            command,
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.IsSuccess(command))
        {
            return ToResult(result, "读取包管理器版本");
        }

        var version = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;

        return Results.Ok(new PackageStatusResponse(
            Available: true,
            Manager: backend.Name,
            DisplayName: backend.DisplayName,
            Version: version,
            Root: isRoot,
            CanModify: PackageAccessGuard.IsServiceRoot(),
            SourceManagement: backend.SupportsSourceWrite));
    }

    private static async Task<IResult> ListInstalledAsync(
        HttpContext context,
        string? query,
        int? limit,
        PackageCommandRunner runner,
        PackageManagerResolver resolver,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings, backend, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var command = backend!.InstalledCommand;
        var result = await runner.RunAsync(
            command,
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.IsSuccess(command))
        {
            return ToResult(result, "加载已安装列表");
        }

        var keyword = query?.Trim() ?? string.Empty;
        var packages = backend.ParseInstalled(result.StandardOutput)
            .Where(item => keyword.Length == 0 ||
                           item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var maxEntries = settings.MaxInstalledEntries > 0 ? settings.MaxInstalledEntries : 5000;
        var take = Math.Clamp(limit ?? maxEntries, 1, maxEntries);

        return Results.Ok(new PackageListResponse(
            backend.Name,
            packages.Length,
            packages.Length > take,
            packages.Take(take).ToArray()));
    }

    private static async Task<IResult> SearchAsync(
        HttpContext context,
        string? q,
        PackageCommandRunner runner,
        PackageManagerResolver resolver,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings, backend, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var keyword = q?.Trim() ?? string.Empty;
        if (keyword.Length is 0 or > 100)
        {
            return BadRequest("搜索关键词不合法", "搜索关键词不能为空，且不能超过 100 个字符。");
        }

        var command = backend!.SearchCommand(keyword);
        var result = await runner.RunAsync(
            command,
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.IsSuccess(command))
        {
            return ToResult(result, "搜索软件包");
        }

        var results = backend.ParseSearch(result.StandardOutput)
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Results.Ok(new PackageSearchResponse(
            backend.Name,
            results.Take(MaxSearchResults).ToArray(),
            results.Length > MaxSearchResults));
    }

    private static async Task<IResult> ListUpdatesAsync(
        HttpContext context,
        PackageCommandRunner runner,
        PackageManagerResolver resolver,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings, backend, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var command = backend!.UpdatesCommand;
        var result = await runner.RunAsync(
            command,
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.IsSuccess(command))
        {
            return ToResult(result, "读取可用更新");
        }

        var updates = backend.ParseUpdates(result.StandardOutput)
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Results.Ok(new PackageUpdatesResponse(backend.Name, updates));
    }

    private static IResult GetSettings(
        HttpContext context,
        PackageSettingsService settingsService)
    {
        var (error, _) = AuthorizeSettings(context);
        return error ?? Results.Ok(settingsService.Current);
    }

    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        PackageOptions? request,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, userName) = AuthorizeSettings(context);
        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return BadRequest("请求内容为空", "请提供完整的应用管理配置。");
        }

        var settings = PackageSettingsValidator.Normalize(request);
        if (PackageSettingsValidator.Validate(settings) is { } detail)
        {
            return BadRequest("配置不合法", detail);
        }

        return Results.Ok(await settingsService.SaveAsync(settings, userName, cancellationToken));
    }

    private static async Task<IResult> StartJobAsync(
        HttpContext context,
        PackageJobStartRequest? request,
        PackageManagerResolver resolver,
        PackageJobManager jobs,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings, backend, userName, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (RequireWriteAccess() is { } writeError)
        {
            return writeError;
        }

        var action = request?.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!PackageActions.IsValid(action))
        {
            return BadRequest(
                "不支持的操作",
                $"支持的动作：{string.Join("、", PackageActions.All)}。");
        }

        var maxPackages = settings.MaxPackagesPerJob > 0 ? settings.MaxPackagesPerJob : 50;
        var packages = NormalizePackages(request?.Packages, maxPackages, out var packageError);
        if (packageError is not null)
        {
            return packageError;
        }

        if (PackageActions.RequiresPackages(action) && packages.Length == 0)
        {
            return BadRequest("请选择目标软件包", "安装、卸载与单包升级必须指定至少一个软件包。");
        }

        if (!PackageActions.RequiresPackages(action))
        {
            packages = [];
        }

        PackageCommand command;
        try
        {
            command = backend!.ActionCommand(action, packages);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest("不支持的操作", $"当前包管理器不支持动作 {action}。");
        }

        var job = jobs.TryStart(command, action, packages, userName);
        if (job is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "已有任务正在运行",
                detail: "包管理器同一时间只能执行一个变更任务，请等待当前任务结束后重试。");
        }

        return Results.Accepted(
            value: new PackageJobStartResponse(job.Id, job.Action, job.Packages));
    }

    private static async Task<IResult> GetJobAsync(
        HttpContext context,
        string id,
        int? offset,
        PackageManagerResolver resolver,
        PackageJobManager jobs,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _, _, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!JobIdPattern.IsMatch(id))
        {
            return NotFound("任务不存在", "任务标识格式不合法。");
        }

        var job = jobs.Find(id);
        if (job is null)
        {
            return NotFound("任务不存在或已过期", "该任务已结束并从内存中清理，请重新发起操作。");
        }

        var snapshot = job.Snapshot(offset ?? 0);
        return Results.Ok(new PackageJobResponse(
            job.Id,
            job.Action,
            job.Packages,
            snapshot.Status,
            snapshot.ExitCode,
            snapshot.Log,
            snapshot.NextOffset,
            snapshot.Truncated,
            job.StartedAt,
            snapshot.EndedAt));
    }

    private static async Task<IResult> CancelJobAsync(
        HttpContext context,
        string id,
        PackageManagerResolver resolver,
        PackageJobManager jobs,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _, _, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!JobIdPattern.IsMatch(id))
        {
            return NotFound("任务不存在", "任务标识格式不合法。");
        }

        if (jobs.Find(id) is null)
        {
            return NotFound("任务不存在或已过期", "该任务已结束并从内存中清理，请重新发起操作。");
        }

        if (!jobs.TryCancel(id))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "任务已经结束",
                detail: "只有正在运行的任务可以取消。");
        }

        return Results.Ok(new PackageJobCancelResponse(true));
    }

    private static async Task<IResult> ListSourcesAsync(
        HttpContext context,
        PackageManagerResolver resolver,
        PackageSourceService sourceService,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _, backend, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        try
        {
            return Results.Ok(await sourceService.ListAsync(backend!, cancellationToken));
        }
        catch (PackageOperationException ex)
        {
            return FromException(ex);
        }
    }

    private static async Task<IResult> AddSourceAsync(
        HttpContext context,
        PackageSourceAddRequest? request,
        PackageManagerResolver resolver,
        PackageSourceService sourceService,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _, backend, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (RequireWriteAccess() is { } writeError)
        {
            return writeError;
        }

        if (request is null)
        {
            return BadRequest("请求内容为空", "请提供软件源标识与仓库地址。");
        }

        try
        {
            var source = await sourceService.AddAsync(backend!, request, cancellationToken);
            return Results.Created($"/api/packages/sources/{source.Id}", source);
        }
        catch (PackageOperationException ex)
        {
            return FromException(ex);
        }
    }

    private static async Task<IResult> SetSourceEnabledAsync(
        HttpContext context,
        string id,
        bool enabled,
        PackageManagerResolver resolver,
        PackageSourceService sourceService,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _, backend, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (RequireWriteAccess() is { } writeError)
        {
            return writeError;
        }

        if (!IsValidSourceId(id))
        {
            return NotFound("软件源不存在", "软件源标识格式不合法。");
        }

        try
        {
            return Results.Ok(await sourceService.SetEnabledAsync(
                backend!,
                id,
                enabled,
                cancellationToken));
        }
        catch (PackageOperationException ex)
        {
            return FromException(ex);
        }
    }

    private static async Task<IResult> RemoveSourceAsync(
        HttpContext context,
        string id,
        PackageManagerResolver resolver,
        PackageSourceService sourceService,
        PackageSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _, backend, _, _) =
            await GuardAsync(context, settingsService, resolver, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (RequireWriteAccess() is { } writeError)
        {
            return writeError;
        }

        if (!IsValidSourceId(id))
        {
            return NotFound("软件源不存在", "软件源标识格式不合法。");
        }

        try
        {
            await sourceService.RemoveAsync(backend!, id, cancellationToken);
            return Results.Ok(new PackageSourceActionResponse(true));
        }
        catch (PackageOperationException ex)
        {
            return FromException(ex);
        }
    }

    /// <summary>
    /// 设置端点鉴权：仅 Linux 且登录账号为 uid 0。刻意不检查
    /// <see cref="PackageOptions.Enabled"/> 与包管理器可用性，避免功能被误关后无法通过界面恢复。
    /// </summary>
    private static (IResult? Error, string UserName) AuthorizeSettings(HttpContext context)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;

        if (!OperatingSystem.IsLinux())
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "应用管理不可用",
                detail: "应用管理依赖 Linux 包管理器，当前平台不支持。"), userName);
        }

        if (string.IsNullOrEmpty(userName))
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再访问应用管理设置。"), userName);
        }

        if (!PackageAccessGuard.IsRootAccount(userName))
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "无权修改应用管理设置",
                detail: "只有 root 账号可以修改应用管理配置。"), userName);
        }

        return (null, userName);
    }

    /// <summary>公共前置校验：返回错误响应、配置、后端、登录名与是否 root。</summary>
    private static async Task<(
        IResult? Error,
        PackageOptions Settings,
        PackageManagerBackend? Backend,
        string UserName,
        bool IsRoot)> GuardAsync(
        HttpContext context,
        PackageSettingsService settingsService,
        PackageManagerResolver resolver,
        CancellationToken cancellationToken)
    {
        var settings = settingsService.Current;
        var (error, userName, isRoot) =
            await PackageAccessGuard.AuthorizeAsync(context, settings, resolver, cancellationToken);
        return (error, settings, resolver.Backend, userName, isRoot);
    }

    /// <summary>写操作要求 Koi 服务进程以 root 运行，否则包管理器会因权限不足失败。</summary>
    private static IResult? RequireWriteAccess() =>
        PackageAccessGuard.IsServiceRoot()
            ? null
            : Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "无法执行包管理操作",
                detail: "Koi 服务未以 root 运行，无法安装、卸载或修改软件源。请以 root 运行 koi.service 后重试。");

    private static string[] NormalizePackages(
        string[]? input,
        int maxPackages,
        out IResult? error)
    {
        error = null;
        if (input is not { Length: > 0 })
        {
            return [];
        }

        var packages = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in input)
        {
            var name = raw?.Trim() ?? string.Empty;
            if (!PackageValidators.IsValidPackageName(name))
            {
                error = BadRequest(
                    "软件包名称不合法",
                    $"“{name}”不是合法的软件包名称：须以字母或数字开头，只能包含字母、数字和 + . _ : @ / = ~ -，最长 200 个字符。");
                return [];
            }

            if (seen.Add(name))
            {
                packages.Add(name);
            }
        }

        if (packages.Count > maxPackages)
        {
            error = BadRequest("目标软件包过多", $"单个任务最多允许 {maxPackages} 个软件包。");
            return [];
        }

        return [.. packages];
    }

    private static bool IsValidSourceId(string id) =>
        id.Length == 64 && id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IResult FromException(PackageOperationException ex) =>
        Results.Problem(statusCode: ex.StatusCode, title: ex.Title, detail: ex.Detail);

    private static IResult ToResult(PackageCommandResult result, string title)
    {
        var (statusCode, errorTitle, detail) = PackageErrors.Map(result, title);
        return Results.Problem(statusCode: statusCode, title: errorTitle, detail: detail);
    }

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

    private static IResult NotFound(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title, detail: detail);

    private static TimeSpan CommandTimeout(PackageOptions settings) =>
        TimeSpan.FromSeconds(settings.CommandTimeoutSeconds > 0 ? settings.CommandTimeoutSeconds : 120);

    private static long MaxOutput(PackageOptions settings) =>
        settings.MaxOutputBytes > 0 ? settings.MaxOutputBytes : 4L * 1024 * 1024;
}
