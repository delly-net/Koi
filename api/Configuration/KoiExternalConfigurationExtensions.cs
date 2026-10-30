using System.Text;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Koi.Api.Configuration;

/// <summary>
/// Linux 外部配置：从 <c>/etc/koi/appsettings.json</c> 读取覆盖层配置，文件缺失时自动创建默认版本。
/// 该文件只覆盖显式写入的键，未写的键继续沿用随程序发布的 <c>appsettings.json</c> 默认值。
/// </summary>
public static class KoiExternalConfigurationExtensions
{
    /// <summary>Linux 系统级配置目录。</summary>
    public const string ConfigDirectory = "/etc/koi";

    /// <summary>外部配置文件名。</summary>
    public const string ConfigFileName = "appsettings.json";

    /// <summary>新建配置目录权限 0755。</summary>
    private const UnixFileMode DefaultDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>新建配置文件权限 0644。</summary>
    private const UnixFileMode DefaultFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// 启用外部配置。非 Linux 平台直接跳过，开发机行为保持不变。
    /// 返回实际加载的配置文件路径；未启用或创建失败时返回 <c>null</c>。
    /// </summary>
    public static string? AddKoiExternalConfiguration(this WebApplicationBuilder builder)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var configPath = Path.Combine(ConfigDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            TryCreateDefaultConfigFile(builder.Environment.ContentRootPath, configPath);
            if (!File.Exists(configPath))
            {
                return null;
            }
        }

        // 用独立配置源加载 /etc 文件，并插入到最后一个 JSON 源之后：
        // 覆盖 appsettings.json / appsettings.{Environment}.json 等文件配置，
        // 但环境变量与命令行参数仍然优先。
        var source = new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(ConfigDirectory),
            Path = ConfigFileName,
            Optional = true,
            ReloadOnChange = false,
        };

        var sources = builder.Configuration.Sources;
        var insertAt = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                insertAt = i + 1;
            }
        }

        sources.Insert(insertAt, source);
        return configPath;
    }

    /// <summary>
    /// 用随程序发布的 <c>appsettings.json</c> 创建默认外部配置。
    /// 任何失败（例如非 root 无法写 <c>/etc</c>）只输出告警，不阻断启动。
    /// </summary>
    private static void TryCreateDefaultConfigFile(string contentRootPath, string configPath)
    {
        var tempPath = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(configPath)!;
            var directoryCreated = !Directory.Exists(directory);
            Directory.CreateDirectory(directory);
            if (directoryCreated)
            {
                SetPermissions(directory, DefaultDirectoryMode);
            }

            // 默认内容优先取随程序发布的 appsettings.json，保证默认键与当前版本一致。
            // 内容根可被配置覆盖，缺失时回退到可执行文件所在目录。
            var bundledPath = Path.Combine(contentRootPath, ConfigFileName);
            if (!File.Exists(bundledPath))
            {
                bundledPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            }

            var content = File.Exists(bundledPath) ? File.ReadAllText(bundledPath) : null;
            if (content is null)
            {
                content = "{}\n";
                Console.WriteLine(
                    $"[Koi] 未找到随程序发布的 {ConfigFileName}，将写入空配置 {configPath}。");
            }

            // 同目录临时文件 + 原子改名，避免并发启动或中断留下半截 JSON。
            tempPath = Path.Combine(directory, $".{ConfigFileName}.{Environment.ProcessId}.tmp");
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetPermissions(tempPath, DefaultFileMode);

            try
            {
                File.Move(tempPath, configPath, overwrite: false);
                Console.WriteLine($"[Koi] 未找到外部配置文件，已创建默认配置：{configPath}");
            }
            catch (IOException) when (File.Exists(configPath))
            {
                // 另一个实例已抢先创建，直接复用现有文件。
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine(
                $"[Koi] 无法创建外部配置文件 {configPath}：{ex.Message}。将继续使用随程序发布的默认配置。");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>仅在 Linux 上尽力设置 Unix 权限，失败只告警，不影响文件创建。</summary>
    private static void SetPermissions(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"[Koi] 无法设置 {path} 的权限：{ex.Message}");
        }
    }

    /// <summary>尽力清理临时文件，失败不影响启动。</summary>
    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件残留无害，忽略即可。
        }
    }
}
