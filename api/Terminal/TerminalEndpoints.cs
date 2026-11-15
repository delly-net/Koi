using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Koi.Api.Auth;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Koi.Api.Terminal;

/// <summary>在线终端端点：WebSocket 承载输入输出，PTY 由 <see cref="PosixPty"/> 提供。</summary>
public static class TerminalEndpoints
{
    private const int ReceiveBufferSize = 8192;
    private const int MinDimension = 1;
    private const int MaxDimension = 1000;

    /// <summary>PTY 读到对端关闭后，等待子进程退出的最长时间。</summary>
    private const int PtyWaitMs = 1000;

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/terminal")
            .WithTags("Terminal")
            .MapGet("/ws", HandleWebSocketAsync);

        // 设置端点仅 root 可用，且不受 Enabled / 平台检查影响，保证关闭终端后仍能修复配置。
        var settingsGroup = app.MapGroup("/api/terminal")
            .WithTags("Terminal")
            .AddEndpointFilter<RequireSessionFilter>();

        settingsGroup.MapGet("/settings", GetSettings);
        settingsGroup.MapPut("/settings", SaveSettingsAsync);

        return app;
    }

    private static async Task HandleWebSocketAsync(
        HttpContext context,
        TerminalSessionManager sessions,
        TerminalSettingsService settingsService,
        IOptions<AuthOptions> authOptionsAccessor,
        ILoggerFactory loggerFactory)
    {
        // 取当前快照：保存设置后，新连接立即使用新值。
        var options = settingsService.Current;
        var logger = loggerFactory.CreateLogger("Koi.Api.Terminal");

        if (!options.Enabled)
        {
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "终端能力已关闭", "管理员已禁用在线终端。");
            return;
        }

        // 未登录优先返回 401，避免向匿名调用方暴露功能可用性。
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "未登录", "请先登录后再打开终端。");
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "终端能力不可用", "在线终端依赖 Linux PTY，当前平台不支持。");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "请求不合法", "该端点仅接受 WebSocket 连接。");
            return;
        }

        // WebSocket 握手是 GET，不经过 SameOriginMiddleware 的非安全方法校验，这里必须显式校验来源。
        if (context.Request.Headers.TryGetValue(HeaderNames.Origin, out var originValues))
        {
            var origin = originValues.ToString();
            if (!string.IsNullOrEmpty(origin) &&
                !SameOriginMiddleware.IsAllowedOrigin(context, origin, authOptionsAccessor.Value.AllowedOrigins))
            {
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "请求来源不被允许", "检测到跨站连接，已拒绝。");
                return;
            }
        }

        var userName = context.User.Identity.Name ?? string.Empty;
        var identity = UserProcessIdentityResolver.TryResolve(userName, options.Shell);
        if (identity is null)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status403Forbidden,
                "无法启动终端",
                "当前账号在系统账号库中不存在，或用户名不合法，无法降权启动终端。");
            return;
        }

        // 可选的工作目录来自文件管理「打开终端」；校验失败直接拒绝，不拉起 PTY。
        if (!TryNormalizeWorkingDirectory(context.Request.Query["cwd"].FirstOrDefault(), out var workingDirectory, out var workingDirectoryError))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "目录无效", workingDirectoryError ?? "工作目录不合法。");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var sendLock = new SemaphoreSlim(1, 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var cancellationToken = lifetime.Token;

        var session = sessions.TryCreate(
            userName,
            () => new TerminalSession(
                PosixPty.Start(
                    identity,
                    [identity.Shell, .. options.ShellArguments],
                    options.DefaultCols,
                    options.DefaultRows,
                    options.CloseTimeoutSeconds,
                    workingDirectory),
                userName,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                context.Connection.RemotePort),
            out var createError);

        if (session is null)
        {
            await TrySendAsync(socket, new TerminalErrorMessage("error", createError ?? "创建终端会话失败。"), sendLock, cancellationToken);
            await TryCloseAsync(socket, WebSocketCloseStatus.InternalServerError, "终端启动失败", cancellationToken);
            return;
        }

        try
        {
            await TrySendAsync(
                socket,
                new TerminalReadyMessage(
                    "ready",
                    session.Id,
                    userName,
                    session.Pty.ProcessId,
                    Math.Clamp(options.DefaultCols, MinDimension, MaxDimension),
                    Math.Clamp(options.DefaultRows, MinDimension, MaxDimension)),
                sendLock,
                cancellationToken);

            var pumpCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            StartPtyPump(session, socket, sendLock, logger, cancellationToken, pumpCompletion);

            var receiveTask = ReceiveLoopAsync(socket, session, options, sendLock, logger, cancellationToken);
            var completed = await Task.WhenAny(receiveTask, pumpCompletion.Task);

            if (completed == pumpCompletion.Task)
            {
                var exitCode = await pumpCompletion.Task;
                await TrySendAsync(socket, new TerminalExitMessage("exit", exitCode), sendLock, CancellationToken.None);
                await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "终端进程已退出", CancellationToken.None);

                // 给接收循环一点时间收尾，避免直接丢弃未处理的控制帧。
                await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或宿主关闭，按正常回收处理。
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "终端会话 {SessionId} 的 WebSocket 连接异常结束。", session.Id);
        }
        finally
        {
            lifetime.Cancel();
            sessions.Remove(session.Id);
        }
    }

    /// <summary>把 PTY 输出转发到 WebSocket；子进程退出时通过 <paramref name="completion"/> 通知主流程。</summary>
    private static void StartPtyPump(
        TerminalSession session,
        WebSocket socket,
        SemaphoreSlim sendLock,
        ILogger logger,
        CancellationToken cancellationToken,
        TaskCompletionSource<int> completion)
    {
        var thread = new Thread(() =>
        {
            var buffer = new byte[ReceiveBufferSize];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = session.Pty.Read(buffer);
                    if (read <= 0)
                    {
                        break;
                    }

                    // 增量解码：PTY 的分片可能切断多字节字符。
                    var count = decoder.GetChars(buffer, 0, read, chars, 0);
                    if (count == 0)
                    {
                        continue;
                    }

                    var message = new TerminalOutputMessage("output", new string(chars, 0, count));
                    TrySendAsync(socket, message, sendLock, cancellationToken).GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException)
            {
                // 会话关闭，正常结束。
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "终端会话 {SessionId} 的输出转发已结束。", session.Id);
            }
            finally
            {
                // 读到对端关闭后给子进程一点时间退出，尽量带回真实退出码（等不到则为 -1）。
                completion.TrySetResult(session.Pty.WaitForExitCode(PtyWaitMs));
            }
        })
        {
            IsBackground = true,
            Name = $"koi-terminal-{session.Id[..8]}",
        };

        thread.Start();
    }

    /// <summary>接收客户端控制帧：输入、窗口尺寸与心跳。</summary>
    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        TerminalSession session,
        TerminalOptions options,
        SemaphoreSlim sendLock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // 协议只接受 JSON 文本帧，二进制帧直接丢弃。
                    message.SetLength(0);
                    continue;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > options.MaxInputBytes)
                {
                    await TrySendAsync(socket, new TerminalErrorMessage("error", "输入内容超过大小限制。"), sendLock, cancellationToken);
                    await TryCloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "输入内容超过大小限制", cancellationToken);
                    return;
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                await HandleClientMessageAsync(payload, session, socket, sendLock, options, logger, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭路径。
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "终端会话 {SessionId} 的接收循环已结束。", session.Id);
        }
        catch (ObjectDisposedException)
        {
            // socket 已释放，无需额外处理。
        }
    }

    private static async Task HandleClientMessageAsync(
        string payload,
        TerminalSession session,
        WebSocket socket,
        SemaphoreSlim sendLock,
        TerminalOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeProperty) ||
                typeProperty.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (typeProperty.GetString())
            {
                case "input":
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                    {
                        var text = data.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            try
                            {
                                session.Pty.Write(Encoding.UTF8.GetBytes(text));
                            }
                            catch (IOException ex)
                            {
                                logger.LogDebug(ex, "终端会话 {SessionId} 写入失败，进程可能已退出。", session.Id);
                            }
                        }
                    }

                    break;

                case "resize":
                    try
                    {
                        session.Pty.Resize(
                            ReadDimension(root, "cols", options.DefaultCols),
                            ReadDimension(root, "rows", options.DefaultRows));
                    }
                    catch (IOException ex)
                    {
                        logger.LogDebug(ex, "终端会话 {SessionId} 调整窗口尺寸失败。", session.Id);
                    }

                    break;

                case "ping":
                    await TrySendAsync(socket, new TerminalPongMessage("pong"), sendLock, cancellationToken);
                    break;
            }
        }
    }

    private static int ReadDimension(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, MinDimension, MaxDimension)
            : Math.Clamp(fallback, MinDimension, MaxDimension);

    private static Task TrySendAsync(WebSocket socket, object message, SemaphoreSlim sendLock, CancellationToken cancellationToken) =>
        SendPayloadAsync(socket, JsonSerializer.SerializeToUtf8Bytes(message, WireJson), sendLock, cancellationToken);

    private static async Task SendPayloadAsync(
        WebSocket socket,
        byte[] payload,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task TryCloseAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(status, description, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 连接已失效，忽略关闭异常。
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail).ExecuteAsync(context);

    /// <summary>
    /// 把可选的 <c>cwd</c> 查询参数归一化为辅助进程使用的工作目录。
    /// 空值返回空串，表示沿用账号家目录；主进程以 root 校验，只证明路径存在且是目录，
    /// 登录用户是否真的可访问仍由降权后的 <c>chdir</c> 决定。
    /// </summary>
    private static bool TryNormalizeWorkingDirectory(string? raw, out string workingDirectory, out string? error)
    {
        workingDirectory = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (raw.Length > 4096)
        {
            error = "工作目录路径过长（最多 4096 个字符）。";
            return false;
        }

        if (raw.Any(char.IsControl))
        {
            error = "工作目录不能包含控制字符。";
            return false;
        }

        if (!Path.IsPathFullyQualified(raw))
        {
            error = "工作目录必须是绝对路径。";
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "工作目录格式不合法。";
            return false;
        }

        if (!Directory.Exists(normalized))
        {
            error = "工作目录不存在或不是目录。";
            return false;
        }

        workingDirectory = normalized;
        return true;
    }

    /// <summary>读取终端配置；仅 root 可读，且不检查 Enabled，便于管理员修复配置。</summary>
    private static IResult GetSettings(HttpContext context, TerminalSettingsService settingsService)
    {
        var error = AuthorizeSettings(context);
        return error ?? Results.Ok(settingsService.Current);
    }

    /// <summary>保存终端配置；保存后立即替换运行时快照，新会话随即生效。</summary>
    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        TerminalOptions? request,
        TerminalSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var error = AuthorizeSettings(context);
        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "请求内容为空", "请提供完整的终端配置。");
        }

        var settings = TerminalSettingsValidator.Normalize(request);
        if (TerminalSettingsValidator.Validate(settings) is { } detail)
        {
            return Problem(StatusCodes.Status400BadRequest, "配置不合法", detail);
        }

        var userName = context.User.Identity?.Name ?? string.Empty;
        return Results.Ok(await settingsService.SaveAsync(settings, userName, cancellationToken));
    }

    /// <summary>
    /// 设置端点仅允许 uid 0 的登录账号。root 判定必须经 <c>getpwnam_r</c> 解析，
    /// 不能按用户名比较。
    /// </summary>
    private static IResult? AuthorizeSettings(HttpContext context)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;
        if (string.IsNullOrEmpty(userName))
        {
            return Problem(StatusCodes.Status401Unauthorized, "未登录", "请先登录后再读取终端设置。");
        }

        if (UserProcessIdentityResolver.TryResolve(userName, configuredShell: null) is not { Uid: 0 })
        {
            return Problem(
                StatusCodes.Status403Forbidden,
                "无权修改终端设置",
                "只有 root（uid 0）账号可以读取或修改终端设置。");
        }

        return null;
    }

    private static IResult Problem(int statusCode, string title, string detail) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail);

    private sealed record TerminalReadyMessage(string Type, string SessionId, string UserName, int Pid, int Cols, int Rows);

    private sealed record TerminalOutputMessage(string Type, string Data);

    private sealed record TerminalExitMessage(string Type, int Code);

    private sealed record TerminalErrorMessage(string Type, string Message);

    private sealed record TerminalPongMessage(string Type);
}
