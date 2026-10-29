using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Koi.Api.Terminal;

/// <summary>
/// 在线终端会话登记表（单例）。负责并发上限、按用户回收与宿主关闭时的统一清理。
/// 终端会话为内存态：服务重启后全部消失，符合「断开即销毁」策略。
/// </summary>
public sealed class TerminalSessionManager(
    IOptions<TerminalOptions> options,
    ILogger<TerminalSessionManager> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _perUserCounts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TerminalOptions _options = options.Value;

    private int _total;
    private int _disposed;

    /// <summary>
    /// 在并发上限内创建并登记会话。工厂抛出的异常会被捕获，
    /// 以可读错误信息返回给调用方（例如降权失败或 forkpty 不可用）。
    /// </summary>
    public TerminalSession? TryCreate(string userName, Func<TerminalSession> factory, out string? error)
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                error = "服务正在关闭，无法创建新的终端会话。";
                return null;
            }

            if (_total >= _options.MaxSessionsTotal)
            {
                error = $"终端会话总数已达上限（{_options.MaxSessionsTotal}），请稍后再试。";
                return null;
            }

            _perUserCounts.TryGetValue(userName, out var userCount);
            if (userCount >= _options.MaxSessionsPerUser)
            {
                error = $"每个账号最多同时打开 {_options.MaxSessionsPerUser} 个终端。";
                return null;
            }

            _perUserCounts[userName] = userCount + 1;
            _total++;
        }

        try
        {
            var session = factory();
            _sessions[session.Id] = session;
            error = null;
            logger.LogInformation(
                "创建终端会话 {SessionId}：用户 {UserName}，来源 {RemoteAddress}:{RemotePort}，PID {Pid}。",
                session.Id,
                session.UserName,
                session.RemoteAddress,
                session.RemotePort,
                session.Pty.ProcessId);
            return session;
        }
        catch (Exception ex)
        {
            ReleaseSlot(userName);
            logger.LogWarning(
                ex,
                "创建终端会话失败：用户 {UserName}，原因 {Reason}。",
                userName,
                ex.Message);
            error = ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
                ? "当前系统不支持 PTY，无法启动终端。"
                : $"启动终端失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>注销并回收会话；对同一 id 重复调用安全。</summary>
    public void Remove(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        ReleaseSlot(session.UserName);
        session.Dispose();
        logger.LogInformation("已回收终端会话 {SessionId}（用户 {UserName}）。", session.Id, session.UserName);
    }

    /// <summary>断开指定用户的全部终端（退出登录时调用）。</summary>
    public int CloseAllForUser(string userName)
    {
        var ids = _sessions.Values
            .Where(session => string.Equals(session.UserName, userName, StringComparison.Ordinal))
            .Select(session => session.Id)
            .ToArray();

        foreach (var id in ids)
        {
            Remove(id);
        }

        if (ids.Length > 0)
        {
            logger.LogInformation("已关闭用户 {UserName} 的 {Count} 个终端会话。", userName, ids.Length);
        }

        return ids.Length;
    }

    /// <summary>宿主关闭时回收全部会话，避免遗留子进程。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var id in _sessions.Keys.ToArray())
        {
            Remove(id);
        }
    }

    private void ReleaseSlot(string userName)
    {
        lock (_gate)
        {
            _total = Math.Max(0, _total - 1);
            if (_perUserCounts.TryGetValue(userName, out var count))
            {
                if (count <= 1)
                {
                    _perUserCounts.Remove(userName);
                }
                else
                {
                    _perUserCounts[userName] = count - 1;
                }
            }
        }
    }
}
