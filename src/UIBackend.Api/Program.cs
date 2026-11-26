using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using UIBackend.Api.Security;
using UIBackend.Application.Interfaces;
using UIBackend.Infrastructure.AiDetection;
using UIBackend.Infrastructure.C2pa;
using UIBackend.Infrastructure.Fingerprinting;
using UIBackend.Infrastructure.Forensics;
using UIBackend.Infrastructure.Monitoring;
using UIBackend.Infrastructure.Notifications;
using UIBackend.Infrastructure.Persistence;
using UIBackend.Infrastructure.Storage;
using UIBackend.Infrastructure.SynthId;
using UIBackend.Infrastructure.Verification;

var builder = WebApplication.CreateBuilder(args);

// Configuration parameters
var connectionString = builder.Configuration.GetConnectionString("UIBackendDatabase")
    ?? "Host=localhost;Port=5436;Database=visionx_uibackend;Username=postgres;Password=postgres";

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "https://visionx.samstack.site/auth";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "visionx-app";

// Database Context
builder.Services.AddDbContext<UIBackendDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.MigrationsAssembly("UIBackend.Infrastructure");
    });
});
builder.Services.AddScoped<IUIBackendDbContext>(sp => sp.GetRequiredService<UIBackendDbContext>());

// HTTP Clients
builder.Services.AddHttpClient("JwksClient");
builder.Services.AddHttpClient("NotificationsClient");
builder.Services.AddHttpClient("SynthIdClient");
builder.Services.AddHttpClient("OpenAiClient");
builder.Services.AddHttpClient("MlServiceClient");

// Security & Key Providers
builder.Services.AddSingleton<JwksKeyProvider>();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
});

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwksKeyProvider>((options, jwksProvider) =>
    {
        var validIssuers = new[]
        {
            "https://visionx.samstack.site/auth",
            "https://visionx.samstack.site",
            "visionx-auth-service",
            "https://auth.visionx.io",
            jwtIssuer
        }
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        var validAudiences = new[]
        {
            "visionx-app",
            "VisionX_Ecosystem",
            jwtAudience
        }
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            ValidateAudience = true,
            ValidAudiences = validAudiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            IssuerSigningKeyResolver = (_, _, kid, _) => jwksProvider.ResolveSigningKeys(kid),
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();

// CORS policy
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Domain & Infrastructure Services
builder.Services.AddSingleton<IMediaStorageService, FileSystemMediaStorageService>();
builder.Services.AddSingleton<C2paSigningService>();
builder.Services.AddSingleton<IC2paService>(sp => sp.GetRequiredService<C2paSigningService>());
builder.Services.AddSingleton<IC2paVerificationProvider>(sp => sp.GetRequiredService<C2paSigningService>());
builder.Services.AddSingleton<ISynthIdDetectionProvider, GoogleSynthIdDetectionProvider>();
builder.Services.AddSingleton<IMediaFingerprintProvider, PerceptualHashService>();
builder.Services.AddSingleton<IImageForensicsProvider, ImageForensicsService>();
builder.Services.AddSingleton<IAiDetectionProvider, OpenAiContentProvenanceProvider>();
builder.Services.AddSingleton<IAiDetectionProvider, SynthIdAiDetectionProvider>();
builder.Services.AddScoped<IVerificationOrchestrator, VerificationOrchestrator>();
builder.Services.AddScoped<INotificationServiceClient, VisionXNotificationClient>();
builder.Services.AddSingleton<IFeedUrlValidator, FeedUrlValidator>();
builder.Services.AddSingleton<IStreamCredentialProtector, StreamCredentialProtector>();
builder.Services.AddScoped<IMlServiceClient, MlServiceClient>();
builder.Services.AddScoped<INotificationOutboxService, NotificationOutboxService>();
builder.Services.AddScoped<IAlertClassificationService, AlertClassificationService>();
builder.Services.AddHostedService<NotificationOutboxWorker>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "VisionX UIBackend & Media Authenticity API",
        Version = "v1",
        Description = "Microservice for media capture, C2PA Content Credentials provenance, SynthID detection, storage, sharing, and access tracking."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the RS256 access JWT obtained from VisionX Auth Service."
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
    });
});

var app = builder.Build();

// Automatically apply EF Core migrations / EnsureCreated on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<UIBackendDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }
        logger.LogInformation("Database migration/schema initialization succeeded.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not run MigrateAsync on startup. Attempting EnsureCreated fallback.");
        try
        {
            await db.Database.EnsureCreatedAsync();
        }
        catch (Exception innerEx)
        {
            logger.LogError(innerEx, "Both MigrateAsync and EnsureCreatedAsync failed.");
        }
    }
}

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Handle Cloudflare Tunnel path routing (e.g. visionx.samstack.site/a/)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/a", out var remainingA))
    {
        context.Request.PathBase = "/a";
        context.Request.Path = remainingA.HasValue && !string.IsNullOrEmpty(remainingA.Value) ? remainingA : "/";
    }
    await next();
});

app.UseSwagger(c =>
{
    c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
    {
        var host = httpReq.Headers["X-Forwarded-Host"].FirstOrDefault() ?? httpReq.Host.Value;
        var scheme = httpReq.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? httpReq.Scheme;
        var pathBase = httpReq.PathBase.Value ?? "";
        swaggerDoc.Servers = new List<OpenApiServer>
        {
            new() { Url = $"{scheme}://{host}{pathBase}" }
        };
    });
});
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("v1/swagger.json", "VisionX Media Authenticity API v1");
    c.RoutePrefix = "swagger";
});

app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (HttpContext context) =>
{
    var target = context.Request.PathBase.HasValue ? $"{context.Request.PathBase}/swagger" : "/swagger";
    return Results.Redirect(target);
});

app.MapControllers();

app.Run();
