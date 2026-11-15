using System.Text.Json;
using Koi.Api.Configuration;

namespace Koi.Api.Processes;

/// <summary>
/// 进程管理配置的运行时快照。
/// </summary>
/// <remarks>
/// 配置不来自 <c>appsettings.json</c>：启动时从 SQLite 通用配置表（Feature = <c>process</c>）
/// 读取；数据库中没有记录时直接使用 <see cref="ProcessOptions"/> 的内置默认值（不读取旧的
/// <c>Koi:Processes</c> 配置节，也不写种子行）。界面保存后立即整体替换快照并触发
/// <see cref="Changed"/>，因此上限、采样窗口、刷新间隔与允许组都无需重启服务即可生效。
/// 配置缺失或损坏时回退到内置默认值。
/// </remarks>
public sealed class ProcessSettingsService
{
    /// <summary>通用配置表中的功能标识。</summary>
    public const string FeatureName = "process";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FeatureConfigStore configs;
    private readonly ILogger<ProcessSettingsService> logger;
    private ProcessOptions current = new();

    public ProcessSettingsService(
        FeatureConfigStore configs,
        ILogger<ProcessSettingsService> logger)
    {
        this.configs = configs;
        this.logger = logger;
    }

    /// <summary>当前生效的配置快照；整体替换发布，不原地修改。</summary>
    public ProcessOptions Current => Volatile.Read(ref current);

    /// <summary>配置被保存或被启动加载替换后触发。</summary>
    public event Action? Changed;

    /// <summary>从数据库加载配置；缺失或损坏时保留内置默认值并记录告警。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        string? json;
        try
        {
            json = await configs.GetJsonAsync(FeatureName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读取进程管理配置失败，使用内置默认值。");
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<ProcessOptions>(json, JsonOptions);
            if (loaded is null)
            {
                return;
            }

            var normalized = ProcessSettingsValidator.Normalize(loaded);
            if (ProcessSettingsValidator.Validate(normalized) is { } detail)
            {
                logger.LogWarning("数据库中的进程管理配置不合法（{Detail}），使用内置默认值。", detail);
                return;
            }

            Volatile.Write(ref current, normalized);
            Changed?.Invoke();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "进程管理配置内容无法解析，使用内置默认值。");
        }
    }

    /// <summary>保存配置到数据库，并立即替换运行时快照。</summary>
    public async Task<ProcessOptions> SaveAsync(
        ProcessOptions settings,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await configs.SaveJsonAsync(FeatureName, json, updatedBy, cancellationToken);

        Volatile.Write(ref current, settings);
        Changed?.Invoke();
        return settings;
    }
}
