using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Koi.Api.Terminal;

/// <summary>
/// Linux 伪终端封装：用 <c>forkpty</c> 分配 PTY 并启动子进程，
/// 在子进程中把身份降权到登录用户，再 <c>execve</c> 登录 shell。
/// </summary>
/// <remarks>
/// 子进程分支只调用异步信号安全的 libc 函数，所有字符串与指针数组都在 fork 前
/// 预分配到非托管内存，避免 fork 后触碰托管堆导致死锁。
/// </remarks>
public sealed class PosixPty : IDisposable
{
    private const int Eintr = 4;
    private const int Wnohang = 1;
    private const int Sighup = 1;
    private const int Sigkill = 9;
    private const nuint Tiocswinsz = 0x5414;

    /// <summary>子进程降权失败的退出码，父进程据此给出可读错误。</summary>
    public const int SetGidFailedExitCode = 111;
    public const int InitGroupsFailedExitCode = 112;
    public const int SetUidFailedExitCode = 113;
    public const int ExecFailedExitCode = 127;

    private static readonly Lazy<ForkPtyDelegate> ForkPty = new(LoadForkPty, isThreadSafe: true);

    private readonly int _masterFd;
    private readonly int _pid;
    private readonly int _closeTimeoutMs;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _exitLock = new();

    private bool _exited;
    private int _exitCode = -1;
    private int _disposed;

    private PosixPty(int masterFd, int pid, int closeTimeoutMs)
    {
        _masterFd = masterFd;
        _pid = pid;
        _closeTimeoutMs = closeTimeoutMs;
    }

    /// <summary>子进程（登录 shell）的进程号。</summary>
    public int ProcessId => _pid;

    /// <summary>启动 PTY 与子进程。失败时抛出 <see cref="Win32Exception"/> 或 <see cref="IOException"/>。</summary>
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

        var stringTable = new UnmanagedStringTable(arguments);
        var environmentTable = new UnmanagedStringTable(BuildEnvironment(identity));
        var userNamePtr = Marshal.StringToCoTaskMemUTF8(identity.UserName);
        var homePtr = Marshal.StringToCoTaskMemUTF8(identity.HomeDirectory);

        var windowSize = new WindowSize
        {
            Rows = Clamp(rows),
            Cols = Clamp(cols),
        };

        int masterFd;
        int pid;
        try
        {
            pid = ForkPty.Value(out masterFd, IntPtr.Zero, IntPtr.Zero, ref windowSize);
        }
        catch
        {
            stringTable.Dispose();
            environmentTable.Dispose();
            Marshal.FreeCoTaskMem(userNamePtr);
            Marshal.FreeCoTaskMem(homePtr);
            throw;
        }

        if (pid == 0)
        {
            // 子进程：以下调用绝不返回（无论如何都会 _exit）。
            RunChild(identity, userNamePtr, homePtr, stringTable.First, stringTable.Pointers, environmentTable.Pointers);
        }

        // forkpty 通过函数指针调用，不走 SetLastError 捕获，需直接读取 errno。
        var forkError = pid < 0 ? GetErrno() : 0;

        // 父进程释放 fork 前的临时分配；子进程使用自己地址空间中的副本。
        stringTable.Dispose();
        environmentTable.Dispose();
        Marshal.FreeCoTaskMem(userNamePtr);
        Marshal.FreeCoTaskMem(homePtr);

        if (pid < 0 || masterFd < 0)
        {
            throw new Win32Exception(forkError, "forkpty 调用失败，无法分配伪终端。");
        }

        return new PosixPty(masterFd, pid, Math.Max(1, closeTimeoutSeconds));
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

    /// <summary>非阻塞回收子进程；未退出时返回 <c>false</c>。</summary>
    public bool TryGetExitCode(out int exitCode) => TryReap(blocking: false, out exitCode);

    /// <summary>关闭主设备、终止子进程并回收，幂等。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            close(_masterFd);
        }
        catch (DllNotFoundException)
        {
            // 非 Linux 环境下不会创建实例，这里仅做兜底。
        }

        try
        {
            if (!TryGetExitCode(out _))
            {
                kill(-_pid, Sighup);

                var deadline = Environment.TickCount64 + _closeTimeoutMs;
                while (Environment.TickCount64 < deadline && !TryGetExitCode(out _))
                {
                    Thread.Sleep(50);
                }

                if (!TryGetExitCode(out _))
                {
                    kill(-_pid, Sigkill);
                    TryReap(blocking: true, out _);
                }
            }
        }
        catch (Exception)
        {
            // 回收阶段的异常不影响调用方，进程随宿主退出时由系统接管。
        }

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

    private bool TryReap(bool blocking, out int exitCode)
    {
        lock (_exitLock)
        {
            if (_exited)
            {
                exitCode = _exitCode;
                return true;
            }

            var result = waitpid(_pid, out var status, blocking ? 0 : Wnohang);
            if (result == _pid)
            {
                _exited = true;
                _exitCode = DecodeStatus(status);
                exitCode = _exitCode;
                return true;
            }

            if (result < 0)
            {
                // ECHILD：已被回收或无权回收，按已退出处理，避免重复等待。
                _exited = true;
                _exitCode = -1;
                exitCode = -1;
                return true;
            }

            exitCode = -1;
            return false;
        }
    }

    private static int DecodeStatus(int status)
    {
        if ((status & 0x7f) == 0)
        {
            return (status >> 8) & 0xff;
        }

        return 128 + (status & 0x7f);
    }

    private static void RunChild(
        UserProcessIdentity identity,
        IntPtr userNamePtr,
        IntPtr homePtr,
        IntPtr shellPtr,
        IntPtr argv,
        IntPtr envp)
    {
        try
        {
            // initgroups 必须在 setuid 之前：降权后就无法读取账号的补充组。
            if (setgid(identity.Gid) != 0)
            {
                _exit(SetGidFailedExitCode);
            }

            if (initgroups(userNamePtr, identity.Gid) != 0)
            {
                _exit(InitGroupsFailedExitCode);
            }

            if (setuid(identity.Uid) != 0)
            {
                _exit(SetUidFailedExitCode);
            }

            // 家目录不可用时保持服务进程的当前目录，不阻断 shell 启动。
            chdir(homePtr);

            execve(shellPtr, argv, envp);
        }
        catch (Exception)
        {
            // fork 后的子进程不记录日志、不再触碰托管状态。
        }

        _exit(ExecFailedExitCode);
    }

    private static string[] BuildEnvironment(UserProcessIdentity identity) =>
    [
        $"HOME={identity.HomeDirectory}",
        $"USER={identity.UserName}",
        $"LOGNAME={identity.UserName}",
        $"SHELL={identity.Shell}",
        "TERM=xterm-256color",
        "PATH=/usr/local/bin:/usr/bin:/bin:/usr/local/sbin:/usr/sbin:/sbin",
        "LANG=C.UTF-8",
    ];

    private static ushort Clamp(int value) => (ushort)Math.Clamp(value, 1, 1000);

    private static unsafe int GetErrno()
    {
        var location = errnoLocation();
        return location == IntPtr.Zero ? 0 : *(int*)location;
    }

    private static ForkPtyDelegate LoadForkPty()
    {
        // glibc >= 2.34 的 forkpty 在 libc 中，旧发行版在 libutil.so.1 中；
        // musl 发行版（Alpine 等）的库名不同，一并尝试。
        foreach (var library in new[] { "libc.so.6", "libutil.so.1", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1" })
        {
            if (NativeLibrary.TryLoad(library, out var handle) &&
                NativeLibrary.TryGetExport(handle, "forkpty", out var export))
            {
                return Marshal.GetDelegateForFunctionPointer<ForkPtyDelegate>(export);
            }
        }

        throw new DllNotFoundException("当前系统未提供 forkpty（libc.so.6 / libutil.so.1 均未找到）。");
    }

    /// <summary>fork 前把字符串数组一次性写入非托管内存（含 NULL 结尾）。</summary>
    private sealed class UnmanagedStringTable : IDisposable
    {
        private readonly IntPtr[] _strings;

        public UnmanagedStringTable(IReadOnlyList<string> values)
        {
            _strings = new IntPtr[values.Count];
            Pointers = Marshal.AllocHGlobal(IntPtr.Size * (values.Count + 1));

            for (var i = 0; i < values.Count; i++)
            {
                _strings[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
                Marshal.WriteIntPtr(Pointers, i * IntPtr.Size, _strings[i]);
            }

            Marshal.WriteIntPtr(Pointers, values.Count * IntPtr.Size, IntPtr.Zero);
        }

        /// <summary>非托管指针数组的首地址。</summary>
        public IntPtr Pointers { get; }

        /// <summary>第一个元素的地址（即可执行文件路径）。</summary>
        public IntPtr First => _strings.Length > 0 ? _strings[0] : IntPtr.Zero;

        public void Dispose()
        {
            foreach (var pointer in _strings)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }

            Marshal.FreeHGlobal(Pointers);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ForkPtyDelegate(out int masterFd, IntPtr name, IntPtr termios, ref WindowSize windowSize);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern unsafe nint read(int fd, byte* buffer, nuint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern unsafe nint write(int fd, byte* buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, ref WindowSize windowSize);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", EntryPoint = "setgid", SetLastError = true)]
    private static extern int setgid(uint gid);

    [DllImport("libc", EntryPoint = "setuid", SetLastError = true)]
    private static extern int setuid(uint uid);

    [DllImport("libc", EntryPoint = "initgroups", SetLastError = true)]
    private static extern int initgroups(IntPtr userName, uint gid);

    [DllImport("libc", EntryPoint = "chdir", SetLastError = true)]
    private static extern int chdir(IntPtr path);

    [DllImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static extern int execve(IntPtr path, IntPtr argv, IntPtr envp);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void _exit(int status);

    [DllImport("libc", EntryPoint = "__errno_location")]
    private static extern IntPtr errnoLocation();
}
