using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Koi.Api.Files;

/// <summary>复制/移动的执行阶段。</summary>
public static class FileTransferPhases
{
    /// <summary>正在统计源目录的条目数与字节数（总进度未知）。</summary>
    public const string Scanning = "scanning";

    /// <summary>正在复制或移动。</summary>
    public const string Transferring = "transferring";

    /// <summary>已完成。</summary>
    public const string Done = "done";

    /// <summary>已被用户取消。</summary>
    public const string Cancelled = "cancelled";

    /// <summary>执行失败。</summary>
    public const string Failed = "failed";
}

/// <summary>
/// 辅助进程 → 主进程的进度帧负载。复制/移动期间辅助进程在最终响应帧之前写入若干进度帧，
/// 主进程在读取响应帧的循环中消费它们并更新内存快照。
/// </summary>
public sealed record FileTransferProgressPayload(
    string Phase,
    long BytesDone,
    long BytesTotal,
    int EntriesDone,
    int EntriesTotal,
    string? CurrentName);

/// <summary>进度查询接口的响应；仅允许发起操作的同一登录账号读取。</summary>
public sealed record FileTransferProgressResponse(
    string OperationId,
    string Mode,
    string Phase,
    long BytesDone,
    long BytesTotal,
    int EntriesDone,
    int EntriesTotal,
    string? CurrentName,
    bool Cancelled,
    bool Completed,
    bool Failed,
    string? Message);

/// <summary>取消接口的响应：<c>Cancelled=false</c> 表示操作已经结束，取消未生效。</summary>
public sealed record FileTransferCancelResponse(bool Ok, bool Cancelled, bool Completed);

/// <summary>
/// 辅助进程内的进度帧写入器：控制节奏（200ms 节流，阶段切换强制写），
/// 并维护已完成字节/条目与当前文件名。复制分块很密集，节流避免每块一帧撑满管道。
/// 写入失败（主进程终止管道）时标记关闭并抛出，由辅助进程外层异常处理收口。
/// </summary>
public sealed class HelperTransferProgressWriter
{
    private static readonly long ThrottleTicks = Stopwatch.Frequency / 5;

    private readonly Stream _output;
    private string _phase = FileTransferPhases.Scanning;
    private long _bytesDone;
    private long _bytesTotal;
    private int _entriesDone;
    private int _entriesTotal;
    private string? _currentName;
    private long _lastWriteTicks;
    private bool _closed;

    public HelperTransferProgressWriter(Stream output)
    {
        _output = output;
    }

    /// <summary>进入统计阶段；总数为未知，前端显示不确定进度。</summary>
    public void BeginScanning() => Write(force: true);

    /// <summary>统计阶段已扫描的条目数与字节数（仅统计阶段有效）。</summary>
    public void Scanned(int entries, long bytes)
    {
        if (_phase != FileTransferPhases.Scanning)
        {
            return;
        }

        _entriesDone = entries;
        _bytesDone = bytes;
        Write(force: false);
    }

    /// <summary>统计完成，进入复制/移动阶段并重置已完成计数。</summary>
    public void StartTransfer(long bytesTotal, int entriesTotal)
    {
        _phase = FileTransferPhases.Transferring;
        _bytesDone = 0;
        _entriesDone = 0;
        _bytesTotal = bytesTotal;
        _entriesTotal = entriesTotal;
        _currentName = null;
        Write(force: true);
    }

    /// <summary>累计已写入的字节数（分块复制时调用）。</summary>
    public void AddBytes(long delta)
    {
        if (delta <= 0)
        {
            return;
        }

        _bytesDone += delta;
        Write(force: false);
    }

    /// <summary>完成一个条目（文件或符号链接），并记录当前文件名。</summary>
    public void AddEntry(string name, long bytesDelta)
    {
        _bytesDone += bytesDelta > 0 ? bytesDelta : 0;
        _entriesDone += 1;
        _currentName = name;
        Write(force: false);
    }

    /// <summary>同设备移动的快路径：该源整棵子树瞬间完成，直接计入完成量。</summary>
    public void AddCompleted(long bytesDelta, int entriesDelta)
    {
        _bytesDone += bytesDelta;
        _entriesDone += entriesDelta;
        Write(force: false);
    }

    /// <summary>全部完成；完成量对齐总数，避免统计与复制之间的微小偏差。</summary>
    public void Done()
    {
        _phase = FileTransferPhases.Done;
        _bytesDone = _bytesTotal;
        _entriesDone = _entriesTotal;
        _currentName = null;
        Write(force: true);
    }

    private void Write(bool force)
    {
        if (_closed)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!force && now - _lastWriteTicks < ThrottleTicks)
        {
            return;
        }

        _lastWriteTicks = now;
        var frame = new FileHelperResponse
        {
            Ok = true,
            Progress = new FileTransferProgressPayload(
                _phase,
                _bytesDone,
                _bytesTotal,
                _entriesDone,
                _entriesTotal,
                _currentName),
        };

        try
        {
            FileProtocol.WriteResponseHeader(_output, frame);
        }
        catch (IOException)
        {
            // 主进程已终止管道（取消或超时），后续进度不再写入。
            _closed = true;
            throw;
        }
    }
}

/// <summary>
/// 主进程侧的一次复制/移动操作：保存进度快照、所属账号与取消能力。
/// 取消通过终止辅助进程实现（尽力而为），已复制的条目不会回滚。
/// </summary>
public sealed class FileTransferOperation
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Action? _killHelper;
    private string _phase = FileTransferPhases.Transferring;
    private long _bytesDone;
    private long _bytesTotal;
    private int _entriesDone;
    private int _entriesTotal;
    private string? _currentName;
    private string? _message;
    private bool _cancelled;
    private bool _completed;

    public FileTransferOperation(string id, string userName, string mode)
    {
        Id = id;
        UserName = userName;
        Mode = mode;
    }

    public string Id { get; }

    public string UserName { get; }

    public string Mode { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>最近一次上报进度/取消的时间，用于清理失去联系的操作。</summary>
    public DateTimeOffset LastActivityAt { get; private set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CompletedAt { get; private set; }

    public bool CancellationRequested
    {
        get
        {
            lock (_gate)
            {
                return _cancelled;
            }
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    /// <summary>登记终止辅助进程的回调；若在登记前已请求取消，立即执行。</summary>
    public void AttachHelper(Action killHelper)
    {
        var killNow = false;
        lock (_gate)
        {
            _killHelper = killHelper;
            killNow = _cancelled && !_completed;
        }

        if (killNow)
        {
            SafeKill(killHelper);
        }
    }

    /// <summary>消费辅助进程上报的进度帧。</summary>
    public void Report(FileTransferProgressPayload payload)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _phase = payload.Phase;
            _bytesDone = payload.BytesDone;
            _bytesTotal = payload.BytesTotal;
            _entriesDone = payload.EntriesDone;
            _entriesTotal = payload.EntriesTotal;
            _currentName = payload.CurrentName;
            LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// 请求取消。已完成的操作返回 <c>false</c>；否则设置标记、取消等待令牌并终止辅助进程。
    /// </summary>
    public bool Cancel()
    {
        Action? kill;
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            _cancelled = true;
            LastActivityAt = DateTimeOffset.UtcNow;
            kill = _killHelper;
        }

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作已结束，忽略。
        }

        if (kill is not null)
        {
            SafeKill(kill);
        }

        return true;
    }

    /// <summary>按最终响应收口阶段；成功优先于取消（复制恰在取消前完成时仍算成功）。</summary>
    public void Complete(FileHelperResponse response)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            CompletedAt = DateTimeOffset.UtcNow;

            if (response.Ok)
            {
                _phase = FileTransferPhases.Done;
                _bytesDone = _bytesTotal;
                _entriesDone = _entriesTotal;
                _currentName = null;
                _message = null;
            }
            else if (_cancelled)
            {
                _phase = FileTransferPhases.Cancelled;
                _message = "粘贴已取消。";
            }
            else
            {
                _phase = FileTransferPhases.Failed;
                _message = response.Message;
            }
        }
    }

    public FileTransferProgressResponse Snapshot()
    {
        lock (_gate)
        {
            return new FileTransferProgressResponse(
                Id,
                Mode,
                _phase,
                _bytesDone,
                _bytesTotal,
                _entriesDone,
                _entriesTotal,
                _currentName,
                _cancelled,
                _completed,
                string.Equals(_phase, FileTransferPhases.Failed, StringComparison.Ordinal),
                _message);
        }
    }

    private static void SafeKill(Action kill)
    {
        try
        {
            kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // 进程已退出或无权终止，忽略。
        }
    }
}

/// <summary>
/// 复制/移动进度登记表（内存态）。operationId 由客户端生成，用于在同步 POST 未返回时
/// 并行轮询进度；只能由发起操作的登录账号查询或取消。完成的条目保留两分钟供最后一次轮询，
/// 之后惰性清理，避免内存增长。
/// </summary>
public sealed class FileTransferProgressRegistry
{
    private const int MaxOperations = 256;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(2);

    /// <summary>未正常收口且长时间没有进度的操作会被清理，防止登记表被永久占满。</summary>
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(2);
    private static readonly Regex OperationIdPattern = new("^[A-Za-z0-9-]{8,64}$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, FileTransferOperation> _operations =
        new(StringComparer.Ordinal);

    public static bool IsValidOperationId(string? value) =>
        value is not null && OperationIdPattern.IsMatch(value);

    /// <summary>登记新操作；标识重复或登记表过满时返回 <c>false</c>。</summary>
    public bool TryBegin(string id, string userName, string mode, out FileTransferOperation operation)
    {
        Prune();
        operation = new FileTransferOperation(id, userName, mode);
        if (_operations.Count >= MaxOperations)
        {
            operation = null!;
            return false;
        }

        if (_operations.TryAdd(id, operation))
        {
            return true;
        }

        operation = null!;
        return false;
    }

    /// <summary>按标识与登录账号取操作；不存在或不属于该账号时返回 <c>false</c>（对外 404）。</summary>
    public bool TryGet(string id, string userName, out FileTransferOperation operation)
    {
        Prune();
        if (_operations.TryGetValue(id, out var found) &&
            string.Equals(found.UserName, userName, StringComparison.Ordinal))
        {
            operation = found;
            return true;
        }

        operation = null!;
        return false;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _operations)
        {
            var finished = pair.Value.IsCompleted &&
                           now - pair.Value.CompletedAt > Retention;
            var abandoned = !pair.Value.IsCompleted &&
                            now - pair.Value.LastActivityAt > IdleLifetime;
            if (finished || abandoned)
            {
                _operations.TryRemove(pair.Key, out _);
            }
        }

        if (_operations.Count < MaxOperations)
        {
            return;
        }

        // 过满时优先移除最早完成的条目；仍在运行的操作不主动丢弃。
        var completed = _operations.Values
            .Where(item => item.IsCompleted)
            .OrderBy(item => item.CompletedAt)
            .Take(Math.Max(1, _operations.Count - MaxOperations + 1));

        foreach (var item in completed)
        {
            _operations.TryRemove(item.Id, out _);
        }
    }
}
