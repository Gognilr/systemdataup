using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BackupMonitor.Api.Bootstrap;
using BackupMonitor.Api.Controllers;
using BackupMonitor.Api.Discovery;
using BackupMonitor.Api.Health;
using BackupMonitor.Api.Middleware;
using BackupMonitor.Api.Security;
using BackupMonitor.Infrastructure;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Infrastructure.Services;
using BackupMonitor.Shared.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using Serilog;

// 以 Windows 服务方式运行时工作目录默认为 System32，
// 固定到程序目录，保证 Serilog 相对路径 logs/ 等落点正确
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// 控制台输出编码固定为 UTF-8：日志消息含中文，控制台默认 OEM 代码页会输出乱码
// （重定向到文件时尤其明显）。以 Windows 服务运行时无控制台，忽略即可。
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
    // 无控制台可用
}

if (MigrationCommand.IsMigrationCommand(args))
{
    Environment.ExitCode = MigrationCommand.RunAsync(args).GetAwaiter().GetResult();
    return;
}

var builder = WebApplication.CreateBuilder(args);

// 服务端安装器写入的非机密 Turnkey 配置只描述数据目录、监听地址和部署模式；
// 机密仍由 LocalServerBootstrap 从 ProgramData 密钥文件加载。
builder.Configuration.AddJsonFile("appsettings.Turnkey.json", optional: true, reloadOnChange: false);

// LAN Turnkey 安装器通过 BACKUPMONITOR_TURNKEY 启用本地引导；
// Secure/开发部署未启用时不生成 ProgramData 文件，继续使用原有外部配置。
var localBootstrap = LocalServerBootstrap.Initialize(builder.Configuration);
if (localBootstrap.Enabled)
    builder.Configuration.AddInMemoryCollection(localBootstrap.Values);

var apiPort = builder.Configuration.GetValue("Server:ApiPort", 5080);
X509Certificate2? turnkeyServerCertificate = null;
if (localBootstrap.Enabled)
{
    var certificatePath = builder.Configuration["Security:ServerCertificate:CertPath"]
        ?? throw new InvalidOperationException("LAN Turnkey 服务端证书路径未配置。");
    var certificatePassword = builder.Configuration["Security:ServerCertificate:Password"]
        ?? throw new InvalidOperationException("LAN Turnkey 服务端证书保护口令未配置。");
    turnkeyServerCertificate = ServerCertificateFactory.LoadForKestrel(certificatePath, certificatePassword);
}

// Serilog
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

// 以 Windows 服务方式托管（SCM 下自动把工作目录/内容根固定到 exe 所在目录；控制台运行时为无操作）
builder.Host.UseWindowsService();

// DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Infrastructure 服务层
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddHostedService<LanDiscoveryHostedService>();

// 健康检查（OPEN-ISSUES #6）：/health/db 改为只读自检。
// 使用框架内置 AddHealthChecks + 自定义只读 DatabaseHealthCheck；
// 不引入第三方 AspNetCore.HealthChecks.NpgSql（AddNpgSql），遵守"不新增第三方依赖"约束。
// 工厂在请求作用域内解析 AppDbContext（DefaultHealthCheckService 按作用域解析，安全）。
builder.Services.AddHealthChecks()
    .Add(new HealthCheckRegistration(
        "database",
        sp => new DatabaseHealthCheck(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<PartitionMaintenanceService>()),
        failureStatus: HealthStatus.Unhealthy,
        tags: null));

// Kestrel：Secure 形态只在本机回环 5080 提供 HTTP，由 nginx 终结 TLS/mTLS；
// LAN Turnkey 形态由程序显式注册唯一的 HTTPS 端点，并使用启动时从
// Security:ServerCertificate:* 加载的持久化密钥集证书（设计书 23.1）。
// 不依赖 Kestrel:Endpoints 自动加载，避免 appsettings.Production 的旧 HTTP
// 端点与 Turnkey HTTPS 端点合并后双重监听同一个端口。
builder.WebHost.ConfigureKestrel(options =>
{
    // WebApplication.CreateBuilder may install a configuration loader for
    // Kestrel:Endpoints. Turnkey owns the listener below, so discard that
    // loader to prevent an old Production HTTP endpoint from being merged.
    options.ConfigurationLoader = null;
    options.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;

        // 本系统的客户端身份信任源是 client_certificates 表的指纹查表
        // （ClientCertificateAuthenticationHandler.AuthenticateByThumbprintAsync），
        // 它校验指纹归属、证书状态、有效期和客户端状态；私钥持有由 TLS 握手本身证明。
        // 签发用的 CA 由服务端自行生成、只存放在 ProgramData，从不安装进操作系统信任存储。
        //
        // 若保持 ClientCertificateValidation 为 null，Kestrel 的默认行为是
        // 「SslPolicyErrors 非 None 即拒绝」——Agent 证书必然因根不受信而报
        // RemoteCertificateChainErrors，结果是 TLS 握手阶段直接失败（连接重置），
        // 连 401 都返回不了，Turnkey 形态下 Agent 根本连不上。
        // 因此这里显式放行链校验，把身份判定交还给上面的指纹查表。
        https.ClientCertificateValidation = static (_, _, _) => true;
    });

    if (turnkeyServerCertificate is not null)
    {
        options.ListenAnyIP(apiPort, listen => listen.UseHttps(turnkeyServerCertificate));
    }
    else
    {
        options.Listen(IPAddress.Loopback, apiPort);
    }
});

// 反向代理（nginx 终结 TLS/mTLS）：还原真实客户端 IP 与协议。
// KnownProxies 必须显式配置——留空则不采信任何 X-Forwarded-* 头。
var trustedProxies = builder.Configuration
    .GetSection("Security:ClientAuth:ForwardedProxies").Get<string[]>() ?? [];

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in trustedProxies)
    {
        if (IPAddress.TryParse(proxy, out var ip))
            options.KnownProxies.Add(ip);
    }
});

// Controllers + 统一 JSON + 模型校验失败也走统一错误形状
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(e => e.Value is { Errors.Count: > 0 })
            .ToDictionary(
                e => e.Key,
                e => e.Value!.Errors.Select(err => err.ErrorMessage).ToArray());

        var requestId = context.HttpContext.Items.TryGetValue(RequestIdMiddleware.ContextItemKey, out var rid)
            ? rid?.ToString() : null;

        var body = new ApiResponse<object>
        {
            Success = false,
            RequestId = requestId,
            Error = new ApiError
            {
                Code = "VALIDATION_FAILED",
                Message = "请求参数校验失败",
                Details = errors
            }
        };

        return new BadRequestObjectResult(body);
    };
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "轻量级集中备份采集与监控系统 API",
        Version = "v1",
        Description = "BackupMonitor Server API v1（管理端 JWT，Agent 端客户端证书）"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "管理员访问令牌"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// 认证：管理端 JWT + 客户端证书（mTLS）
var jwtSettings = new JwtSettings();
builder.Configuration.GetSection(JwtSettings.SectionName).Bind(jwtSettings);

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // 保留原始 claim 名（sub/username/role/permission）
        options.TokenValidationParameters = jwtSettings.CreateValidationParameters();
    })
    .AddScheme<ClientCertificateAuthenticationOptions, ClientCertificateAuthenticationHandler>(
        ClientCertificateAuthenticationHandler.SchemeName, options => { });

// 授权：16 个权限策略（设计书 4.2 权限编码）
string[] permissionCodes =
[
    "clients.read", "clients.manage",
    "tasks.read", "tasks.manage", "tasks.precheck", "tasks.upload",
    "backups.read", "backups.download", "backups.manage",
    "alerts.read", "alerts.handle",
    "audit.read",
    "system.manage",
    "operations.batch",
    "groups.read", "groups.manage"
];

builder.Services.AddAuthorization(options =>
{
    foreach (var code in permissionCodes)
    {
        options.AddPolicy($"perm:{code}", policy =>
            policy.RequireClaim(JwtClaimTypes.Permission, code));
    }
});

// CORS（管理端）：白名单来源。
// 内置控制台与 API 同源，无需 CORS；此策略只为独立部署的前端预留。
// 留空 = 不放行任何跨域来源（同源访问不受影响）。
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

// 审查 P2-2：此前只有「按账号」的失败锁定，没有任何 IP 维度限速。
// 后果有两面：① 分布式撞库可以每账号 5 次以内横扫全部用户名而不触发任何阈值；
// ② 攻击者可以主动打满某个管理员的失败次数，把人锁在系统外造成拒绝服务。
// 账号锁定拦不住这两种，因为它们都不依赖「同一账号连续失败」。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // 按对端 IP 分区。取 RemoteIpAddress——UseForwardedHeaders 已在更早的中间件里
    // 用 KnownProxies 白名单还原过真实客户端 IP，未配置代理时它就是 TCP 直连对端。
    options.AddPolicy(AuthController.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            """{"success":false,"error":{"code":"TOO_MANY_REQUESTS","message":"请求过于频繁，请稍后重试"}}""",
            ct);
    };
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("Management", policy =>
    {
        if (allowedOrigins.Length == 0)
        {
            policy.WithOrigins().WithExposedHeaders(RequestIdMiddleware.HeaderName);
            return;
        }

        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders(RequestIdMiddleware.HeaderName);
    });
});

var app = builder.Build();

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (turnkeyServerCertificate is not null)
    {
        Log.Information(
            "Turnkey HTTPS endpoint bound by Kestrel on any interface at port {ApiPort}; no automatic Kestrel endpoints are loaded.",
            apiPort);
    }
    else
    {
        Log.Information("Secure HTTP endpoint bound by Kestrel on loopback at port {ApiPort}.", apiPort);
    }
});

if (turnkeyServerCertificate is not null)
    app.Lifetime.ApplicationStopped.Register(turnkeyServerCertificate.Dispose);

// 中间件管道：
//   对端记录 → 代理头还原 → RequestId → 请求日志 → 异常 → CORS → 认证 → 授权
//
// 请求日志必须位于异常中间件「之外」：否则业务异常先被 Serilog 记成 500 + 堆栈，
// 再由异常中间件转成 401/404/409/422，日志里的状态码与实际响应不符。
app.Use(async (context, next) =>
{
    // UseForwardedHeaders 会把 RemoteIpAddress 改写为真实客户端 IP，
    // 而客户端证书头的可信度判断依赖改写前的 TCP 直连对端，先存下来。
    context.Items[ClientCertificateAuthenticationHandler.PeerAddressItemKey] =
        context.Connection.RemoteIpAddress;
    await next();
});
app.UseForwardedHeaders();

app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
// 审查 P2-3：恢复下载令牌出现在 URL 路径中（/api/v1/downloads/{token}），
// 会被请求日志原文记录——有日志读取权限的人可在 TTL 内重放，直接取走整个备份集。
// 协议形态（路径携带令牌）是为了让浏览器用普通 <a> 直接下载，改成请求头需要一整套
// 一次性会话交换，属于独立的接口变更；这里先切断「令牌落进日志」这条实际泄露路径。
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (httpContext, elapsed, ex) =>
        ex is not null ? Serilog.Events.LogEventLevel.Error
        : httpContext.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
        : Serilog.Events.LogEventLevel.Information;

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        // 下载端点的路径段就是令牌本身，打码后再入日志
        if (path.StartsWith("/api/v1/downloads/", StringComparison.OrdinalIgnoreCase))
            diagnosticContext.Set("RequestPath", "/api/v1/downloads/***");
    };
});
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Secure 形态的 TLS 由 nginx 终结；Turnkey 形态的 Kestrel 端点本身就是 HTTPS，
// 因此这里不做重定向，避免反向代理和本地 HTTPS 之间形成循环。
app.UseCors("Management");

// 内置单页管理控制台（wwwroot）：静态文件匿名可访问，业务接口仍走认证授权
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();

// 强制改密闸门必须位于认证之后（需要 JWT claim）、授权之前（未改密时不应触达任何业务端点）。
// 静态文件在上面已经短路返回，不会经过这里。
app.UseMiddleware<PasswordChangeRequiredMiddleware>();

app.UseAuthorization();
app.MapControllers();

// 健康检查端点
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// 数据库健康检查端点（OPEN-ISSUES #6）：只读连通性自检（SELECT version + 结构可达）。
// 原实现为验证乐观锁会向 system_settings 写探针行（diag_probe）、更新三次再删除，
// 污染生产表审计、异常路径可能残留探针行、并发互相干扰。乐观锁与枚举映射自检
// 已迁入测试工程（DatabaseSelfCheckTests），本端点只保留只读检查。
//
// 仍要求 system.manage 权限（会回显数据库版本与表统计），不匿名开放。
app.MapHealthChecks("/health/db", new HealthCheckOptions
{
    ResponseWriter = static async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            ok = report.Status == HealthStatus.Healthy,
            status = report.Status.ToString(),
            entries = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                error = e.Value.Exception?.Message
            }),
            timestamp = DateTime.UtcNow
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
})
.RequireAuthorization("perm:system.manage");

app.Run();
