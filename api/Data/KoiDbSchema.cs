using Microsoft.EntityFrameworkCore;

namespace Koi.Api.Data;

/// <summary>
/// 兼容已有数据库的模式补齐。
/// </summary>
/// <remarks>
/// Koi 目前使用 <c>EnsureCreated()</c> 且没有迁移：数据库文件已存在时不会补充新增表。
/// 这里用幂等 DDL 创建后续新增的通用配置表，避免旧 <c>koi.db</c> 升级后因缺表直接报错。
/// 表结构与 <see cref="KoiDbContext"/> 的实体配置保持一致；改用 EF 迁移后应删除本类。
/// </remarks>
public static class KoiDbSchema
{
    public static void EnsureCompatibilityTables(KoiDbContext db)
    {
        // UpdatedAt 由 UtcTicksConverter 存为 INTEGER（UTC ticks）。
        // 已由 EnsureCreated 创建时，IF NOT EXISTS 不会产生任何改动。
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "FeatureConfigs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_FeatureConfigs" PRIMARY KEY AUTOINCREMENT,
                "Feature" TEXT NOT NULL,
                "ConfigJson" TEXT NOT NULL,
                "UpdatedAt" INTEGER NOT NULL,
                "UpdatedBy" TEXT NULL
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FeatureConfigs_Feature"
            ON "FeatureConfigs" ("Feature");
            """);
    }
}
