using Microsoft.EntityFrameworkCore;

namespace Koi.Api.Data;

/// <summary>Koi 的 SQLite 数据上下文，后续业务表统一挂载于此。</summary>
public class KoiDbContext(DbContextOptions<KoiDbContext> options) : DbContext(options)
{
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SessionEntity>(entity =>
        {
            entity.ToTable("Sessions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.ExpiresAt);
        });
    }
}
