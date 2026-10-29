using System.Security.Cryptography;
using System.Text;
using Koi.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Koi.Api.Auth;

/// <summary>服务端会话管理：创建、校验、吊销与过期清理。</summary>
public sealed class SessionService(
    KoiDbContext db,
    IOptions<AuthOptions> options,
    ILogger<SessionService> logger)
{
    private static readonly TimeSpan LastSeenUpdateInterval = TimeSpan.FromMinutes(5);

    private readonly AuthOptions _options = options.Value;

    /// <summary>创建会话并返回明文令牌（仅 Cookie 持有，数据库只存摘要）。</summary>
    public async Task<(string Token, SessionEntity Session)> CreateAsync(
        string userName,
        string? remoteIp,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await CleanupExpiredAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var token = GenerateToken();
        var session = new SessionEntity
        {
            TokenHash = HashToken(token),
            UserName = userName,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddHours(_options.SessionExpireHours),
            RemoteIp = Truncate(remoteIp, 64),
            UserAgent = Truncate(userAgent, 512),
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return (token, session);
    }

    /// <summary>按明文令牌查找有效会话；命中后按需刷新最近访问时间。</summary>
    public async Task<SessionEntity?> FindAsync(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        var now = DateTimeOffset.UtcNow;
        var session = await db.Sessions
            .FirstOrDefaultAsync(x => x.TokenHash == hash && x.ExpiresAt > now, cancellationToken);

        if (session is null)
        {
            return null;
        }

        if (now - session.LastSeenAt >= LastSeenUpdateInterval)
        {
            session.LastSeenAt = now;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // 最近访问时间属于非关键数据，写失败不影响本次请求。
                logger.LogWarning(ex, "更新会话最近访问时间失败。");
            }
        }

        return session;
    }

    /// <summary>吊销指定令牌对应的会话。</summary>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        await db.Sessions
            .Where(x => x.TokenHash == hash)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>清理已过期会话。</summary>
    public async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Sessions
            .Where(x => x.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
