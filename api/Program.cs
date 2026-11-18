using System.Threading.RateLimiting;
using Koi.Api.Auth;
using Koi.Api.Configuration;
using Koi.Api.Data;
using Koi.Api.Docker;
using Koi.Api.Editor;
using Koi.Api.Files;
using Koi.Api.Hosting;
using Koi.Api.Monitor;
using Koi.Api.Nginx;
using Koi.Api.Packages;
using Koi.Api.Processes;
using Koi.Api.Services;
using Koi.Api.Terminal;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
// Koi 的文件配置类与 System.IO.FileOptions 同名，这里显式指定。
using FileOptions = Koi.Api.Files.FileOptions;

// 辅助进程模式：必须在构建 Web 宿主之前分流，辅助进程只执行单次任务，不启动 Kestrel。
if (PtyHelper.TryRun(args))
{
    return;
}

// 文件辅助进程：从标准输入读取一次降权后的文件操作请求并回传结果。
if (FileHelper.TryRun(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Linux 下从 /etc/koi/appsettings.json 读取外部配置；文件缺失时用发布默认值自动创建。
// 必须早于任何 builder.Configuration 读取，否则数据库路径、文件与终端上限仍取旧值。
var externalConfigPath = builder.AddKoiExternalConfiguration();

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
builder.Services.Configure<SystemMonitorOptions>(
    builder.Configuration.GetSection(SystemMonitorOptions.SectionName));
builder.Services.AddSingleton<IAuthenticator, PamAuthenticator>();
builder.Services.AddScoped<SessionService>();
// 终端配置来自 SQLite 通用配置表（FeatureConfigs），不再读取 appsettings.json。
builder.Services.AddSingleton<TerminalSettingsService>();
builder.Services.AddSingleton<TerminalSessionManager>();
// 文件管理配置来自 SQLite 通用配置表（FeatureConfigs），不再读取 appsettings.json。
builder.Services.AddSingleton<FileSettingsService>();
builder.Services.AddSingleton<FileOperationService>();
// 快速访问目录按登录账号保存在 SQLite 的 FileQuickAccess 表。
builder.Services.AddSingleton<FileQuickAccessService>();
// 文本编辑后缀名与着色方案来自 SQLite 独立表 EditorFileTypes（替代旧的 FeatureConfigs 文本扩展名）。
builder.Services.AddSingleton<EditorFileTypeService>();
// 容器管理配置来自 SQLite 通用配置表（FeatureConfigs），不再读取 appsettings.json。
builder.Services.AddSingleton<DockerSettingsService>();
builder.Services.AddSingleton<DockerCommandRunner>();
builder.Services.AddSingleton<SystemMonitorService>();
// 服务管理配置来自 SQLite 通用配置表（FeatureConfigs），不再读取 appsettings.json。
builder.Services.AddSingleton<ServiceSettingsService>();
builder.Services.AddSingleton<SystemctlCommandRunner>();
// 进程管理读取 /proc 完成采集，配置同样来自 SQLite 通用配置表（FeatureConfigs）。
builder.Services.AddSingleton<ProcessSettingsService>();
builder.Services.AddSingleton<ProcessService>();
// Nginx 管理配置同样来自 SQLite 通用配置表（FeatureConfigs）；配置文件由主进程直接读写。
builder.Services.AddSingleton<NginxSettingsService>();
builder.Services.AddSingleton<NginxCommandRunner>();
builder.Services.AddSingleton<NginxConfigService>();
// 应用管理配置来自 SQLite 通用配置表（FeatureConfigs），不再读取 appsettings.json。
builder.Services.AddSingleton<FeatureConfigStore>();
builder.Services.AddSingleton<PackageSettingsService>();
builder.Services.AddSingleton<PackageCommandRunner>();
builder.Services.AddSingleton<PackageManagerResolver>();
builder.Services.AddSingleton<PackageSourceService>();
builder.Services.AddSingleton<PackageJobManager>();
builder.Services.AddSingleton<PackageCacheService>();

// 文件上传上限在运行期来自 SQLite 配置，但 Kestrel 的请求体上限只能在启动时确定：
// 这里按固定硬顶（512 MiB）加 1 MiB 余量放行，具体单文件上限由端点逐请求检查。
var uploadRequestLimit = FileOptions.MaxUploadBytesCeiling + 1024 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = uploadRequestLimit);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = uploadRequestLimit);

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

if (externalConfigPath is not null)
{
    app.Logger.LogInformation("外部配置文件：{ConfigPath}", externalConfigPath);
}

// 当前阶段使用 EnsureCreated；后续引入迁移后改为 Migrate。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KoiDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated 不会给已存在的数据库补表：幂等创建新增的通用配置表。
    KoiDbSchema.EnsureCompatibilityTables(db);
}

// 应用管理配置在启动时加载一次；之后由设置端点保存并立即替换运行时快照。
await app.Services.GetRequiredService<PackageSettingsService>().LoadAsync();

// 终端配置同样在启动时加载；数据库无记录时把旧 Koi:Terminal 节一次性迁移进来。
await app.Services.GetRequiredService<TerminalSettingsService>().LoadAsync();

// 文件管理配置在启动时加载；数据库无记录时把旧 Koi:Files 节一次性迁移进来。
await app.Services.GetRequiredService<FileSettingsService>().LoadAsync();

// 文本编辑后缀规则在启动时加载；表为空且未迁移时迁移旧配置或写入内置默认规则。
await app.Services.GetRequiredService<EditorFileTypeService>().LoadAsync();

// 服务管理配置在启动时加载；数据库无记录时直接使用代码默认值（不读取旧 Koi:Services 节）。
await app.Services.GetRequiredService<ServiceSettingsService>().LoadAsync();

// 容器管理配置在启动时加载；数据库无记录时直接使用代码默认值（不读取旧 Koi:Docker 节）。
await app.Services.GetRequiredService<DockerSettingsService>().LoadAsync();

// 进程管理配置在启动时加载；数据库无记录时直接使用代码默认值（不读取旧 Koi:Processes 节）。
await app.Services.GetRequiredService<ProcessSettingsService>().LoadAsync();

// Nginx 管理配置在启动时加载；数据库无记录时直接使用代码默认值（不读取旧 Koi:Nginx 节）。
await app.Services.GetRequiredService<NginxSettingsService>().LoadAsync();

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

app.MapGet("/api/health", (
    DockerSettingsService dockerSettings,
    ServiceSettingsService serviceSettings,
    NginxSettingsService nginxSettings,
    PackageManagerResolver packageResolver) => Results.Ok(new
{
    status = "ok",
    platform = OperatingSystem.IsLinux() ? "linux" : "other",
    pamAvailable = OperatingSystem.IsLinux(),
    terminalAvailable = OperatingSystem.IsLinux(),
    fileAvailable = OperatingSystem.IsLinux(),
    dockerAvailable = DockerCommandRunner.IsAvailable(dockerSettings.Current),
    systemMonitorAvailable = OperatingSystem.IsLinux(),
    serviceAvailable = SystemctlCommandRunner.IsAvailable(serviceSettings.Current),
    processAvailable = OperatingSystem.IsLinux(),
    nginxAvailable = NginxCommandRunner.IsAvailable(nginxSettings.Current),
    packageAvailable = packageResolver.Backend is not null,
    timestamp = DateTimeOffset.UtcNow,
}));

app.MapAuthEndpoints();
app.MapTerminalEndpoints();
app.MapFileEndpoints();
app.MapEditorEndpoints();
app.MapDockerEndpoints();
app.MapSystemMonitorEndpoints();
app.MapServiceEndpoints();
app.MapProcessEndpoints();
app.MapNginxEndpoints();
app.MapPackageEndpoints();

// /api 未匹配返回 404 ProblemDetails，其余无扩展名路径回退前端 index.html。
app.MapKoiFallbacks();

app.Run();

/// <summary>便于后续集成测试引用应用入口。</summary>
public partial class Program
{
}
