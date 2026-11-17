using Microsoft.EntityFrameworkCore;

namespace Koi.Api.Data;

/// <summary>Koi 的 SQLite 数据上下文，后续业务表统一挂载于此。</summary>
public class KoiDbContext(DbContextOptions<KoiDbContext> options) : DbContext(options)
{
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    public DbSet<FeatureConfigEntity> FeatureConfigs => Set<FeatureConfigEntity>();

    public DbSet<PackageCacheEntity> PackageCacheItems => Set<PackageCacheEntity>();

    public DbSet<EditorFileTypeEntity> EditorFileTypes => Set<EditorFileTypeEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // SQLite 不翻译 DateTimeOffset 比较，统一按 UTC ticks 存储以支持 Where / 排序 / ExecuteDelete。
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

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

        // 通用功能配置：每个功能一行，Feature 唯一，配置内容为 JSON。
        modelBuilder.Entity<FeatureConfigEntity>(entity =>
        {
            entity.ToTable("FeatureConfigs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Feature).IsUnique();
        });

        // 已安装软件缓存：名称唯一，刷新时按名称 Upsert。
        modelBuilder.Entity<PackageCacheEntity>(entity =>
        {
            entity.ToTable("PackageCacheItems");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        // 文本编辑后缀规则：后缀唯一，保存时整表替换。
        modelBuilder.Entity<EditorFileTypeEntity>(entity =>
        {
            entity.ToTable("EditorFileTypes");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Extension).IsUnique();
        });
    }
}
