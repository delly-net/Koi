namespace Koi.Api.Terminal;

/// <summary>一次在线终端会话：一个 PTY、一个子进程、一个 WebSocket 连接。</summary>
public sealed class TerminalSession : IDisposable
{
    private int _disposed;

    public TerminalSession(PosixPty pty, string userName, string remoteAddress, int remotePort)
    {
        Pty = pty;
        UserName = userName;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        Id = Guid.NewGuid().ToString("N");
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    public string UserName { get; }

    public string RemoteAddress { get; }

    public int RemotePort { get; }

    public DateTimeOffset CreatedAt { get; }

    public PosixPty Pty { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Pty.Dispose();
    }
}
