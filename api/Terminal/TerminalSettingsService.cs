using System.Text.Json;
using Koi.Api.Configuration;

namespace Koi.Api.Terminal;

/// <summary>
/// 终端配置的运行时快照。
/// </summary>
/// <remarks>
/// 配置不再来自 <c>appsettings.json</c>：启动时从 SQLite 通用配置表（Feature = <c>terminal</c>）
/// 读取；数据库中没有记录时，把现有 <c>Koi:Terminal</c> 配置（含 <c>/etc/koi/appsettings.json</c>
/// 覆盖值）一次性迁移进数据库，此后只以数据库为准。界面保存后立即整体替换快照并触发
/// <see cref="Changed"/>，新会话与新消息随即使用新值，无需重启服务。
/// 配置缺失或损坏时回退到 <see cref="TerminalOptions"/> 的内置默认值。
/// </remarks>
public sealed class TerminalSettingsService
{
    /// <summary>通用配置表中的功能标识。</summary>
    public const string FeatureName = "terminal";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FeatureConfigStore configs;
    private readonly IConfiguration configuration;
    private readonly ILogger<TerminalSettingsService> logger;
    private TerminalOptions current = new();

    public TerminalSettingsService(
        FeatureConfigStore configs,
        IConfiguration configuration,
        ILogger<TerminalSettingsService> logger)
    {
        this.configs = configs;
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>当前生效的配置快照；整体替换发布，不原地修改。</summary>
    public TerminalOptions Current => Volatile.Read(ref current);

    /// <summary>配置被保存或被启动加载替换后触发。</summary>
    public event Action? Changed;

    /// <summary>
    /// 从数据库加载配置；数据库中还没有记录时，用旧配置节或内置默认值完成一次性迁移。
    /// 读取失败不阻断启动。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        string? json;
        try
        {
            json = await configs.GetJsonAsync(FeatureName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读取终端配置失败，使用内置默认值。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<TerminalOptions>(json, JsonOptions);
                if (loaded is not null)
                {
                    var normalized = TerminalSettingsValidator.Normalize(loaded);
                    if (TerminalSettingsValidator.Validate(normalized) is null)
                    {
                        Volatile.Write(ref current, normalized);
                        Changed?.Invoke();
                        return;
                    }

                    logger.LogWarning("数据库中的终端配置不合法，改用旧配置节或内置默认值。");
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "终端配置内容无法解析，改用旧配置节或内置默认值。");
            }
        }

        await SeedAsync(cancellationToken);
    }

    /// <summary>保存配置到数据库，并立即替换运行时快照。</summary>
    public async Task<TerminalOptions> SaveAsync(
        TerminalOptions settings,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await configs.SaveJsonAsync(FeatureName, json, updatedBy, cancellationToken);

        Volatile.Write(ref current, settings);
        Changed?.Invoke();
        return settings;
    }

    /// <summary>
    /// 首次迁移：把 <c>Koi:Terminal</c> 节（含外部配置覆盖）写入数据库并记录来源；
    /// 旧配置不合法或不存在时写入内置默认值。写库失败只告警，本次运行仍使用内存快照。
    /// </summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var section = configuration.GetSection(TerminalOptions.SectionName);
        TerminalOptions seeded;
        string updatedBy;
        if (section.Exists())
        {
            seeded = new TerminalOptions();

            // 配置绑定器绑定集合时会沿用属性现有内容：配置里写了 ShellArguments 时，
            // 必须先把内置默认值清空，否则会得到 ["-l","-l"] 这样的重复参数；
            // 没写时保留内置默认值。
            if (section.GetSection(nameof(TerminalOptions.ShellArguments)).Exists())
            {
                seeded.ShellArguments = [];
            }

            section.Bind(seeded);
            updatedBy = "appsettings";
        }
        else
        {
            seeded = new TerminalOptions();
            updatedBy = "defaults";
        }

        seeded = TerminalSettingsValidator.Normalize(seeded);
        if (TerminalSettingsValidator.Validate(seeded) is { } detail)
        {
            logger.LogWarning("旧终端配置不合法（{Detail}），改用内置默认值。", detail);
            seeded = new TerminalOptions();
            updatedBy = "defaults";
        }

        Volatile.Write(ref current, seeded);
        Changed?.Invoke();

        try
        {
            var json = JsonSerializer.Serialize(seeded, JsonOptions);
            await configs.SaveJsonAsync(FeatureName, json, updatedBy, cancellationToken);
            logger.LogInformation("已把终端配置写入数据库（来源：{Source}）。", updatedBy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "写入终端配置失败，本次运行使用内存中的配置。");
        }
    }
}
