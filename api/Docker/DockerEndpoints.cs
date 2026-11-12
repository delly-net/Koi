using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Koi.Api.Auth;
using Microsoft.Extensions.Options;

namespace Koi.Api.Docker;

/// <summary>
/// 容器管理端点：容器包管理、镜像包管理与运行状态查看。
/// </summary>
/// <remarks>
/// 所有操作都通过外部 <c>docker</c> 命令行完成，子命令固定白名单；用户输入仅作为容器标识、
/// 镜像引用或数值参数，且经过白名单校验。接口不会执行任意 docker 子命令或 shell。
/// </remarks>
public static class DockerEndpoints
{
    /// <summary>容器 ID/名称：字母数字开头，允许 <c>_ . -</c>，最长 128。</summary>
    private static readonly Regex ContainerIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
        RegexOptions.Compiled);

    /// <summary>镜像引用：允许仓库、标签、摘要与 registry 端口，不得以 <c>-</c> 开头。</summary>
    private static readonly Regex ImageReferencePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:/@-]{0,254}$",
        RegexOptions.Compiled);

    private static readonly string[] ContainerActions = ["start", "stop", "restart", "pause", "unpause"];

    public static IEndpointRouteBuilder MapDockerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/docker")
            .WithTags("Docker")
            .AddEndpointFilter<RequireSessionFilter>();

        group.MapGet("/status", GetStatusAsync);
        group.MapGet("/containers", ListContainersAsync);
        group.MapGet("/containers/{id}", InspectContainerAsync);

        foreach (var action in ContainerActions)
        {
            var actionName = action;
            group.MapPost($"/containers/{{id}}/{actionName}", (HttpContext context, string id,
                    DockerCommandRunner runner, IOptions<DockerOptions> optionsAccessor,
                    CancellationToken cancellationToken) =>
                ContainerActionAsync(context, id, actionName, runner, optionsAccessor, cancellationToken));
        }

        group.MapDelete("/containers/{id}", RemoveContainerAsync);
        group.MapGet("/containers/{id}/logs", GetLogsAsync);
        group.MapGet("/stats", GetStatsAsync);

        group.MapGet("/images", ListImagesAsync);
        group.MapPost("/images/pull", PullImageAsync);
        group.MapPost("/images/prune", PruneImagesAsync);
        group.MapDelete("/images/{id}", RemoveImageAsync);

        return app;
    }

    /// <summary>Docker 版本、守护进程信息与资源概览。</summary>
    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var timeout = CommandTimeout(settings);
        var limit = MaxOutput(settings);

        var versionResult = await runner.RunAsync(
            ["version", "--format", "{{json .}}"],
            timeout,
            limit,
            cancellationToken);
        if (!versionResult.Ok)
        {
            return ToResult(versionResult, "读取 Docker 版本");
        }

        var infoResult = await runner.RunAsync(
            ["info", "--format", "{{json .}}"],
            timeout,
            limit,
            cancellationToken);
        if (!infoResult.Ok)
        {
            return ToResult(infoResult, "读取 Docker 信息");
        }

        string clientVersion;
        string serverVersion;
        string apiVersion;
        string os;
        string arch;

        try
        {
            using var versionDocument = JsonDocument.Parse(versionResult.StandardOutput);
            var version = versionDocument.RootElement;
            var client = GetObject(version, "Client");
            var server = GetObject(version, "Server");

            clientVersion = client is { } clientElement ? GetString(clientElement, "Version") : string.Empty;
            serverVersion = server is { } serverElement ? GetString(serverElement, "Version") : string.Empty;
            apiVersion = server is { } apiElement ? GetString(apiElement, "ApiVersion") : string.Empty;
            os = server is { } osElement ? GetString(osElement, "Os") : string.Empty;
            arch = server is { } archElement ? GetString(archElement, "Arch") : string.Empty;
        }
        catch (JsonException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Docker 返回数据不合法",
                detail: "无法解析 docker version 的 JSON 输出。");
        }

        string driver;
        string infoOs;
        string infoArch;
        string infoServerVersion;
        int containers;
        int containersRunning;
        int containersPaused;
        int containersStopped;
        int images;
        long memoryTotal;
        int cpus;

        try
        {
            using var infoDocument = JsonDocument.Parse(infoResult.StandardOutput);
            var info = infoDocument.RootElement;
            driver = GetString(info, "Driver");
            infoOs = GetString(info, "OperatingSystem");
            infoArch = GetString(info, "Architecture");
            infoServerVersion = GetString(info, "ServerVersion");
            containers = GetInt(info, "Containers");
            containersRunning = GetInt(info, "ContainersRunning");
            containersPaused = GetInt(info, "ContainersPaused");
            containersStopped = GetInt(info, "ContainersStopped");
            images = GetInt(info, "Images");
            memoryTotal = GetLong(info, "MemTotal");
            cpus = GetInt(info, "NCPU");
        }
        catch (JsonException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Docker 返回数据不合法",
                detail: "无法解析 docker info 的 JSON 输出。");
        }

        return Results.Ok(new DockerStatusResponse(
            Available: true,
            ClientVersion: clientVersion,
            ServerVersion: string.IsNullOrEmpty(serverVersion) ? infoServerVersion : serverVersion,
            ApiVersion: apiVersion,
            OperatingSystem: string.IsNullOrEmpty(infoOs) ? os : infoOs,
            Architecture: string.IsNullOrEmpty(infoArch) ? arch : infoArch,
            Driver: driver,
            Containers: containers,
            ContainersRunning: containersRunning,
            ContainersPaused: containersPaused,
            ContainersStopped: containersStopped,
            Images: images,
            MemoryTotal: memoryTotal,
            Cpus: cpus));
    }

    /// <summary>容器列表（包含已停止容器）。</summary>
    private static async Task<IResult> ListContainersAsync(
        HttpContext context,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (result, items) = await runner.RunJsonLinesAsync(
            ["ps", "-a", "--no-trunc", "--format", "{{json .}}"],
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, "加载容器列表");
        }

        return Results.Ok(items.Select(MapContainer).ToArray());
    }

    /// <summary>容器详情摘要（inspect）。</summary>
    private static async Task<IResult> InspectContainerAsync(
        HttpContext context,
        string id,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!IsValidContainerId(id))
        {
            return BadRequest("容器标识不合法", "容器 ID 或名称只能包含字母、数字、下划线、点和短横线。");
        }

        var result = await runner.RunAsync(
            ["inspect", "--format", "{{json .}}", id],
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, "读取容器详情");
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return NotFound("容器不存在", $"未找到容器 {id}。");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return Results.Ok(MapContainerDetail(document.RootElement));
        }
        catch (JsonException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Docker 返回数据不合法",
                detail: "无法解析 docker inspect 的 JSON 输出。");
        }
    }

    /// <summary>执行固定的容器动作：start / stop / restart / pause / unpause。</summary>
    private static async Task<IResult> ContainerActionAsync(
        HttpContext context,
        string id,
        string action,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!ContainerActions.Contains(action, StringComparer.Ordinal))
        {
            return BadRequest("操作不合法", $"不支持的操作：{action}。");
        }

        if (!IsValidContainerId(id))
        {
            return BadRequest("容器标识不合法", "容器 ID 或名称只能包含字母、数字、下划线、点和短横线。");
        }

        var result = await runner.RunAsync(
            [action, id],
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, ActionTitle(action));
        }

        return Results.Ok(new DockerActionResponse(true, action, id));
    }

    /// <summary>删除容器，可选强制删除与同时删除匿名卷。</summary>
    private static async Task<IResult> RemoveContainerAsync(
        HttpContext context,
        string id,
        bool? force,
        bool? volumes,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!IsValidContainerId(id))
        {
            return BadRequest("容器标识不合法", "容器 ID 或名称只能包含字母、数字、下划线、点和短横线。");
        }

        var arguments = new List<string> { "rm" };
        if (force == true)
        {
            arguments.Add("-f");
        }

        if (volumes == true)
        {
            arguments.Add("-v");
        }

        arguments.Add(id);

        var result = await runner.RunAsync(
            arguments,
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        return result.Ok
            ? Results.Ok(new DockerActionResponse(true, "rm", id))
            : ToResult(result, "删除容器");
    }

    /// <summary>容器日志快照（<c>docker logs --tail N</c>）。</summary>
    private static async Task<IResult> GetLogsAsync(
        HttpContext context,
        string id,
        int? tail,
        bool? timestamps,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!IsValidContainerId(id))
        {
            return BadRequest("容器标识不合法", "容器 ID 或名称只能包含字母、数字、下划线、点和短横线。");
        }

        var maxTail = settings.LogTailMax > 0 ? settings.LogTailMax : 5000;
        var defaultTail = settings.LogTailDefault > 0 ? settings.LogTailDefault : 500;
        var lineCount = Math.Clamp(tail ?? defaultTail, 1, maxTail);

        var arguments = new List<string>
        {
            "logs",
            "--tail",
            lineCount.ToString(CultureInfo.InvariantCulture),
        };
        if (timestamps == true)
        {
            arguments.Add("--timestamps");
        }

        arguments.Add(id);

        var maxBytes = settings.MaxLogBytes > 0 ? settings.MaxLogBytes : 2 * 1024 * 1024;
        var result = await runner.RunAsync(
            arguments,
            CommandTimeout(settings),
            maxBytes,
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, "读取容器日志");
        }

        // docker logs 把容器 stdout 写标准输出、容器 stderr 写标准错误；合并展示时无法还原严格顺序。
        var content = result.StandardError.Length > 0
            ? $"{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            : result.StandardOutput;

        return Results.Ok(new DockerLogsResponse(
            id,
            lineCount,
            content,
            Truncated: false));
    }

    /// <summary>资源占用快照（<c>docker stats --no-stream</c>）。</summary>
    private static async Task<IResult> GetStatsAsync(
        HttpContext context,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (result, items) = await runner.RunJsonLinesAsync(
            ["stats", "--no-stream", "--format", "{{json .}}"],
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, "读取容器资源占用");
        }

        return Results.Ok(items.Select(item => new DockerStatsEntry(
            Id: GetString(item, "Container"),
            Name: GetString(item, "Name"),
            CpuPercent: GetString(item, "CPUPerc"),
            MemUsage: GetString(item, "MemUsage"),
            MemPercent: GetString(item, "MemPerc"),
            NetIo: GetString(item, "NetIO"),
            BlockIo: GetString(item, "BlockIO"),
            Pids: GetString(item, "PIDs"))).ToArray());
    }

    /// <summary>镜像列表。</summary>
    private static async Task<IResult> ListImagesAsync(
        HttpContext context,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (result, items) = await runner.RunJsonLinesAsync(
            ["images", "--format", "{{json .}}"],
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, "加载镜像列表");
        }

        return Results.Ok(items.Select(item => new DockerImageSummary(
            Id: GetString(item, "ID"),
            Repository: GetString(item, "Repository"),
            Tag: GetString(item, "Tag"),
            Digest: GetString(item, "Digest"),
            CreatedAt: GetString(item, "CreatedAt"),
            Size: GetString(item, "Size"),
            Containers: GetString(item, "Containers"))).ToArray());
    }

    /// <summary>拉取镜像；使用 <c>--quiet</c> 抑制逐层进度输出，避免输出超限。</summary>
    private static async Task<IResult> PullImageAsync(
        HttpContext context,
        DockerPullRequest? request,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (request?.Reference is null || !IsValidImageReference(request.Reference))
        {
            return BadRequest(
                "镜像引用不合法",
                "镜像引用不能为空，只能包含字母、数字、下划线、点、冒号、斜杠、@ 和短横线，且不得以短横线开头。");
        }

        var timeoutSeconds = settings.PullTimeoutSeconds > 0 ? settings.PullTimeoutSeconds : 600;
        var result = await runner.RunAsync(
            ["pull", "--quiet", request.Reference],
            TimeSpan.FromSeconds(timeoutSeconds),
            MaxOutput(settings),
            cancellationToken);
        if (!result.Ok)
        {
            return ToResult(result, "拉取镜像");
        }

        return Results.Ok(new DockerPullResponse(true, request.Reference, result.StandardOutput.Trim()));
    }

    /// <summary>删除镜像。</summary>
    private static async Task<IResult> RemoveImageAsync(
        HttpContext context,
        string id,
        bool? force,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!IsValidImageReference(id))
        {
            return BadRequest("镜像标识不合法", "镜像 ID 或引用格式不合法。");
        }

        var arguments = new List<string> { "rmi" };
        if (force == true)
        {
            arguments.Add("-f");
        }

        arguments.Add(id);

        var result = await runner.RunAsync(
            arguments,
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        if (result.Ok)
        {
            return Results.Ok(new DockerActionResponse(true, "rmi", id));
        }

        // 常见冲突原因单独给出可读提示；前端据 409 决定是否引导强制删除。
        var stderr = result.StandardError;
        if (stderr.Contains("image is referenced in multiple repositories", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "镜像被多个仓库标签引用",
                detail: force == true
                    ? "即使强制删除也未能移除该镜像，请检查其余标签或容器引用后重试。"
                    : "该镜像被多个仓库标签共同引用，普通删除会被拒绝；确认后可强制删除（会同时移除该镜像的所有标签）。");
        }

        if (stderr.Contains("image is being used by running container", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "镜像正在被容器使用",
                detail: "该镜像正被运行中的容器使用，强制删除也无法移除；请先停止并删除相关容器。");
        }

        if (stderr.Contains("image is being used by stopped container", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "镜像被已停止的容器使用",
                detail: "该镜像仍被已停止的容器引用，需要强制删除；强制删除不会删除容器。");
        }

        return ToResult(result, "删除镜像");
    }

    /// <summary>清理悬空镜像（<c>docker image prune -f</c>）。</summary>
    private static async Task<IResult> PruneImagesAsync(
        HttpContext context,
        DockerCommandRunner runner,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, optionsAccessor, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var result = await runner.RunAsync(
            ["image", "prune", "-f"],
            CommandTimeout(settings),
            MaxOutput(settings),
            cancellationToken);
        return result.Ok
            ? Results.Ok(new DockerPruneResponse(true, result.StandardOutput.Trim()))
            : ToResult(result, "清理悬空镜像");
    }

    /// <summary>公共前置校验：返回错误响应或当前配置。</summary>
    private static async Task<(IResult? Error, DockerOptions Settings)> GuardAsync(
        HttpContext context,
        IOptions<DockerOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var settings = optionsAccessor.Value;
        var (error, _) = await DockerAccessGuard.AuthorizeAsync(context, settings, cancellationToken);
        return (error, settings);
    }

    private static DockerContainerSummary MapContainer(JsonElement item) => new(
        Id: GetString(item, "ID"),
        Names: GetString(item, "Names"),
        Image: GetString(item, "Image"),
        Command: GetString(item, "Command"),
        CreatedAt: GetString(item, "CreatedAt"),
        RunningFor: GetString(item, "RunningFor"),
        Status: GetString(item, "Status"),
        State: GetString(item, "State"),
        Ports: GetString(item, "Ports"),
        Labels: GetString(item, "Labels"));

    private static DockerContainerDetail MapContainerDetail(JsonElement root)
    {
        var state = GetObject(root, "State");
        var config = GetObject(root, "Config");

        var ports = new List<DockerPortBinding>();
        if (root.TryGetProperty("NetworkSettings", out var network) &&
            GetObject(network, "Ports") is { } portMap)
        {
            foreach (var entry in portMap.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var binding in entry.Value.EnumerateArray())
                    {
                        ports.Add(new DockerPortBinding(
                            entry.Name,
                            GetString(binding, "HostIp"),
                            GetString(binding, "HostPort")));
                    }
                }
                else
                {
                    ports.Add(new DockerPortBinding(entry.Name, null, null));
                }
            }
        }

        var mounts = new List<DockerMountInfo>();
        if (root.TryGetProperty("Mounts", out var mountArray) && mountArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var mount in mountArray.EnumerateArray())
            {
                mounts.Add(new DockerMountInfo(
                    GetString(mount, "Type"),
                    GetString(mount, "Source"),
                    GetString(mount, "Destination"),
                    GetString(mount, "Mode")));
            }
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config is { } configElement && GetObject(configElement, "Labels") is { } labelMap)
        {
            foreach (var label in labelMap.EnumerateObject())
            {
                labels[label.Name] = label.Value.ValueKind == JsonValueKind.String
                    ? label.Value.GetString() ?? string.Empty
                    : label.Value.ToString();
            }
        }

        var name = GetString(root, "Name").TrimStart('/');

        return new DockerContainerDetail(
            Id: GetString(root, "Id"),
            Name: name,
            Image: config is { } imageConfig ? GetString(imageConfig, "Image") : string.Empty,
            Status: state is { } stateElement ? GetString(stateElement, "Status") : string.Empty,
            Running: state is { } runningElement && GetBool(runningElement, "Running"),
            Paused: state is { } pausedElement && GetBool(pausedElement, "Paused"),
            Restarting: state is { } restartingElement && GetBool(restartingElement, "Restarting"),
            Created: GetString(root, "Created"),
            StartedAt: state is { } startedElement ? GetString(startedElement, "StartedAt") : string.Empty,
            FinishedAt: state is { } finishedElement ? GetString(finishedElement, "FinishedAt") : string.Empty,
            ExitCode: state is { } exitElement ? GetInt(exitElement, "ExitCode") : 0,
            RestartCount: GetInt(root, "RestartCount"),
            Command: config is { } commandConfig ? GetStringArray(commandConfig, "Cmd") : [],
            Entrypoint: config is { } entrypointConfig ? GetStringArray(entrypointConfig, "Entrypoint") : [],
            Ports: ports.ToArray(),
            Mounts: mounts.ToArray(),
            Labels: labels);
    }

    private static bool IsValidContainerId(string? id) =>
        !string.IsNullOrEmpty(id) && ContainerIdPattern.IsMatch(id);

    private static bool IsValidImageReference(string? reference) =>
        !string.IsNullOrEmpty(reference) && ImageReferencePattern.IsMatch(reference);

    private static string ActionTitle(string action) => action switch
    {
        "start" => "启动容器",
        "stop" => "停止容器",
        "restart" => "重启容器",
        "pause" => "暂停容器",
        "unpause" => "恢复容器",
        _ => "容器操作",
    };

    /// <summary>把 docker 命令失败映射为 ProblemDetails（400/403/404/409/413/503/504）。</summary>
    private static IResult ToResult(DockerCommandResult result, string title)
    {
        if (result.TimedOut)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: $"{title}超时",
                detail: "docker 命令执行时间超过上限，已终止。可稍后重试或缩小操作范围。");
        }

        if (result.OutputTooLarge)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"{title}输出过大",
                detail: "docker 命令输出超过上限，已终止。请缩小日志 tail 行数或减少目标数量。");
        }

        var stderr = result.StandardError.Trim();
        var lower = stderr.ToLowerInvariant();

        if (result.ExitCode == -1)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Docker 不可用",
                detail: string.IsNullOrEmpty(stderr) ? "无法执行 docker 命令。" : stderr);
        }

        if (lower.Contains("permission denied", StringComparison.Ordinal) ||
            lower.Contains("pull access denied", StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Docker 权限不足",
                detail: stderr);
        }

        if (lower.Contains("no such", StringComparison.Ordinal) ||
            lower.Contains("not found", StringComparison.Ordinal) ||
            lower.Contains("manifest unknown", StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "目标不存在",
                detail: stderr);
        }

        if (lower.Contains("is in use", StringComparison.Ordinal) ||
            lower.Contains("conflict", StringComparison.Ordinal) ||
            (lower.Contains("removal of", StringComparison.Ordinal) &&
             lower.Contains("prohibited", StringComparison.Ordinal)))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "操作冲突",
                detail: stderr);
        }

        if (lower.Contains("cannot connect to the docker daemon", StringComparison.Ordinal) ||
            lower.Contains("docker daemon is not running", StringComparison.Ordinal) ||
            lower.Contains("is the docker daemon running", StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Docker 守护进程不可用",
                detail: stderr);
        }

        return Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: $"{title}失败",
            detail: string.IsNullOrEmpty(stderr) ? "docker 命令执行失败。" : stderr);
    }

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

    private static IResult NotFound(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title, detail: detail);

    private static TimeSpan CommandTimeout(DockerOptions settings) =>
        TimeSpan.FromSeconds(settings.CommandTimeoutSeconds > 0 ? settings.CommandTimeoutSeconds : 60);

    private static long MaxOutput(DockerOptions settings) =>
        settings.MaxOutputBytes > 0 ? settings.MaxOutputBytes : 4L * 1024 * 1024;

    private static JsonElement? GetObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : 0;

    private static long GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static string[] GetStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
    }
}

public sealed record DockerStatusResponse(
    bool Available,
    string ClientVersion,
    string ServerVersion,
    string ApiVersion,
    string OperatingSystem,
    string Architecture,
    string Driver,
    int Containers,
    int ContainersRunning,
    int ContainersPaused,
    int ContainersStopped,
    int Images,
    long MemoryTotal,
    int Cpus);

public sealed record DockerContainerSummary(
    string Id,
    string Names,
    string Image,
    string Command,
    string CreatedAt,
    string RunningFor,
    string Status,
    string State,
    string Ports,
    string Labels);

public sealed record DockerPortBinding(string ContainerPort, string? HostIp, string? HostPort);

public sealed record DockerMountInfo(string Type, string Source, string Destination, string Mode);

public sealed record DockerContainerDetail(
    string Id,
    string Name,
    string Image,
    string Status,
    bool Running,
    bool Paused,
    bool Restarting,
    string Created,
    string StartedAt,
    string FinishedAt,
    int ExitCode,
    int RestartCount,
    string[] Command,
    string[] Entrypoint,
    DockerPortBinding[] Ports,
    DockerMountInfo[] Mounts,
    Dictionary<string, string> Labels);

public sealed record DockerActionResponse(bool Ok, string Action, string Id);

public sealed record DockerLogsResponse(string Id, int Tail, string Content, bool Truncated);

public sealed record DockerStatsEntry(
    string Id,
    string Name,
    string CpuPercent,
    string MemUsage,
    string MemPercent,
    string NetIo,
    string BlockIo,
    string Pids);

public sealed record DockerImageSummary(
    string Id,
    string Repository,
    string Tag,
    string Digest,
    string CreatedAt,
    string Size,
    string Containers);

public sealed record DockerPullRequest(string? Reference);

public sealed record DockerPullResponse(bool Ok, string Reference, string Output);

public sealed record DockerPruneResponse(bool Ok, string Output);
