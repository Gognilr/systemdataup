using System.Net;
using System.Text.Json;
using BackupMonitor.Api.Middleware;
using BackupMonitor.Api.Security;
using BackupMonitor.Core.Entities.System;
using BackupMonitor.Infrastructure;
using BackupMonitor.Infrastructure.Data;
using BackupMonitor.Infrastructure.Security;
using BackupMonitor.Shared.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
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

var builder = WebApplication.CreateBuilder(args);

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

// Kestrel：若直接对外终结 HTTPS，则允许客户端证书（mTLS，设计书 23.1）。
// 当前生产形态由 nginx 终结 TLS/mTLS，Kestrel 只监听回环 HTTP，此配置不生效但保留以支持直连部署。
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureHttpsDefaults(https =>
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate);
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
app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// TLS 由 nginx 终结，此处不做 HTTPS 重定向（Kestrel 只监听回环 HTTP）。
app.UseCors("Management");

// 内置单页管理控制台（wwwroot）：静态文件匿名可访问，业务接口仍走认证授权
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// 健康检查端点
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// 数据库联调诊断端点：EF 模型 ↔ 真实 PostgreSQL 的全面自检
// 1) 原生连接  2) 种子数据读取 + snake_case 枚举转换  3) 乐观锁写入/并发冲突/清理
//
// 会回显数据库版本、角色列表与 admin 账号信息，并向 system_settings 写入探针行，
// 因此要求 system.manage 权限，不能匿名开放。
var defaultConnStr = builder.Configuration.GetConnectionString("Default");

app.MapGet("/health/db", async (AppDbContext db) =>
{
    var report = new Dictionary<string, object?>();
    try
    {
        // 1) 原生连接
        var dbVersion = await db.Database
            .SqlQueryRaw<string>("SELECT version() AS \"Value\"")
            .FirstAsync();
        report["rawConnection"] = dbVersion;

        // 2) 种子数据读取 + 枚举转换（DOMAIN snake_case → C# PascalCase 枚举）
        var roleCodes = await db.Roles.AsNoTracking()
            .OrderBy(r => r.Code).Select(r => r.Code).ToListAsync();
        var templates = await db.BackupTaskTemplates.AsNoTracking()
            .OrderBy(t => t.Code)
            .Select(t => new { t.Code, recognizer = t.RecognizerType.ToString(), hasConfig = t.DefaultConfig != null })
            .ToListAsync();
        var settingsCount = await db.SystemSettings.CountAsync();
        var admin = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == "admin");

        report["seedData"] = new
        {
            roleCodes,
            settingsCount,
            templates,
            adminUser = admin == null ? null : new { admin.Id, admin.Username, status = admin.Status.ToString() }
        };

        // 3) 乐观锁写入测试（一次性探针行，测完即删）
        const string probeKey = "diag_probe";
        var staleProbe = await db.SystemSettings.FirstOrDefaultAsync(s => s.SettingKey == probeKey);
        if (staleProbe != null)
        {
            db.SystemSettings.Remove(staleProbe);
            await db.SaveChangesAsync();
        }

        var probe = new SystemSetting { SettingKey = probeKey, SettingValue = "1", Encrypted = false };
        db.SystemSettings.Add(probe);
        await db.SaveChangesAsync();
        var versionAfterInsert = probe.RowVersion;

        probe.SettingValue = "2";
        await db.SaveChangesAsync();
        var versionAfterFirstUpdate = probe.RowVersion;

        // 第二个上下文模拟并发：先读到旧版本号
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(defaultConnStr).Options;
        await using var ctx2 = new AppDbContext(options);
        var probe2 = await ctx2.SystemSettings.SingleAsync(s => s.SettingKey == probeKey);

        probe.SettingValue = "3";
        await db.SaveChangesAsync(); // 先提交成功，row_version 再 +1
        var versionAfterSecondUpdate = probe.RowVersion;

        bool conflictDetected;
        try
        {
            probe2.SettingValue = "99";
            await ctx2.SaveChangesAsync(); // 持旧版本号，应当冲突
            conflictDetected = false;
        }
        catch (DbUpdateConcurrencyException)
        {
            conflictDetected = true;
        }

        // 清理探针行
        db.SystemSettings.Remove(probe);
        await db.SaveChangesAsync();

        report["optimisticLock"] = new
        {
            versionAfterInsert,
            versionAfterFirstUpdate,
            versionAfterSecondUpdate,
            conflictDetected
        };
        report["ok"] = true;
    }
    catch (Exception ex)
    {
        report["ok"] = false;
        report["error"] = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null)
            report["innerError"] = ex.InnerException.Message;
    }

    var success = report.TryGetValue("ok", out var ok) && ok is true;
    return Results.Json(report, statusCode: success ? 200 : 500);
})
.RequireAuthorization("perm:system.manage");

app.Run();
