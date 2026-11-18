using System.Text.Json;
using Koi.Api.Configuration;
using Koi.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Koi.Api.Editor;

/// <summary>一条后缀规则及其着色方案。</summary>
public sealed record EditorFileTypeInfo(string Extension, string Scheme);

/// <summary>
/// 文本编辑支持的文件后缀名与着色方案的运行时快照。
/// </summary>
/// <remarks>
/// 数据来自 SQLite 独立表 <c>EditorFileTypes</c>，取代了原先
/// <c>FeatureConfigs(Feature = editor)</c> 中的 <c>textExtensions</c>。
/// 首次启动时若表为空，会迁移旧配置的后缀列表（保留用户自定义，含空数组）并按默认规则
/// 补齐着色方案；迁移完成后写入标记行 <c>Feature = editor-file-types</c>，避免用户清空
/// 全部后缀后重启又被重新种子化。保存时整体替换表内容并发布新的内存快照，
/// 因此文件管理的文本关联与编辑器着色都无需重启即可生效。
/// </remarks>
public sealed class EditorFileTypeService
{
    /// <summary>旧配置行（已废弃，仅用于一次性迁移）。</summary>
    private const string LegacyFeature = "editor";

    /// <summary>迁移标记行，存在即表示已完成迁移/种子化。</summary>
    public const string MigrationFeature = "editor-file-types";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FeatureConfigStore configs;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<EditorFileTypeService> logger;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private EditorFileTypeInfo[] current = [];

    public EditorFileTypeService(
        FeatureConfigStore configs,
        IServiceScopeFactory scopeFactory,
        ILogger<EditorFileTypeService> logger)
    {
        this.configs = configs;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    /// <summary>当前生效的后缀规则快照；整体替换发布，不原地修改。</summary>
    public IReadOnlyList<EditorFileTypeInfo> Current => Volatile.Read(ref current);

    /// <summary>规则被保存或被启动加载替换后触发。</summary>
    public event Action? Changed;

    /// <summary>
    /// 从数据库加载后缀规则；表为空且未迁移时先迁移旧配置或写入内置默认值。
    /// 读取失败不阻断启动，本次运行使用内置默认值。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        EditorFileTypeEntity[] rows;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();
            rows = await db.EditorFileTypes
                .AsNoTracking()
                .OrderBy(item => item.Extension)
                .ToArrayAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读取文本编辑后缀规则失败，使用内置默认值。");
            Publish(NormalizeItems(EditorSyntaxSchemes.DefaultExtensions
                .Select(rule => new EditorFileTypeInfo(rule, EditorSyntaxSchemes.DefaultSchemeFor(rule)))));
            return;
        }

        var loaded = NormalizeItems(rows.Select(row => new EditorFileTypeInfo(row.Extension, row.Scheme)));
        if (loaded.Count > 0)
        {
            Publish(loaded);
            return;
        }

        if (await IsMigratedAsync(cancellationToken))
        {
            // 已完成迁移但表为空：用户主动清空了全部规则，保持为空。
            Publish([]);
            return;
        }

        var seeds = await BuildSeedItemsAsync(cancellationToken);
        try
        {
            await ReplaceRowsAsync(seeds, "migration", cancellationToken);
            await SaveMigrationMarkerAsync(cancellationToken);
            logger.LogInformation("已迁移/初始化文本编辑后缀规则，共 {Count} 条。", seeds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "写入文本编辑后缀规则失败，本次运行使用内存中的默认值。");
        }

        Publish(seeds);
    }

    /// <summary>整体替换后缀规则并立即发布新快照。</summary>
    public async Task<IReadOnlyList<EditorFileTypeInfo>> SaveAsync(
        IReadOnlyList<EditorFileTypeInfo> items,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeItems(items);

        await saveGate.WaitAsync(cancellationToken);
        try
        {
            await ReplaceRowsAsync(normalized, updatedBy, cancellationToken);

            // 正常启动时标记已由 LoadAsync 写入；这里兜底一次，避免空列表在重启后被重复种子化。
            await SaveMigrationMarkerAsync(cancellationToken);
        }
        finally
        {
            saveGate.Release();
        }

        Publish(normalized);
        return Current;
    }

    /// <summary>用默认映射把旧配置的后缀规则补齐为「后缀 + 方案」。</summary>
    private async Task<List<EditorFileTypeInfo>> BuildSeedItemsAsync(CancellationToken cancellationToken)
    {
        string[] rules = EditorSyntaxSchemes.DefaultExtensions;

        try
        {
            var json = await configs.GetJsonAsync(LegacyFeature, cancellationToken);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var legacy = JsonSerializer.Deserialize<LegacyEditorOptions>(json, JsonOptions);
                if (legacy?.TextExtensions is { } textExtensions)
                {
                    rules = textExtensions;
                }
                else
                {
                    logger.LogWarning("旧文本编辑配置缺少 textExtensions，改用内置默认后缀。");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "旧文本编辑配置无法解析，改用内置默认后缀。");
        }

        return NormalizeItems(rules.Select(rule =>
            new EditorFileTypeInfo(rule, EditorSyntaxSchemes.DefaultSchemeFor(rule))));
    }

    private async Task<bool> IsMigratedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await configs.GetJsonAsync(MigrationFeature, cancellationToken);
            return !string.IsNullOrWhiteSpace(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "读取文本编辑后缀规则迁移标记失败，按未迁移处理。");
            return false;
        }
    }

    private async Task SaveMigrationMarkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await configs.SaveJsonAsync(MigrationFeature, """{"seeded":true}""", "migration", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "写入文本编辑后缀规则迁移标记失败。");
        }
    }

    /// <summary>在同一个 SaveChanges 中整表替换，保证要么全部生效、要么全部回滚。</summary>
    private async Task ReplaceRowsAsync(
        IReadOnlyList<EditorFileTypeInfo> items,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();

        var existing = await db.EditorFileTypes.ToListAsync(cancellationToken);
        db.EditorFileTypes.RemoveRange(existing);

        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            db.EditorFileTypes.Add(new EditorFileTypeEntity
            {
                Extension = item.Extension,
                Scheme = item.Scheme,
                UpdatedAt = now,
                UpdatedBy = updatedBy,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>规范化并去重（保序）；数据库中的未知方案回退纯文本。</summary>
    private List<EditorFileTypeInfo> NormalizeItems(IEnumerable<EditorFileTypeInfo> items)
    {
        var result = new List<EditorFileTypeInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var extension = item.Extension?.Trim().ToLowerInvariant() ?? string.Empty;
            if (extension.Length == 0 || !seen.Add(extension))
            {
                continue;
            }

            var scheme = EditorSyntaxSchemes.Normalize(item.Scheme);
            if (!EditorSyntaxSchemes.IsKnown(scheme))
            {
                if (scheme.Length > 0)
                {
                    logger.LogWarning("后缀 {Extension} 使用未知着色方案 {Scheme}，回退纯文本。", extension, scheme);
                }

                scheme = EditorSyntaxSchemes.PlainText;
            }

            result.Add(new EditorFileTypeInfo(extension, scheme));
        }

        return result;
    }

    private void Publish(IReadOnlyList<EditorFileTypeInfo> items)
    {
        Volatile.Write(ref current, [.. items]);
        Changed?.Invoke();
    }

    /// <summary>旧版 <c>Feature = editor</c> 配置行的最小反序列化模型。</summary>
    private sealed class LegacyEditorOptions
    {
        public string[]? TextExtensions { get; set; }
    }
}
