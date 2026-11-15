using Koi.Api.Auth;
using Koi.Api.Terminal;

namespace Koi.Api.Processes;

/// <summary>
/// 进程管理端点：进程列表、详情与结束进程，以及 root 专用的进程管理设置。
/// </summary>
/// <remarks>
/// 数据全部来自 <c>/proc</c>，不执行外部命令；结束进程是唯一的特权动作，通过 <c>kill(2)</c>
/// 发送 SIGTERM / SIGKILL，并由 <see cref="ProcessAccessGuard"/> 按允许组授权。
/// </remarks>
public static class ProcessEndpoints
{
    public static IEndpointRouteBuilder MapProcessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/processes")
            .WithTags("Processes")
            .AddEndpointFilter<RequireSessionFilter>();

        // 设置端点使用字面量路径，必须早于 /{pid} 注册；设置端点刻意跳过 Enabled 与平台检查，
        // 便于管理员修复被关闭或配错的进程管理。
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettingsAsync);

        group.MapGet("", ListProcessesAsync);
        group.MapGet("/{pid:int}", GetProcessAsync);
        group.MapPost("/{pid:int}/terminate", TerminateProcessAsync);

        return app;
    }

    private static async Task<IResult> ListProcessesAsync(
        HttpContext context,
        ProcessService processService,
        ProcessSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        try
        {
            return Results.Ok(await processService.GetSnapshotAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "进程数据不可用",
                detail: "无法读取 /proc 进程信息，请确认系统为 Linux 且 /proc 已挂载。");
        }
    }

    private static async Task<IResult> GetProcessAsync(
        HttpContext context,
        int pid,
        ProcessService processService,
        ProcessSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (pid <= 0)
        {
            return BadRequest("进程号不合法", "进程号必须是正整数。");
        }

        try
        {
            var detail = await processService.GetDetailAsync(pid, cancellationToken);
            return detail is null
                ? NotFound("进程不存在", $"未找到进程 {pid}，它可能已经退出。")
                : Results.Ok(detail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "进程数据不可用",
                detail: "无法读取 /proc 进程信息，请确认系统为 Linux 且 /proc 已挂载。");
        }
    }

    /// <summary>
    /// 结束进程：默认发送 SIGTERM；<c>force=true</c> 发送 SIGKILL。
    /// 查询参数用 <see cref="HttpContext"/> 手动读取，缺省即优雅结束，不把参数当作必填。
    /// </summary>
    private static async Task<IResult> TerminateProcessAsync(
        HttpContext context,
        int pid,
        ProcessService processService,
        ProcessSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, _) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (pid <= 0)
        {
            return BadRequest("进程号不合法", "进程号必须是正整数。");
        }

        var force = bool.TryParse(context.Request.Query["force"].ToString(), out var parsedForce) && parsedForce;
        var signal = force ? "SIGKILL" : "SIGTERM";

        ProcessTerminateOutcome outcome;
        try
        {
            outcome = await processService.TerminateAsync(pid, force, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "进程管理不可用",
                detail: "无法访问进程信息，请确认系统为 Linux 且 /proc 已挂载。");
        }

        return outcome.Status switch
        {
            ProcessTerminateStatus.Exited => Results.Ok(
                new ProcessTerminateResponse(true, pid, signal, Exited: true, WaitedMilliseconds: outcome.WaitedMilliseconds)),
            ProcessTerminateStatus.NotFound => NotFound("进程不存在", $"未找到进程 {pid}，它可能已经退出。"),
            ProcessTerminateStatus.PermissionDenied => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "系统权限不足",
                detail: "服务账号无权向该进程发送信号。"),
            ProcessTerminateStatus.Protected => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "受保护进程",
                detail: "不能结束 PID 1（init/systemd）或 Koi 服务自身进程。"),
            ProcessTerminateStatus.StillRunning => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: force ? "进程仍未退出" : "进程未在限期内退出",
                detail: force
                    ? $"已发送 {signal}，但进程仍处于未退出状态（可能处于不可中断等待），请稍后重试。"
                    : $"已发送 {signal}，但进程未在等待时间内退出，可选择强制结束（SIGKILL）或稍后重试。"),
            _ => Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "结束进程失败",
                detail: "未知的进程操作结果。"),
        };
    }

    /// <summary>
    /// 设置端点授权：只允许 uid 0。刻意跳过 <c>Enabled</c> 与平台检查，
    /// 以便管理员在进程管理被关闭或配错时仍能读取并修复配置。
    /// </summary>
    private static (IResult? Error, string UserName) AuthorizeSettings(HttpContext context)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;

        if (!OperatingSystem.IsLinux())
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "进程管理不可用",
                detail: "进程管理依赖 Linux 的 /proc 文件系统，当前平台不支持。"), userName);
        }

        if (string.IsNullOrEmpty(userName))
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再访问进程管理设置。"), userName);
        }

        if (UserProcessIdentityResolver.TryResolve(userName, configuredShell: null) is not { Uid: 0 })
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "无权修改进程管理设置",
                detail: "只有 root 账号可以修改进程管理配置。"), userName);
        }

        return (null, userName);
    }

    private static IResult GetSettings(
        HttpContext context,
        ProcessSettingsService settingsService)
    {
        var (error, _) = AuthorizeSettings(context);
        return error ?? Results.Ok(settingsService.Current);
    }

    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        ProcessOptions? request,
        ProcessSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, userName) = AuthorizeSettings(context);
        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return BadRequest("请求内容为空", "请提供完整的进程管理配置。");
        }

        var settings = ProcessSettingsValidator.Normalize(request);
        if (ProcessSettingsValidator.Validate(settings) is { } detail)
        {
            return BadRequest("配置不合法", detail);
        }

        return Results.Ok(await settingsService.SaveAsync(settings, userName, cancellationToken));
    }

    /// <summary>公共前置校验：返回错误响应或当前配置快照。</summary>
    private static async Task<(IResult? Error, ProcessOptions Settings)> GuardAsync(
        HttpContext context,
        ProcessSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        // 每次请求读取最新快照，保证界面保存后立即生效。
        var settings = settingsService.Current;
        var (error, _) = await ProcessAccessGuard.AuthorizeAsync(context, settings, cancellationToken);
        return (error, settings);
    }

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

    private static IResult NotFound(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title, detail: detail);
}

/// <summary>结束进程的结果状态。</summary>
public enum ProcessTerminateStatus
{
    Exited,
    StillRunning,
    NotFound,
    PermissionDenied,
    Protected,
}

/// <summary>结束进程的尝试结果（含等待时长）。</summary>
public sealed record ProcessTerminateOutcome(ProcessTerminateStatus Status, long WaitedMilliseconds);

/// <summary>进程列表项；CPU 占比以单核为 100%，多线程进程可超过 100。</summary>
public sealed record ProcessSummary(
    int Pid,
    int ParentPid,
    string Name,
    string User,
    uint Uid,
    string State,
    double CpuPercent,
    double MemoryPercent,
    long RssBytes,
    long VirtualBytes,
    int Threads,
    double RuntimeSeconds,
    string CommandLine);

/// <summary>一次进程列表快照；<c>truncated</c> 表示超出最大进程数被截断。</summary>
public sealed record ProcessListResponse(
    ProcessSummary[] Processes,
    int Total,
    bool Truncated,
    DateTimeOffset SampledAt,
    int RefreshIntervalSeconds);

/// <summary>进程详情（列表字段 + 可执行文件、工作目录、IO 与文件描述符）。</summary>
public sealed record ProcessDetail(
    int Pid,
    int ParentPid,
    string Name,
    string User,
    uint Uid,
    string State,
    double CpuPercent,
    double MemoryPercent,
    long RssBytes,
    long VirtualBytes,
    int Threads,
    double RuntimeSeconds,
    string CommandLine,
    string ExecutablePath,
    string WorkingDirectory,
    long? ReadBytes,
    long? WriteBytes,
    int? OpenFileCount,
    DateTimeOffset SampledAt);

/// <summary>结束进程成功响应。</summary>
public sealed record ProcessTerminateResponse(
    bool Ok,
    int Pid,
    string Signal,
    bool Exited,
    long WaitedMilliseconds);
