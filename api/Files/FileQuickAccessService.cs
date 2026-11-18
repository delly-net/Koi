using Koi.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Koi.Api.Files;

/// <summary>快速访问列表中的一条目录。</summary>
public sealed record FileQuickAccessEntry(long Id, string Path, string Name);

/// <summary>添加快捷访问的结果；<see cref="LimitReached"/> 为真时表示已达到每账号上限。</summary>
public sealed record FileQuickAccessAddResult(
    FileQuickAccessEntry? Item,
    bool Created,
    bool LimitReached);

/// <summary>
/// 文件管理快速访问目录的读写。
/// </summary>
/// <remarks>
/// 单例服务，通过 <see cref="IServiceScopeFactory"/> 获取 scoped 的 <see cref="KoiDbContext"/>，
/// 不持有请求作用域。列表按登录账号隔离：任何账号只能读写自己的条目，重复添加幂等返回既有记录。
/// 快速访问只是导航快捷方式，不构成权限授予；打开目录时仍按登录用户的系统权限与允许根目录校验。
/// </remarks>
public sealed class FileQuickAccessService(IServiceScopeFactory scopeFactory)
{
    /// <summary>每个账号最多保存的条目数。</summary>
    public const int MaxEntriesPerUser = 50;

    /// <summary>列出某账号的快速访问目录，按添加顺序升序。</summary>
    public async Task<FileQuickAccessEntry[]> ListAsync(
        string userName,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();

        var rows = await db.FileQuickAccess
            .AsNoTracking()
            .Where(item => item.UserName == userName)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Path })
            .ToArrayAsync(cancellationToken);

        return rows
            .Select(row => new FileQuickAccessEntry(row.Id, row.Path, NameFor(row.Path)))
            .ToArray();
    }

    /// <summary>
    /// 把目录加入某账号的快速访问列表。目录已存在时原样返回既有条目（不重复插入）；
    /// 达到每账号上限时返回 <see cref="FileQuickAccessAddResult.LimitReached"/>。
    /// </summary>
    public async Task<FileQuickAccessAddResult> AddAsync(
        string userName,
        string path,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();

        var existing = await FindAsync(db, userName, path, cancellationToken);
        if (existing is not null)
        {
            return new FileQuickAccessAddResult(existing, false, false);
        }

        var count = await db.FileQuickAccess.CountAsync(
            item => item.UserName == userName,
            cancellationToken);
        if (count >= MaxEntriesPerUser)
        {
            return new FileQuickAccessAddResult(null, false, true);
        }

        var entity = new FileQuickAccessEntity
        {
            UserName = userName,
            Path = path,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.FileQuickAccess.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发添加同一目录时唯一索引会拒绝插入；回查既有记录并按幂等返回。
            db.ChangeTracker.Clear();
            var raced = await FindAsync(db, userName, path, cancellationToken);
            if (raced is not null)
            {
                return new FileQuickAccessAddResult(raced, false, false);
            }

            throw;
        }

        return new FileQuickAccessAddResult(
            new FileQuickAccessEntry(entity.Id, entity.Path, NameFor(entity.Path)),
            true,
            false);
    }

    /// <summary>移除某账号的一条记录；条目不存在或不属于该账号时返回 <c>false</c>。</summary>
    public async Task<bool> RemoveAsync(
        string userName,
        long id,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();

        var entity = await db.FileQuickAccess
            .FirstOrDefaultAsync(
                item => item.Id == id && item.UserName == userName,
                cancellationToken);

        if (entity is null)
        {
            return false;
        }

        db.FileQuickAccess.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>条目的显示名称：取路径最后一段，根目录显示 /。</summary>
    public static string NameFor(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return "/";
        }

        var name = System.IO.Path.GetFileName(path.TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static async Task<FileQuickAccessEntry?> FindAsync(
        KoiDbContext db,
        string userName,
        string path,
        CancellationToken cancellationToken)
    {
        var row = await db.FileQuickAccess
            .AsNoTracking()
            .Where(item => item.UserName == userName && item.Path == path)
            .Select(item => new { item.Id, item.Path })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new FileQuickAccessEntry(row.Id, row.Path, NameFor(row.Path));
    }
}
