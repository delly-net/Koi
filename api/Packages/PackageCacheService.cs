using Koi.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Koi.Api.Packages;

/// <summary>
/// 已安装软件缓存读写。
/// </summary>
/// <remarks>
/// 单例服务，通过 <see cref="IServiceScopeFactory"/> 获取 scoped 的 <see cref="KoiDbContext"/>，
/// 不持有请求作用域。刷新缓存会整表核对系统当前的已安装列表：
/// <list type="bullet">
/// <item>新出现的软件插入缓存，简介用包管理器描述预填；</item>
/// <item>已存在的软件只更新版本与状态，<b>简介一律不写</b>，因此用户修改不会被刷新覆盖；</item>
/// <item>系统中已不存在的软件标记为 <c>removed</c> 并保留记录，重新出现时恢复 <c>installed</c>。</item>
/// </list>
/// 刷新以信号量串行化，避免并发插入同名记录触发唯一索引冲突。
/// </remarks>
public sealed class PackageCacheService(IServiceScopeFactory scopeFactory)
{
    /// <summary>简介最大长度（字符），与实体映射及前端输入限制一致。</summary>
    public const int MaxIntroLength = 500;

    private readonly SemaphoreSlim refreshGate = new(1, 1);

    /// <summary>分页查询缓存；<paramref name="status"/> 为 all / installed / removed。</summary>
    public async Task<(PackageCacheEntry[] Items, int Total, DateTimeOffset? LastRefreshedAt)> QueryAsync(
        int page,
        int pageSize,
        string status,
        string? query,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();

        var source = db.PackageCacheItems.AsNoTracking();

        if (status == PackageCacheStatusFilters.Installed)
        {
            source = source.Where(item => item.Status == PackageCacheStatuses.Installed);
        }
        else if (status == PackageCacheStatusFilters.Removed)
        {
            source = source.Where(item => item.Status == PackageCacheStatuses.Removed);
        }

        var keyword = query?.Trim() ?? string.Empty;
        if (keyword.Length > 0)
        {
            // SQLite 的 LIKE 对 ASCII 不区分大小写，包名均为 ASCII。
            source = source.Where(item => item.Name.Contains(keyword));
        }

        var total = await source.CountAsync(cancellationToken);

        var items = await source
            .OrderBy(item => item.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new PackageCacheEntry(
                item.Id,
                item.Name,
                item.Version,
                item.Intro,
                item.Status,
                item.UpdatedAt))
            .ToArrayAsync(cancellationToken);

        // 取一条最新时间即可；用 Take(1) 避免空表时把默认值当成真实刷新时间。
        var latest = await db.PackageCacheItems
            .AsNoTracking()
            .OrderByDescending(item => item.RefreshedAt)
            .Select(item => item.RefreshedAt)
            .Take(1)
            .ToListAsync(cancellationToken);

        return (items, total, latest.Count > 0 ? latest[0] : null);
    }

    /// <summary>修改一条缓存记录的简介；记录不存在时返回 <c>null</c>。</summary>
    public async Task<PackageCacheEntry?> UpdateIntroAsync(
        long id,
        string intro,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();

        var entity = await db.PackageCacheItems
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        entity.Intro = intro.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return ToEntry(entity);
    }

    /// <summary>
    /// 用系统当前的已安装列表刷新缓存；返回新增、变更与标记移除的条数。
    /// </summary>
    public async Task<PackageCacheRefreshResult> RefreshAsync(
        IReadOnlyList<PackageSummary> installed,
        string manager,
        CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();

            var now = DateTimeOffset.UtcNow;
            var cached = new Dictionary<string, PackageCacheEntity>(StringComparer.Ordinal);
            await foreach (var entity in db.PackageCacheItems
                               .AsAsyncEnumerable()
                               .WithCancellation(cancellationToken))
            {
                cached[entity.Name] = entity;
            }

            var current = new HashSet<string>(StringComparer.Ordinal);
            var added = 0;
            var updated = 0;

            foreach (var package in installed)
            {
                var name = package.Name.Trim();
                if (name.Length == 0 || !current.Add(name))
                {
                    continue;
                }

                if (cached.TryGetValue(name, out var entity))
                {
                    var changed = entity.Status != PackageCacheStatuses.Installed ||
                                  !string.Equals(entity.Version, package.Version, StringComparison.Ordinal) ||
                                  !string.Equals(entity.Manager, manager, StringComparison.Ordinal);

                    // 简介由用户维护：刷新只更新时间戳与系统字段，绝不改写 Intro。
                    entity.Status = PackageCacheStatuses.Installed;
                    entity.Version = package.Version;
                    entity.Manager = manager;
                    entity.RefreshedAt = now;
                    if (changed)
                    {
                        entity.UpdatedAt = now;
                        updated++;
                    }

                    continue;
                }

                db.PackageCacheItems.Add(new PackageCacheEntity
                {
                    Name = name,
                    Version = package.Version,
                    Intro = TruncateIntro(package.Summary),
                    Status = PackageCacheStatuses.Installed,
                    Manager = manager,
                    CreatedAt = now,
                    UpdatedAt = now,
                    RefreshedAt = now,
                });
                added++;
            }

            var removed = 0;
            foreach (var (name, entity) in cached)
            {
                if (current.Contains(name) || entity.Status == PackageCacheStatuses.Removed)
                {
                    continue;
                }

                entity.Status = PackageCacheStatuses.Removed;
                entity.RefreshedAt = now;
                entity.UpdatedAt = now;
                removed++;
            }

            await db.SaveChangesAsync(cancellationToken);

            return new PackageCacheRefreshResult(
                current.Count,
                added,
                updated,
                removed,
                now);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>包管理器描述做空白归一化并按简介长度上限截断。</summary>
    private static string TruncateIntro(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var text = string.Join(' ', summary.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= MaxIntroLength ? text : text[..MaxIntroLength];
    }

    private static PackageCacheEntry ToEntry(PackageCacheEntity entity) =>
        new(entity.Id, entity.Name, entity.Version, entity.Intro, entity.Status, entity.UpdatedAt);
}

/// <summary>一次缓存刷新的统计结果。</summary>
public sealed record PackageCacheRefreshResult(
    int Total,
    int Added,
    int Updated,
    int Removed,
    DateTimeOffset RefreshedAt);
