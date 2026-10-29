using System.Threading.RateLimiting;
using Koi.Api.Auth;
using Koi.Api.Data;
using Koi.Api.Hosting;
using Koi.Api.Terminal;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

// SQLite 数据库默认放在内容根目录，运行期生成且不入库。
var configuredDbPath = builder.Configuration["Koi:Database:Path"] ?? "koi.db";
var databasePath = Path.IsPathRooted(configuredDbPath)
    ? configuredDbPath
    : Path.Combine(builder.Environment.ContentRootPath, configuredDbPath);

var databaseDirectory = Path.GetDirectoryName(databasePath);
if (!string.IsNullOrEmpty(databaseDirectory))
{
    Directory.CreateDirectory(databaseDirectory);
}

builder.Services.AddDbContext<KoiDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<KoiStaticFilesOptions>(
    builder.Configuration.GetSection(KoiStaticFilesOptions.SectionName));
builder.Services.Configure<TerminalOptions>(
    builder.Configuration.GetSection(TerminalOptions.SectionName));
builder.Services.AddSingleton<IAuthenticator, PamAuthenticator>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddSingleton<TerminalSessionManager>();

var authOptions = builder.Configuration
    .GetSection(AuthOptions.SectionName)
    .Get<AuthOptions>() ?? new AuthOptions();
var loginPermitLimit = Math.Max(1, authOptions.LoginRateLimitPerMinute);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = loginPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            status = StatusCodes.Status429TooManyRequests,
            title = "登录尝试过于频繁",
            detail = "请稍后再试。",
        }, cancellationToken);
    };
});

var app = builder.Build();

// 当前阶段使用 EnsureCreated；后续引入迁移后改为 Migrate。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();
    db.Database.EnsureCreated();
}

// 反向代理（Nginx 等）终止 TLS 时，恢复原始协议与来源地址。
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
});

app.UseExceptionHandler();

// 静态文件先于认证中间件短路，避免前端资源请求触发会话查询。
app.UseKoiStaticFiles();

app.UseMiddleware<SameOriginMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<SessionAuthenticationMiddleware>();

// 在线终端：KeepAlive 用于探测半开连接，端点自身完成会话校验与来源校验。
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    platform = OperatingSystem.IsLinux() ? "linux" : "other",
    pamAvailable = OperatingSystem.IsLinux(),
    terminalAvailable = OperatingSystem.IsLinux(),
    timestamp = DateTimeOffset.UtcNow,
}));

app.MapAuthEndpoints();
app.MapTerminalEndpoints();

// /api 未匹配返回 404 ProblemDetails，其余无扩展名路径回退前端 index.html。
app.MapKoiFallbacks();

app.Run();

/// <summary>便于后续集成测试引用应用入口。</summary>
public partial class Program
{
}
