using System.Globalization;
using System.Runtime.InteropServices;

namespace Koi.Api.Terminal;

/// <summary>
/// 终端辅助进程模式：主进程以隐藏参数拉起本程序，本模式把自身降权到登录用户后
/// <c>execve</c> 登录 shell，进程随即变为该 shell。
/// </summary>
/// <remarks>
/// .NET 运行时在 <c>fork</c> 之后只剩调用线程（GC、线程池等全部消失），子进程再执行托管代码
/// 属于未定义行为，实测表现为 SIGSEGV（退出码 139）。因此 PTY 子进程必须由
/// <see cref="System.Diagnostics.Process"/> 在原生代码中创建，绝不能在托管进程内 <c>fork</c>。
/// 本类只在**未被 fork 的独立进程**中运行，其中所有 P/Invoke 都是常规调用。
/// </remarks>
public static class PtyHelper
{
    /// <summary>隐藏模式标记：作为进程首个参数出现时进入辅助逻辑。</summary>
    public const string ArgumentName = "--koi-pty-helper";

    /// <summary>就绪信号字节：辅助进程写入重定向的标准输出，主进程收到后才下发 ready。</summary>
    public const byte ReadyByte = (byte)'R';

    /// <summary>无法切换到登录用户的主组。</summary>
    public const int SetGidFailedExitCode = 111;

    /// <summary>无法读取登录用户的补充组。</summary>
    public const int InitGroupsFailedExitCode = 112;

    /// <summary>无法切换为登录用户身份。</summary>
    public const int SetUidFailedExitCode = 113;

    /// <summary>无法 exec 登录 shell。</summary>
    public const int ExecFailedExitCode = 127;

    /// <summary>无法打开伪终端从设备。</summary>
    public const int SlaveOpenFailedExitCode = 120;

    /// <summary>无法创建独立会话。</summary>
    public const int SessionFailedExitCode = 121;

    /// <summary>无法把标准输入输出重定向到伪终端。</summary>
    public const int RedirectFailedExitCode = 122;

    private const int OpenReadWrite = 2;
    private const int OpenNoCtty = 0x100;
    private const nuint Tiocsctty = 0x540E;
    private const int Sigpipe = 13;
    private const int SigSetMask = 2;
    private const int ExecuteAccess = 1;
    private const int SigsetSize = 128;
    private const int MinimumArgumentCount = 7;

    /// <summary>
    /// 参数以辅助模式标记开头时在当前进程执行辅助逻辑（正常不会返回）；否则返回 <c>false</c>，
    /// 调用方按普通 Web 服务启动。
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], ArgumentName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!OperatingSystem.IsLinux())
        {
            Environment.Exit(ExecFailedExitCode);
        }

        Run(args);
        return true;
    }

    private static void Run(string[] args)
    {
        uint uid = 0;
        uint gid = 0;
        if (args.Length < MinimumArgumentCount ||
            !uint.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out uid) ||
            !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out gid))
        {
            _exit(ExecFailedExitCode);
        }

        var slavePath = args[1];
        var userName = args[4];
        var homeDirectory = args[5];
        var shell = args[6];
        var shellArguments = args.Length > MinimumArgumentCount ? args[MinimumArgumentCount..] : [];

        var slaveFd = open(slavePath, OpenReadWrite | OpenNoCtty);
        if (slaveFd < 0)
        {
            _exit(SlaveOpenFailedExitCode);
        }

        // 成为会话首进程并接管从设备为控制终端；作业控制（Ctrl+C、前后台切换）依赖于此。
        if (setsid() < 0)
        {
            close(slaveFd);
            _exit(SessionFailedExitCode);
        }

        ioctl(slaveFd, Tiocsctty, IntPtr.Zero);

        // 降权顺序固定：initgroups 必须在 setuid 前（降权后读不到补充组），setgid 先于 setuid。
        if (initgroups(userName, gid) != 0)
        {
            _exit(InitGroupsFailedExitCode);
        }

        if (setgid(gid) != 0)
        {
            _exit(SetGidFailedExitCode);
        }

        if (setuid(uid) != 0)
        {
            _exit(SetUidFailedExitCode);
        }

        // 家目录不可用时保持当前目录，不阻断 shell 启动。
        chdir(homeDirectory);

        // 按登录用户权限判断 shell 是否可执行，尽早给出可读的启动失败原因。
        if (access(shell, ExecuteAccess) != 0)
        {
            _exit(ExecFailedExitCode);
        }

        // 通知主进程已就绪：此刻 fd 1 仍是主进程的重定向管道，下一行起被替换为 PTY 从设备。
        WriteReadySignal();

        if (dup2(slaveFd, 0) < 0 || dup2(slaveFd, 1) < 0 || dup2(slaveFd, 2) < 0)
        {
            _exit(RedirectFailedExitCode);
        }

        if (slaveFd > 2)
        {
            close(slaveFd);
        }

        // exec 会保留「被忽略」的信号处置与信号掩码，恢复默认后再启动 shell。
        ResetProcessSignals();

        using var argumentTable = new UnmanagedStringTable([shell, .. shellArguments]);
        using var environmentTable = new UnmanagedStringTable(BuildEnvironment(userName, homeDirectory, shell));

        execve(argumentTable.First, argumentTable.Pointers, environmentTable.Pointers);

        _exit(ExecFailedExitCode);
    }

    /// <summary>向重定向的 stdout 写入一个就绪字节（此时 fd 1 尚未被 PTY 替换）。</summary>
    private static unsafe void WriteReadySignal()
    {
        var buffer = stackalloc byte[1];
        buffer[0] = ReadyByte;
        write(1, buffer, 1);
    }

    /// <summary>恢复 exec 后会被继承的进程级信号状态。</summary>
    private static void ResetProcessSignals()
    {
        // .NET 运行时把 SIGPIPE 置为忽略，而「被忽略」的处置会跨 exec 继承，需恢复默认。
        signal(Sigpipe, IntPtr.Zero);

        var signalSet = Marshal.AllocHGlobal(SigsetSize);
        try
        {
            sigemptyset(signalSet);
            sigprocmask(SigSetMask, signalSet, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(signalSet);
        }
    }

    private static string[] BuildEnvironment(string userName, string homeDirectory, string shell) =>
    [
        $"HOME={homeDirectory}",
        $"USER={userName}",
        $"LOGNAME={userName}",
        $"SHELL={shell}",
        "TERM=xterm-256color",
        "PATH=/usr/local/bin:/usr/bin:/bin:/usr/local/sbin:/usr/sbin:/sbin",
        "LANG=C.UTF-8",
    ];

    /// <summary>把字符串数组一次性写入非托管内存（含 NULL 结尾），供 execve 使用。</summary>
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

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, IntPtr argument);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int dup2(int oldFd, int newFd);

    [DllImport("libc", EntryPoint = "initgroups", SetLastError = true)]
    private static extern int initgroups(string userName, uint gid);

    [DllImport("libc", EntryPoint = "setgid", SetLastError = true)]
    private static extern int setgid(uint gid);

    [DllImport("libc", EntryPoint = "setuid", SetLastError = true)]
    private static extern int setuid(uint uid);

    [DllImport("libc", EntryPoint = "chdir", SetLastError = true)]
    private static extern int chdir(string path);

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int access(string path, int mode);

    [DllImport("libc", EntryPoint = "signal", SetLastError = true)]
    private static extern IntPtr signal(int signalNumber, IntPtr handler);

    [DllImport("libc", EntryPoint = "sigemptyset", SetLastError = true)]
    private static extern int sigemptyset(IntPtr signalSet);

    [DllImport("libc", EntryPoint = "sigprocmask", SetLastError = true)]
    private static extern int sigprocmask(int how, IntPtr signalSet, IntPtr previousSet);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern unsafe nint write(int fd, byte* buffer, nuint count);

    [DllImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static extern int execve(IntPtr path, IntPtr argv, IntPtr envp);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void _exit(int status);
}
