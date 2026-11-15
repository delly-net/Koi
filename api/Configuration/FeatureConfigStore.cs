using System.Collections.Concurrent;
using Koi.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Koi.Api.Configuration;

/// <summary>
/// 通用功能配置读写：以「功能」标识区分，配置内容为 JSON。
/// </summary>
/// <remarks>
/// 单例服务，通过 <see cref="IServiceScopeFactory"/> 获取 scoped 的 <see cref="KoiDbContext"/>，
/// 不持有请求作用域。读取结果进程内缓存，保存后立即更新缓存，因此同一进程内后续读取能看到最新值。
/// 后续功能只需传入自己的功能标识即可复用本服务与 <c>FeatureConfigs</c> 表。
/// </remarks>
public sealed class FeatureConfigStore(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>读取功能配置 JSON；未保存过该功能时返回 <c>null</c>。</summary>
    public async Task<string?> GetJsonAsync(string feature, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(feature, out var cached))
        {
            return cached;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(feature, out cached))
            {
                return cached;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();
            var entity = await db.FeatureConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Feature == feature, cancellationToken);

            if (entity is null)
            {
                return null;
            }

            cache[feature] = entity.ConfigJson;
            return entity.ConfigJson;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>保存功能配置 JSON（新增或覆盖），同时更新进程内缓存。</summary>
    public async Task SaveJsonAsync(
        string feature,
        string json,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();
            var entity = await db.FeatureConfigs
                .FirstOrDefaultAsync(item => item.Feature == feature, cancellationToken);

            if (entity is null)
            {
                db.FeatureConfigs.Add(new FeatureConfigEntity
                {
                    Feature = feature,
                    ConfigJson = json,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    UpdatedBy = updatedBy,
                });
            }
            else
            {
                entity.ConfigJson = json;
                entity.UpdatedAt = DateTimeOffset.UtcNow;
                entity.UpdatedBy = updatedBy;
            }

            await db.SaveChangesAsync(cancellationToken);
            cache[feature] = json;
        }
        finally
        {
            gate.Release();
        }
    }
}
