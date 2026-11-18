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

        // 已安装软件缓存；时间列由 UtcTicksConverter 存为 INTEGER（UTC ticks）。
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "PackageCacheItems" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PackageCacheItems" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Version" TEXT NOT NULL,
                "Intro" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Manager" TEXT NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                "UpdatedAt" INTEGER NOT NULL,
                "RefreshedAt" INTEGER NOT NULL
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PackageCacheItems_Name"
            ON "PackageCacheItems" ("Name");
            """);

        // 文本编辑支持的后缀名与着色方案；时间列由 UtcTicksConverter 存为 INTEGER（UTC ticks）。
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "EditorFileTypes" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_EditorFileTypes" PRIMARY KEY AUTOINCREMENT,
                "Extension" TEXT NOT NULL,
                "Scheme" TEXT NOT NULL,
                "UpdatedAt" INTEGER NOT NULL,
                "UpdatedBy" TEXT NULL
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_EditorFileTypes_Extension"
            ON "EditorFileTypes" ("Extension");
            """);

        // 文件管理快速访问目录：按账号隔离，CreatedAt 由 UtcTicksConverter 存为 INTEGER。
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "FileQuickAccess" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_FileQuickAccess" PRIMARY KEY AUTOINCREMENT,
                "UserName" TEXT NOT NULL,
                "Path" TEXT NOT NULL,
                "CreatedAt" INTEGER NOT NULL
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FileQuickAccess_UserName_Path"
            ON "FileQuickAccess" ("UserName", "Path");
            """);
    }
}
