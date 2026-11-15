using System.Text.Json;
using Koi.Api.Configuration;

namespace Koi.Api.Packages;

/// <summary>
/// 应用管理配置的运行时快照。
/// </summary>
/// <remarks>
/// 配置不再来自 <c>appsettings.json</c>：启动时从 SQLite 通用配置表（Feature = <c>packages</c>）
/// 读取，界面保存后立即替换快照并触发 <see cref="Changed"/>，因此超时、上限、允许组与
/// 包管理器选择都无需重启服务即可生效。配置缺失或损坏时回退到 <see cref="PackageOptions"/> 的内置默认值。
/// </remarks>
public sealed class PackageSettingsService
{
    /// <summary>通用配置表中的功能标识。</summary>
    public const string FeatureName = "packages";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FeatureConfigStore configs;
    private readonly ILogger<PackageSettingsService> logger;
    private PackageOptions current = new();

    public PackageSettingsService(
        FeatureConfigStore configs,
        ILogger<PackageSettingsService> logger)
    {
        this.configs = configs;
        this.logger = logger;
    }

    /// <summary>当前生效的配置快照；整体替换发布，不原地修改。</summary>
    public PackageOptions Current => Volatile.Read(ref current);

    /// <summary>配置被保存或被启动加载替换后触发，供包管理器探测等缓存失效。</summary>
    public event Action? Changed;

    /// <summary>从数据库加载配置；失败时保留内置默认值并记录告警。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        string? json;
        try
        {
            json = await configs.GetJsonAsync(FeatureName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读取应用管理配置失败，使用内置默认值。");
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<PackageOptions>(json, JsonOptions);
            if (loaded is null)
            {
                return;
            }

            Volatile.Write(ref current, PackageSettingsValidator.Normalize(loaded));
            Changed?.Invoke();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "应用管理配置内容无法解析，使用内置默认值。");
        }
    }

    /// <summary>保存配置到数据库，并立即替换运行时快照。</summary>
    public async Task<PackageOptions> SaveAsync(
        PackageOptions settings,
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
