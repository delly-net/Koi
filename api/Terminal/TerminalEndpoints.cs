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

        return app;
    }

    private static async Task HandleWebSocketAsync(
        HttpContext context,
        TerminalSessionManager sessions,
        IOptions<TerminalOptions> optionsAccessor,
        IOptions<AuthOptions> authOptionsAccessor,
        ILoggerFactory loggerFactory)
    {
        var options = optionsAccessor.Value;
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
                    options.CloseTimeoutSeconds),
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

    private sealed record TerminalReadyMessage(string Type, string SessionId, string UserName, int Pid, int Cols, int Rows);

    private sealed record TerminalOutputMessage(string Type, string Data);

    private sealed record TerminalExitMessage(string Type, int Code);

    private sealed record TerminalErrorMessage(string Type, string Message);

    private sealed record TerminalPongMessage(string Type);
}
