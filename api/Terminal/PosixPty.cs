using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Koi.Api.Terminal;

/// <summary>
/// Linux 伪终端封装：主进程分配 PTY，再以隐藏参数拉起 <see cref="PtyHelper"/> 辅助进程，
/// 由辅助进程降权到登录用户后 <c>execve</c> 登录 shell。
/// </summary>
/// <remarks>
/// 不能在托管进程内 <c>fork</c>：.NET 运行时 fork 后只剩调用线程，子进程执行托管代码属未定义行为，
/// 表现为 SIGSEGV（退出码 139）。因此子进程一律交给 <see cref="Process.Start(ProcessStartInfo)"/>
/// 在原生 fork/exec 中创建；辅助进程通过重定向的标准输出回传就绪字节，主进程收到后才判为启动成功。
/// </remarks>
public sealed class PosixPty : IDisposable
{
    private const int Eintr = 4;
    private const int Sighup = 1;
    private const int Sigkill = 9;
    private const int FSetFd = 2;
    private const int FdCloexec = 1;
    private const int OpenReadWrite = 2;
    private const int SlavePathBufferSize = 256;
    private const int ReadyTimeoutMs = 5000;
    private const int FailureExitWaitMs = 2000;
    private const int ForceKillWaitMs = 1000;
    private const nuint Tiocswinsz = 0x5414;

    private readonly int _masterFd;
    private readonly Process _process;
    private readonly int _closeTimeoutMs;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _exitLock = new();

    private int _disposed;

    private PosixPty(int masterFd, Process process, int closeTimeoutMs)
    {
        _masterFd = masterFd;
        _process = process;
        _closeTimeoutMs = closeTimeoutMs;
    }

    /// <summary>登录 shell 的进程号。</summary>
    public int ProcessId => _process.Id;

    /// <summary>分配 PTY 并启动辅助进程；失败时抛出 <see cref="IOException"/> 或 <see cref="Win32Exception"/>。</summary>
    public static PosixPty Start(
        UserProcessIdentity identity,
        IReadOnlyList<string> arguments,
        int cols,
        int rows,
        int closeTimeoutSeconds)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("PTY 仅在 Linux 平台可用。");
        }

        if (arguments.Count == 0)
        {
            throw new ArgumentException("必须提供 shell 可执行文件路径。", nameof(arguments));
        }

        var masterFd = OpenPseudoTerminal(cols, rows, out var slavePath);
        Process? process = null;
        try
        {
            process = StartHelperProcess(slavePath, identity, arguments);
            if (!WaitForReady(process, ReadyTimeoutMs, out var failureExitCode))
            {
                throw new IOException(DescribeStartFailure(failureExitCode));
            }

            return new PosixPty(masterFd, process, Math.Max(1, closeTimeoutSeconds) * 1000);
        }
        catch
        {
            CloseMaster(masterFd);

            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(ForceKillWaitMs);
                    }
                }
                catch (Exception)
                {
                    // 启动失败路径上的清理异常不再上抛，保留原始错误。
                }

                process.Dispose();
            }

            throw;
        }
    }

    /// <summary>从 PTY 主设备读取输出；返回 0 或负数表示对端已关闭。</summary>
    public unsafe int Read(Span<byte> buffer)
    {
        while (true)
        {
            int readBytes;
            fixed (byte* pointer = buffer)
            {
                readBytes = (int)read(_masterFd, pointer, (nuint)buffer.Length);
            }

            if (readBytes >= 0)
            {
                return readBytes;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == Eintr)
            {
                continue;
            }

            return -1;
        }
    }

    /// <summary>把字节写入 PTY 主设备（串行执行，避免并发写入交错）。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        _writeLock.Wait();
        try
        {
            WriteCore(data);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>下发窗口尺寸，使全屏程序（vim、htop 等）正确重排。</summary>
    public void Resize(int cols, int rows)
    {
        var windowSize = new WindowSize
        {
            Rows = Clamp(rows),
            Cols = Clamp(cols),
        };

        if (ioctl(_masterFd, Tiocswinsz, ref windowSize) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new IOException($"设置终端窗口尺寸失败（errno={errno}）。");
        }
    }

    /// <summary>非阻塞查询子进程退出码；未退出时返回 <c>false</c>。</summary>
    public bool TryGetExitCode(out int exitCode)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            exitCode = -1;
            return true;
        }

        return QueryExitCode(out exitCode);
    }

    /// <summary>等待子进程退出并返回退出码；超时返回 -1。</summary>
    public int WaitForExitCode(int milliseconds) =>
        Volatile.Read(ref _disposed) != 0 ? -1 : WaitExitCode(milliseconds);

    /// <summary>关闭主设备、终止子进程并回收，幂等。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CloseMaster(_masterFd);

        try
        {
            if (!QueryExitCode(out _))
            {
                // setsid() 使辅助进程成为会话与进程组首进程，负 pid 可连同 shell 的子进程一并终止。
                kill(-_process.Id, Sighup);

                if (!_process.WaitForExit(_closeTimeoutMs))
                {
                    kill(-_process.Id, Sigkill);
                    _process.WaitForExit(ForceKillWaitMs);
                }
            }
        }
        catch (Exception)
        {
            // 回收阶段的异常不影响调用方，进程随宿主退出时由系统接管。
        }

        _process.Dispose();

        // 不释放 _writeLock：回收时可能仍有写入方在等待，SemaphoreSlim 未使用 WaitHandle 时无需释放。
    }

    private unsafe void WriteCore(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            int written;
            fixed (byte* pointer = data)
            {
                written = (int)write(_masterFd, pointer + offset, (nuint)(data.Length - offset));
            }

            if (written < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Eintr)
                {
                    continue;
                }

                throw new IOException($"写入终端失败（errno={errno}）。");
            }

            offset += written;
        }
    }

    private bool QueryExitCode(out int exitCode)
    {
        lock (_exitLock)
        {
            try
            {
                if (_process.HasExited)
                {
                    exitCode = _process.ExitCode;
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 进程对象已失效，按已退出处理。
                exitCode = -1;
                return true;
            }

            exitCode = -1;
            return false;
        }
    }

    private int WaitExitCode(int milliseconds)
    {
        lock (_exitLock)
        {
            try
            {
                if (_process.WaitForExit(Math.Max(0, milliseconds)) || _process.HasExited)
                {
                    return _process.ExitCode;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 进程对象已失效，按未知退出码处理。
            }

            return -1;
        }
    }

    /// <summary>分配 PTY 主设备并解锁从设备，返回主设备 fd 与从设备路径。</summary>
    private static int OpenPseudoTerminal(int cols, int rows, out string slavePath)
    {
        slavePath = string.Empty;

        // 等价于 open("/dev/ptmx", O_RDWR | O_NOCTTY)：POSIX 接口，不依赖 glibc 版本。
        var masterFd = posix_openpt(OpenReadWrite);
        if (masterFd < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法分配伪终端（posix_openpt 失败）。");
        }

        try
        {
            // 主设备不能泄漏给辅助进程与 shell：exec 时自动关闭。
            if (fcntl(masterFd, FSetFd, FdCloexec) < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法设置伪终端主设备的关闭即关闭标志。");
            }

            if (grantpt(masterFd) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法初始化伪终端从设备权限（grantpt 失败）。");
            }

            if (unlockpt(masterFd) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法解锁伪终端从设备（unlockpt 失败）。");
            }

            var pathBuffer = Marshal.AllocHGlobal(SlavePathBufferSize);
            try
            {
                if (ptsname_r(masterFd, pathBuffer, (nuint)SlavePathBufferSize) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法解析伪终端从设备路径（ptsname_r 失败）。");
                }

                slavePath = Marshal.PtrToStringUTF8(pathBuffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(pathBuffer);
            }

            if (string.IsNullOrEmpty(slavePath))
            {
                throw new IOException("伪终端从设备路径为空。");
            }

            var windowSize = new WindowSize
            {
                Rows = Clamp(rows),
                Cols = Clamp(cols),
            };

            if (ioctl(masterFd, Tiocswinsz, ref windowSize) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法设置终端窗口尺寸。");
            }

            return masterFd;
        }
        catch
        {
            CloseMaster(masterFd);
            throw;
        }
    }

    /// <summary>以隐藏参数拉起本程序的辅助模式，标准输出被重定向用于回传就绪信号。</summary>
    private static Process StartHelperProcess(
        string slavePath,
        UserProcessIdentity identity,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        // 发布为单文件时可执行文件即本体；dotnet run 时进程是 dotnet，需显式传入程序集路径。
        var processPath = Environment.ProcessPath ?? throw new IOException("无法确定当前可执行文件路径。");
        startInfo.FileName = processPath;
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(assemblyPath))
            {
                throw new IOException("无法确定入口程序集路径。");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";

        startInfo.ArgumentList.Add(PtyHelper.ArgumentName);
        startInfo.ArgumentList.Add(slavePath);
        startInfo.ArgumentList.Add(identity.Uid.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(identity.Gid.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(identity.UserName);
        startInfo.ArgumentList.Add(identity.HomeDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new IOException("无法启动终端辅助进程。");
    }

    /// <summary>等待辅助进程的就绪字节；失败时带回其退出码（拿不到则为 <c>null</c>）。</summary>
    private static bool WaitForReady(Process process, int timeoutMs, out int? failureExitCode)
    {
        failureExitCode = null;

        try
        {
            var buffer = new byte[1];
            var readTask = process.StandardOutput.BaseStream.ReadAsync(buffer, 0, 1);
            if (readTask.Wait(timeoutMs) && readTask.Result == 1 && buffer[0] == PtyHelper.ReadyByte)
            {
                return true;
            }
        }
        catch (Exception)
        {
            // 管道异常同样按启动失败处理。
        }

        try
        {
            if (process.WaitForExit(FailureExitWaitMs) || process.HasExited)
            {
                failureExitCode = process.ExitCode;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 拿不到退出码时按未知原因处理。
        }

        return false;
    }

    private static string DescribeStartFailure(int? exitCode) => exitCode switch
    {
        null => "终端辅助进程未能就绪（未收到就绪信号）。",
        PtyHelper.SetGidFailedExitCode => "终端进程无法切换到登录用户所属的主组（服务权限不足）。",
        PtyHelper.InitGroupsFailedExitCode => "终端进程无法读取登录用户的补充组（服务权限不足）。",
        PtyHelper.SetUidFailedExitCode => "终端进程无法切换为登录用户身份（服务权限不足）。",
        PtyHelper.ExecFailedExitCode => "登录 shell 启动失败，请检查账号的 shell 是否存在且可执行。",
        PtyHelper.SlaveOpenFailedExitCode => "终端进程无法打开伪终端从设备。",
        PtyHelper.SessionFailedExitCode => "终端进程无法创建独立会话（setsid 失败）。",
        PtyHelper.RedirectFailedExitCode => "终端进程无法把标准输入输出重定向到伪终端。",
        _ => $"终端进程启动后立即退出（退出码 {exitCode}）。",
    };

    private static void CloseMaster(int masterFd)
    {
        try
        {
            close(masterFd);
        }
        catch (Exception)
        {
            // 非 Linux 环境不会走到这里，fd 无效时忽略即可。
        }
    }

    private static ushort Clamp(int value) => (ushort)Math.Clamp(value, 1, 1000);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [DllImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", EntryPoint = "grantpt", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", EntryPoint = "ptsname_r", SetLastError = true)]
    private static extern int ptsname_r(int fd, IntPtr buffer, nuint bufferLength);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl(int fd, int command, int argument);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, ref WindowSize windowSize);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern unsafe nint read(int fd, byte* buffer, nuint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern unsafe nint write(int fd, byte* buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
