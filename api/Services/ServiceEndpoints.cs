using System.Globalization;
using System.Text.RegularExpressions;
using Koi.Api.Auth;
using Koi.Api.Terminal;

namespace Koi.Api.Services;

/// <summary>
/// 服务管理端点：systemd 服务列表、详情、生命周期、开机自启与日志快照。
/// </summary>
/// <remarks>
/// 所有操作都通过外部 <c>systemctl</c> / <c>journalctl</c> 命令行完成，子命令固定白名单；
/// 用户输入仅作为经过校验的 <c>.service</c> 单元名或 tail 行数，不会执行任意命令或 shell。
/// </remarks>
public static class ServiceEndpoints
{
    /// <summary>
    /// 服务单元名：字母或数字开头，允许 <c>_ . @ : -</c> 与 systemd 转义用的反斜杠，必须以
    /// <c>.service</c> 结尾。禁止空格、斜杠、glob 字符与以 <c>-</c> 开头的名称。
    /// </summary>
    private static readonly Regex UnitNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_.@:\x5c-]{0,200}\.service$",
        RegexOptions.Compiled);

    /// <summary><c>systemctl list-units --plain --no-legend</c> 的列：UNIT LOAD ACTIVE SUB DESCRIPTION。</summary>
    private static readonly Regex UnitLinePattern = new(
        @"^(?<unit>\S+)\s+(?<load>\S+)\s+(?<active>\S+)\s+(?<sub>\S+)(?:\s+(?<description>.*))?$",
        RegexOptions.Compiled);

    private static readonly string[] ServiceActions = ["start", "stop", "restart", "reload", "enable", "disable"];

    private static readonly string[] DetailProperties =
    [
        "Id",
        "Description",
        "LoadState",
        "ActiveState",
        "SubState",
        "UnitFileState",
        "FragmentPath",
        "MainPID",
        "ExecMainPID",
        "ActiveEnterTimestamp",
        "MemoryCurrent",
        "TasksCurrent",
        "NRestarts",
        "Result",
        "ExecStart",
        "CanReload",
        "NeedDaemonReload",
    ];

    public static IEndpointRouteBuilder MapServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/services")
            .WithTags("Services")
            .AddEndpointFilter<RequireSessionFilter>();

        // 设置端点使用字面量路径，必须早于 /{name} 注册；设置端点刻意跳过 Enabled 与
        // systemctl 可用性检查，便于管理员修复被关闭或配错的服务管理。
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettingsAsync);

        group.MapGet("", ListServicesAsync);
        group.MapGet("/{name}", GetServiceAsync);

        foreach (var action in ServiceActions)
        {
            var actionName = action;
            group.MapPost($"/{{name}}/{actionName}", (HttpContext context, string name,
                    SystemctlCommandRunner runner, ServiceSettingsService settingsService,
                    CancellationToken cancellationToken) =>
                ServiceActionAsync(context, name, actionName, runner, settingsService, cancellationToken));
        }

        group.MapGet("/{name}/logs", GetLogsAsync);

        return app;
    }

    /// <summary>
    /// 服务列表：合并已加载单元（运行状态、描述）与已安装单元文件（自启状态），
    /// 因此已停止且未加载的服务也会出现在列表中。
    /// </summary>
    private static async Task<IResult> ListServicesAsync(
        HttpContext context,
        SystemctlCommandRunner runner,
        ServiceSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var timeout = CommandTimeout(settings);
        var limit = MaxOutput(settings);

        // 两个列表互不依赖，并行执行以缩短首次加载时间。
        var unitsTask = runner.RunSystemctlAsync(
            ["list-units", "--type=service", "--all", "--no-pager", "--no-legend", "--plain"],
            timeout,
            limit,
            cancellationToken);
        var filesTask = runner.RunSystemctlAsync(
            ["list-unit-files", "--type=service", "--no-pager", "--no-legend"],
            timeout,
            limit,
            cancellationToken);

        await Task.WhenAll(unitsTask, filesTask);
        var units = await unitsTask;
        var files = await filesTask;

        if (!units.Ok)
        {
            return ToResult(units, "加载服务列表");
        }

        if (!files.Ok)
        {
            return ToResult(files, "加载服务列表");
        }

        return Results.Ok(MergeServices(units.StandardOutput, files.StandardOutput));
    }

    /// <summary>服务详情（<c>systemctl show</c> 的精选属性）。</summary>
    private static async Task<IResult> GetServiceAsync(
        HttpContext context,
        string name,
        SystemctlCommandRunner runner,
        ServiceSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!IsValidUnitName(name))
        {
            return BadRequest("服务名不合法", "服务名必须是形如 nginx.service 的 systemd 单元名，且不能包含空格、斜杠或通配符。");
        }

        var result = await runner.RunSystemctlAsync(
            ["show", name, "--no-pager", $"--property={string.Join(',', DetailProperties)}"],
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, "读取服务详情");
        }

        var values = ParseKeyValue(result.StandardOutput);
        var loadState = GetValue(values, "LoadState");

        // systemctl show 对不存在的单元也返回退出码 0，只能依据 LoadState 判定 404。
        if (string.IsNullOrEmpty(loadState) || string.Equals(loadState, "not-found", StringComparison.Ordinal))
        {
            return NotFound("服务不存在", $"未找到服务单元 {name}。");
        }

        return Results.Ok(new ServiceDetail(
            Name: GetValue(values, "Id") is { Length: > 0 } id ? id : name,
            Description: GetValue(values, "Description"),
            LoadState: loadState,
            ActiveState: GetValue(values, "ActiveState"),
            SubState: GetValue(values, "SubState"),
            UnitFileState: GetValue(values, "UnitFileState"),
            FragmentPath: GetValue(values, "FragmentPath"),
            MainPid: ParseLong(values, "MainPID"),
            ExecMainPid: ParseLong(values, "ExecMainPID"),
            ActiveEnterTimestamp: GetValue(values, "ActiveEnterTimestamp"),
            MemoryCurrent: ParseLong(values, "MemoryCurrent"),
            TasksCurrent: ParseLong(values, "TasksCurrent"),
            RestartCount: ParseInt(values, "NRestarts"),
            Result: GetValue(values, "Result"),
            ExecStart: GetValue(values, "ExecStart"),
            CanReload: GetValue(values, "CanReload"),
            NeedDaemonReload: GetValue(values, "NeedDaemonReload")));
    }

    /// <summary>执行固定的服务动作：start / stop / restart / reload / enable / disable。</summary>
    private static async Task<IResult> ServiceActionAsync(
        HttpContext context,
        string name,
        string action,
        SystemctlCommandRunner runner,
        ServiceSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!ServiceActions.Contains(action, StringComparer.Ordinal))
        {
            return BadRequest("操作不合法", $"不支持的操作：{action}。");
        }

        if (!IsValidUnitName(name))
        {
            return BadRequest("服务名不合法", "服务名必须是形如 nginx.service 的 systemd 单元名，且不能包含空格、斜杠或通配符。");
        }

        var result = await runner.RunSystemctlAsync(
            [action, name],
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, ActionTitle(action));
        }

        return Results.Ok(new ServiceActionResponse(true, action, name));
    }

    /// <summary>服务日志快照（<c>journalctl -u &lt;unit&gt; -n &lt;tail&gt;</c>）。</summary>
    private static async Task<IResult> GetLogsAsync(
        HttpContext context,
        string name,
        int? tail,
        SystemctlCommandRunner runner,
        ServiceSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!IsValidUnitName(name))
        {
            return BadRequest("服务名不合法", "服务名必须是形如 nginx.service 的 systemd 单元名，且不能包含空格、斜杠或通配符。");
        }

        if (!SystemctlCommandRunner.IsJournalctlAvailable(settings))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "服务日志不可用",
                detail: "未找到 journalctl 可执行文件，请确认系统使用 systemd，或在服务管理设置中配置 journalctl 路径。");
        }

        var maxTail = settings.LogTailMax > 0 ? settings.LogTailMax : 5000;
        var defaultTail = settings.LogTailDefault > 0 ? settings.LogTailDefault : 500;
        var lineCount = Math.Clamp(tail ?? defaultTail, 1, maxTail);

        var maxBytes = settings.MaxLogBytes > 0 ? settings.MaxLogBytes : 2 * 1024 * 1024;
        var result = await runner.RunJournalctlAsync(
            [
                "-u", name,
                "-n", lineCount.ToString(CultureInfo.InvariantCulture),
                "-q",
                "--no-pager",
                "-o", "short-iso",
            ],
            CommandTimeout(settings),
            maxBytes,
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, "读取服务日志");
        }

        return Results.Ok(new ServiceLogsResponse(name, lineCount, result.StandardOutput, Truncated: false));
    }

    /// <summary>合并两个 systemctl 列表输出；单元文件列表提供自启状态，单元列表提供运行状态与描述。</summary>
    private static ServiceSummary[] MergeServices(string unitsOutput, string unitFilesOutput)
    {
        var entries = new Dictionary<string, ServiceSummaryBuilder>(StringComparer.Ordinal);

        foreach (var line in unitFilesOutput.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            // 列：UNIT FILE  STATE  [PRESET]，第三列在部分版本存在，这里只取前两列。
            var parts = text.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IsServiceUnitName(parts[0]))
            {
                continue;
            }

            entries[parts[0]] = new ServiceSummaryBuilder
            {
                Name = parts[0],
                UnitFileState = parts[1],
            };
        }

        foreach (var line in unitsOutput.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var match = UnitLinePattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var unitName = match.Groups["unit"].Value;
            if (!IsServiceUnitName(unitName))
            {
                continue;
            }

            if (!entries.TryGetValue(unitName, out var entry))
            {
                entry = new ServiceSummaryBuilder { Name = unitName };
                entries[unitName] = entry;
            }

            entry.LoadState = match.Groups["load"].Value;
            entry.ActiveState = match.Groups["active"].Value;
            entry.SubState = match.Groups["sub"].Value;
            entry.Description = match.Groups["description"].Value.Trim();
        }

        return entries.Values
            .Select(entry => new ServiceSummary(
                entry.Name,
                entry.Description,
                entry.LoadState,
                entry.ActiveState,
                entry.SubState,
                entry.UnitFileState))
            .OrderBy(service => service.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, string> ParseKeyValue(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            values[line[..index]] = line[(index + 1)..].TrimEnd('\r');
        }

        return values;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static long? ParseLong(IReadOnlyDictionary<string, string> values, string key) =>
        long.TryParse(GetValue(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.TryParse(GetValue(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    private static bool IsServiceUnitName(string name) =>
        name.EndsWith(".service", StringComparison.Ordinal);

    private static bool IsValidUnitName(string? name) =>
        !string.IsNullOrEmpty(name) && UnitNamePattern.IsMatch(name);

    private static string ActionTitle(string action) => action switch
    {
        "start" => "启动服务",
        "stop" => "停止服务",
        "restart" => "重启服务",
        "reload" => "重载服务",
        "enable" => "启用服务自启",
        "disable" => "禁用服务自启",
        _ => "服务操作",
    };

    /// <summary>把 systemctl / journalctl 失败映射为 ProblemDetails（400/403/404/409/413/503/504）。</summary>
    private static IResult ToResult(ServiceCommandResult result, string title)
    {
        if (result.TimedOut)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: $"{title}超时",
                detail: "systemctl 命令执行时间超过上限，已终止。可稍后重试或检查服务是否卡在停止/启动阶段。");
        }

        if (result.OutputTooLarge)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"{title}输出过大",
                detail: "命令输出超过上限，已终止。请缩小日志 tail 行数后重试。");
        }

        var stderr = result.StandardError.Trim();
        var lower = stderr.ToLowerInvariant();

        if (result.ExitCode == -1)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "systemctl 不可用",
                detail: string.IsNullOrEmpty(stderr) ? "无法执行 systemctl 命令。" : Truncate(stderr));
        }

        if (lower.Contains("system has not been booted with systemd", StringComparison.Ordinal) ||
            lower.Contains("failed to connect to bus", StringComparison.Ordinal) ||
            lower.Contains("failed to connect to system bus", StringComparison.Ordinal) ||
            lower.Contains("bus connection failed", StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "systemd 不可用",
                detail: Truncate(stderr));
        }

        if (lower.Contains("permission denied", StringComparison.Ordinal) ||
            lower.Contains("access denied", StringComparison.Ordinal) ||
            lower.Contains("authentication is required", StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "系统权限不足",
                detail: Truncate(stderr));
        }

        if (lower.Contains("not found", StringComparison.Ordinal) ||
            lower.Contains("could not be found", StringComparison.Ordinal) ||
            lower.Contains("does not exist", StringComparison.Ordinal) ||
            lower.Contains("no such file", StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "服务不存在",
                detail: Truncate(stderr));
        }

        if (lower.Contains("is masked", StringComparison.Ordinal) ||
            lower.Contains("not applicable", StringComparison.Ordinal) ||
            lower.Contains("not supported", StringComparison.Ordinal) ||
            lower.Contains("read-only file system", StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "操作冲突",
                detail: Truncate(stderr));
        }

        return Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: $"{title}失败",
            detail: string.IsNullOrEmpty(stderr) ? "systemctl 命令执行失败。" : Truncate(stderr));
    }

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

    private static IResult NotFound(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title, detail: detail);

    private static TimeSpan CommandTimeout(ServiceOptions settings) =>
        TimeSpan.FromSeconds(settings.CommandTimeoutSeconds > 0 ? settings.CommandTimeoutSeconds : 120);

    private static long MaxOutput(ServiceOptions settings) =>
        settings.MaxOutputBytes > 0 ? settings.MaxOutputBytes : 4L * 1024 * 1024;

    /// <summary>限制写入 ProblemDetails 的 stderr 长度，避免超长命令输出直接回传。</summary>
    private static string Truncate(string value) =>
        value.Length <= 4000 ? value : $"{value[..4000]}…";

    /// <summary>
    /// 设置端点授权：只允许 uid 0。刻意跳过 <c>Enabled</c> 与 systemctl 可用性检查，
    /// 以便管理员在服务管理被关闭或配错时仍能读取并修复配置。
    /// </summary>
    private static (IResult? Error, string UserName) AuthorizeSettings(HttpContext context)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;

        if (!OperatingSystem.IsLinux())
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "服务管理不可用",
                detail: "服务管理依赖 Linux 的 systemd，当前平台不支持。"), userName);
        }

        if (string.IsNullOrEmpty(userName))
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再访问服务管理设置。"), userName);
        }

        if (UserProcessIdentityResolver.TryResolve(userName, configuredShell: null) is not { Uid: 0 })
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "无权修改服务管理设置",
                detail: "只有 root 账号可以修改服务管理配置。"), userName);
        }

        return (null, userName);
    }

    private static IResult GetSettings(
        HttpContext context,
        ServiceSettingsService settingsService)
    {
        var (error, _) = AuthorizeSettings(context);
        return error ?? Results.Ok(settingsService.Current);
    }

    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        ServiceOptions? request,
        ServiceSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, userName) = AuthorizeSettings(context);
        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return BadRequest("请求内容为空", "请提供完整的服务管理配置。");
        }

        var settings = ServiceSettingsValidator.Normalize(request);
        if (ServiceSettingsValidator.Validate(settings) is { } detail)
        {
            return BadRequest("配置不合法", detail);
        }

        return Results.Ok(await settingsService.SaveAsync(settings, userName, cancellationToken));
    }

    /// <summary>公共前置校验：返回错误响应或当前配置快照。</summary>
    private static async Task<(IResult? Error, ServiceOptions Settings)> GuardAsync(
        HttpContext context,
        ServiceSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        // 每次请求读取最新快照，保证界面保存后立即生效。
        var settings = settingsService.Current;
        var (error, _) = await ServiceAccessGuard.AuthorizeAsync(context, settings, cancellationToken);
        return (error, settings);
    }

    private sealed class ServiceSummaryBuilder
    {
        public string Name { get; init; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string LoadState { get; set; } = string.Empty;

        public string ActiveState { get; set; } = string.Empty;

        public string SubState { get; set; } = string.Empty;

        public string UnitFileState { get; set; } = string.Empty;
    }
}

public sealed record ServiceSummary(
    string Name,
    string Description,
    string LoadState,
    string ActiveState,
    string SubState,
    string UnitFileState);

public sealed record ServiceDetail(
    string Name,
    string Description,
    string LoadState,
    string ActiveState,
    string SubState,
    string UnitFileState,
    string FragmentPath,
    long? MainPid,
    long? ExecMainPid,
    string ActiveEnterTimestamp,
    long? MemoryCurrent,
    long? TasksCurrent,
    int RestartCount,
    string Result,
    string ExecStart,
    string CanReload,
    string NeedDaemonReload);

public sealed record ServiceActionResponse(bool Ok, string Action, string Name);

public sealed record ServiceLogsResponse(string Name, int Tail, string Content, bool Truncated);
