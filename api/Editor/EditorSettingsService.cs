using System.Text.Json;
using Koi.Api.Configuration;

namespace Koi.Api.Editor;

/// <summary>
/// 文本编辑配置的运行时快照。
/// </summary>
/// <remarks>
/// 配置不再来自 <c>appsettings.json</c>，也不再读取前端 <c>conf/setting.json</c>：启动时从
/// SQLite 通用配置表（<c>Feature = editor</c>）读取；数据库中没有记录或内容不合法时写入
/// <see cref="EditorOptions"/> 的内置默认值。界面保存后立即整体替换快照并触发
/// <see cref="Changed"/>，因此文件管理中的文本关联规则无需重启服务即可生效。
/// </remarks>
public sealed class EditorSettingsService
{
    /// <summary>通用配置表中的功能标识。</summary>
    public const string FeatureName = "editor";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FeatureConfigStore configs;
    private readonly ILogger<EditorSettingsService> logger;
    private EditorOptions current = new();

    public EditorSettingsService(
        FeatureConfigStore configs,
        ILogger<EditorSettingsService> logger)
    {
        this.configs = configs;
        this.logger = logger;
    }

    /// <summary>当前生效的配置快照；整体替换发布，不原地修改。</summary>
    public EditorOptions Current => Volatile.Read(ref current);

    /// <summary>配置被保存或被启动加载替换后触发。</summary>
    public event Action? Changed;

    /// <summary>
    /// 从数据库加载配置；数据库中没有记录或内容不合法时，写入内置默认值。
    /// 读取失败不阻断启动，本次运行使用内存中的默认值。
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
            logger.LogWarning(ex, "读取文本编辑配置失败，使用内置默认值。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<EditorOptions>(json, JsonOptions);
                if (loaded is not null)
                {
                    var normalized = EditorSettingsValidator.Normalize(loaded);
                    if (EditorSettingsValidator.Validate(normalized) is null)
                    {
                        Volatile.Write(ref current, normalized);
                        Changed?.Invoke();
                        return;
                    }

                    logger.LogWarning("数据库中的文本编辑配置不合法，改用内置默认值。");
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "文本编辑配置内容无法解析，改用内置默认值。");
            }
        }

        await SeedAsync(cancellationToken);
    }

    /// <summary>保存配置到数据库，并立即替换运行时快照。</summary>
    public async Task<EditorOptions> SaveAsync(
        EditorOptions settings,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await configs.SaveJsonAsync(FeatureName, json, updatedBy, cancellationToken);

        Volatile.Write(ref current, settings);
        Changed?.Invoke();
        return settings;
    }

    /// <summary>首次启动或配置损坏时把内置默认值写入数据库；写库失败只告警。</summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var seeded = new EditorOptions();
        Volatile.Write(ref current, seeded);
        Changed?.Invoke();

        try
        {
            var json = JsonSerializer.Serialize(seeded, JsonOptions);
            await configs.SaveJsonAsync(FeatureName, json, "defaults", cancellationToken);
            logger.LogInformation("已把文本编辑默认配置写入数据库。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "写入文本编辑配置失败，本次运行使用内存中的默认值。");
        }
    }
}
