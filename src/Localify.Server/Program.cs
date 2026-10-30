using System.Net;
using System.Threading.RateLimiting;
using Localify.Server;
using Localify.Server.Admin;
using Localify.Server.Api;
using Localify.Server.Auth;
using Localify.Server.Data;
using Localify.Server.Jobs;
using Localify.Server.Library;
using Localify.Server.Search;
using Localify.Server.Streaming;
using Localify.Server.Youtube;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<LocalifyOptions>()
    .Bind(builder.Configuration.GetSection(LocalifyOptions.Section))
    .PostConfigure(o =>
    {
        o.DataPath = Path.GetFullPath(o.DataPath);
        o.UploadPath = Path.GetFullPath(o.UploadPath);
        Directory.CreateDirectory(o.DataPath);
        Directory.CreateDirectory(o.UploadPath);
    });

builder.Services.AddDbContext<LocalifyDbContext>((sp, db) =>
    db.UseSqlite($"Data Source={sp.GetRequiredService<IOptions<LocalifyOptions>>().Value.DatabasePath}"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<InitialAdminSeeder>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddSingleton<MetadataReader>();
builder.Services.AddSingleton<CoverLocator>();
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanCoordinator>());
builder.Services.AddSingleton<FfmpegLocator>();
builder.Services.AddSingleton<TranscodeService>();
builder.Services.AddSingleton<CoverService>();
builder.Services.AddSingleton<UploadService>();
ElasticsearchClient.ConfigureHttp(builder.Services.AddHttpClient(ElasticsearchClient.HttpClientName));
builder.Services.AddSingleton<ElasticsearchClient>();
builder.Services.AddSingleton<SearchGateway>();
builder.Services.AddSingleton<SearchIndexer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SearchIndexer>());
builder.Services.AddScoped<SearchService>();

builder.Services.AddHttpClient(YoutubeTools.HttpClientName, http =>
{
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Localify/1.0");
    http.Timeout = TimeSpan.FromMinutes(5);
});
MusicBrainzClient.ConfigureHttp(builder.Services.AddHttpClient(MusicBrainzClient.HttpClientName));
builder.Services.AddSingleton<YoutubeTools>();
builder.Services.AddSingleton<YoutubeSettingsStore>();
builder.Services.AddSingleton<YtDlp>();
builder.Services.AddSingleton<MusicBrainzClient>();
builder.Services.AddSingleton<MetadataResolver>();
builder.Services.AddSingleton<YoutubeLibraryWriter>();
builder.Services.AddSingleton<YoutubeImporter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<YoutubeImporter>());
builder.Services.AddSingleton<YoutubeSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<YoutubeSyncService>());

// Gestionnaire de tâches : un consommateur par file, types de tâches déclarés par leurs exécutants.
builder.Services.AddSingleton<JobEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobEngine>());
builder.Services.AddSingleton<IJobHandler, YoutubeDownloadJob>();
builder.Services.AddSingleton<IJobHandler, AcoustIdIdentifyJob>();
builder.Services.AddSingleton<IJobHandler, YoutubeMetadataJob>();
builder.Services.AddSingleton<IJobHandler, AcoustIdRescanJob>();
builder.Services.AddSingleton<IJobHandler, LibraryMatchJob>();

ConfigureAuthentication(builder.Services);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<LocalifyOptions>>().Value.LoginAttemptsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Derrière cloudflared, le proxy est un conteneur voisin dont l'IP n'est pas fixe.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Messages d'erreur dans la langue du client (Accept-Language, envoyé par l'interface) : voir Tx.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    var language = Localify.Contracts.Localization.Translations.Instance.Resolve(ctx.HttpContext.Request.Headers.AcceptLanguage.ToString());
    var t = Localify.Contracts.Localization.Translations.Instance;
    if (ctx.ProblemDetails.Detail is { } detail) ctx.ProblemDetails.Detail = Localify.Contracts.Localization.Tx.Translate(t, language, detail);
});
builder.Services.AddHealthChecks().AddDbContextCheck<LocalifyDbContext>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Morceaux d'upload de 8 Mo + marge.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16 * 1024 * 1024);

var app = builder.Build();

await InitializeAsync(app);

var localify = app.Services.GetRequiredService<IOptions<LocalifyOptions>>().Value;
if (localify.BehindProxy)
{
    app.UseForwardedHeaders();
    app.Use(CloudflareClientIp);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(SecurityHeaders);

app.MapStaticAssets();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapAuthEndpoints();
app.MapLibraryEndpoints();
app.MapStreamEndpoints();
app.MapMeEndpoints();
app.MapPlaylistEndpoints();
app.MapAdminEndpoints();
app.MapYoutubeEndpoints();
app.MapJobEndpoints();
app.MapExtensionEndpoints();
app.MapDashboardEndpoints();

app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();

static void ConfigureAuthentication(IServiceCollection services)
{
    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { })
        .AddJwtBearer(AuthConstants.MediaScheme, o => o.Events = new JwtBearerEvents
        {
            // Les lecteurs (<audio>, <img>, ExoPlayer, Android Auto) passent le jeton média en query.
            OnMessageReceived = ctx =>
            {
                if (ctx.Request.Query.TryGetValue("t", out var t)) ctx.Token = t;
                return Task.CompletedTask;
            },
        })
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthConstants.ApiKeyScheme, _ => { });

    services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<SigningKeyProvider>((o, keys) =>
        Configure(o, keys, AuthConstants.ApiAudience));
    services.AddOptions<JwtBearerOptions>(AuthConstants.MediaScheme).Configure<SigningKeyProvider>((o, keys) =>
        Configure(o, keys, AuthConstants.MediaAudience));

    services.AddAuthorizationBuilder()
        .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser().Build())
        .AddPolicy(AuthConstants.AdminPolicy, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser().RequireClaim("role", Localify.Contracts.Roles.Admin))
        .AddPolicy(AuthConstants.ExtensionPolicy, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AuthConstants.ApiKeyScheme)
            .RequireAuthenticatedUser().RequireAssertion(c =>
                c.User.HasClaim("role", Localify.Contracts.Roles.Admin) ||
                c.User.HasClaim(ApiKeyAuthenticationHandler.KindClaim, nameof(ApiKeyKind.BrowserExtension))))
        .AddPolicy(AuthConstants.MediaPolicy, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AuthConstants.MediaScheme)
            .RequireAuthenticatedUser());

    static void Configure(JwtBearerOptions o, SigningKeyProvider keys, string audience)
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = AuthConstants.Issuer,
            ValidAudience = audience,
            IssuerSigningKey = keys.Key,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "unique_name",
            RoleClaimType = "role",
        };
        o.Events ??= new JwtBearerEvents();
        o.Events.OnTokenValidated = ValidateSecurityStampAsync;
    }
}

// Un changement de mot de passe ou de rôle invalide les jetons encore valides (cache d'une minute).
static async Task ValidateSecurityStampAsync(TokenValidatedContext ctx)
{
    var sub = ctx.Principal?.FindFirst("sub")?.Value;
    var stamp = ctx.Principal?.FindFirst(AuthConstants.SecurityStampClaim)?.Value;
    if (sub is null || stamp is null || !Guid.TryParse(sub, out var userId))
    {
        ctx.Fail("Jeton incomplet");
        return;
    }
    var cache = ctx.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
    var current = await cache.GetOrCreateAsync($"sstamp:{userId}", async entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
        var db = ctx.HttpContext.RequestServices.GetRequiredService<LocalifyDbContext>();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).FirstOrDefaultAsync();
    });
    if (current != stamp) ctx.Fail("Session révoquée");
}

static async Task InitializeAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LocalifyDbContext>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await scope.ServiceProvider.GetRequiredService<InitialAdminSeeder>().SeedAsync(CancellationToken.None);
}

// Cloudflare fournit l'IP réelle du client dans CF-Connecting-IP.
static Task CloudflareClientIp(HttpContext http, Func<Task> next)
{
    if (http.Request.Headers.TryGetValue("CF-Connecting-IP", out var ip) && IPAddress.TryParse(ip, out var address))
        http.Connection.RemoteIpAddress = address;
    return next();
}

static Task SecurityHeaders(HttpContext http, Func<Task> next)
{
    var h = http.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "same-origin";
    h.XFrameOptions = "DENY";
    if (!http.Request.Path.StartsWithSegments("/api"))
    {
        // Miniatures YouTube affichées dans l'import (admin).
        h.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data: blob: https://i.ytimg.com https://*.googleusercontent.com https://*.ggpht.com; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'";
    }
    return next();
}

public partial class Program;
