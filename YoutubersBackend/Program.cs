using System;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YoutubersBackend.Logging;
using YoutubersBackend.Services;

namespace YoutubersBackend;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        // recent logs are kept in memory for the admin page
        var logStore = new InMemoryLogStore(capacity: 2000);
        builder.Services.AddSingleton(logStore);
        builder.Logging.AddProvider(new InMemoryLoggerProvider(logStore));

        // HTTP client for Google Sheets calls
        builder.Services.AddHttpClient();

        // Application services
        builder.Services.AddSingleton<GoogleSheetsService>();
        builder.Services.AddSingleton<YoutubeScraperService>();
        builder.Services.AddSingleton<ChannelCacheService>();
        builder.Services.AddSingleton<AdminAuthService>();
        builder.Services.AddHostedService<ChannelRefreshWorker>();

        // the client address comes from X-Forwarded-For, but only when the frontend container sent it
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in TrustedProxies())
                options.KnownProxies.Add(proxy);
        });

        var app = builder.Build();

        app.UseForwardedHeaders();

        // hash the admin password and drop it from the environment before anything else starts
        app.Services.GetRequiredService<AdminAuthService>();

        // -------------------------
        // API endpoints
        // -------------------------

        app.MapGet("/api/health", () =>
        {
            return Results.Ok(new
            {
                status = "Healthy",
                time = DateTimeOffset.UtcNow
            });
        });

        app.MapGet("/api/channels", (ChannelCacheService cache) =>
        {
            return Results.Ok(new
            {
                totalChannels = cache.Channels.Count,
                lastRefresh = cache.LastRefresh,
                isRefreshing = cache.IsRefreshing,
                progress = new
                {
                    scraped = cache.ScrapedCount,
                    total = cache.TotalCount
                },
                channels = cache.Channels
            });
        });

        // -------------------------
        // Admin endpoints
        // -------------------------

        app.MapPost("/api/admin/login", async (HttpContext context, AdminAuthService auth, ILogger<Program> logger) =>
        {
            if (!auth.IsEnabled)
                return Results.Json(new { error = "Administratoriaus prisijungimas išjungtas: nenustatytas ADMIN_PASSWORD" }, statusCode: 503);

            var clientIp = GetClientIp(context);

            if (!auth.TryStartLoginAttempt(clientIp, out var retryAfter))
            {
                context.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                // debug only, so hammering the endpoint can't flood the log; real password checks are logged below
                logger.LogDebug("Admin login attempt from {ClientIp} refused, retry after {RetryAfter}", clientIp, retryAfter);

                return Results.Json(new { error = "Palaukite prieš bandydami prisijungti dar kartą" }, statusCode: 429);
            }

            var session = await auth.TryLoginAsync(context.Request.Body, context.RequestAborted);

            if (session == null)
            {
                logger.LogWarning("Failed admin login from {ClientIp}", clientIp);

                return Results.Json(new { error = "Neteisingas slaptažodis" }, statusCode: 401);
            }

            logger.LogInformation("Admin logged in from {ClientIp}", clientIp);

            return Results.Ok(session);
        });

        var admin = app.MapGroup("/api/admin").AddEndpointFilter(async (context, next) =>
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<AdminAuthService>();

            if (!auth.IsAuthorized(context.HttpContext.Request))
                return Results.Json(new { error = "Neprisijungta" }, statusCode: 401);

            return await next(context);
        });

        admin.MapGet("/status", (ChannelCacheService cache) =>
        {
            return Results.Ok(new
            {
                totalChannels = cache.Channels.Count,
                lastRefresh = cache.LastRefresh,
                isRefreshing = cache.IsRefreshing,
                progress = new
                {
                    scraped = cache.ScrapedCount,
                    total = cache.TotalCount
                }
            });
        });

        // a full refresh takes minutes, so it runs in the background and progress is read from /status
        admin.MapPost("/refresh", (ChannelCacheService cache, ILogger<Program> logger) =>
        {
            if (cache.IsRefreshing)
                return Results.Json(new { error = "Atnaujinimas jau vykdomas" }, statusCode: 409);

            logger.LogInformation("Manual channel refresh triggered by admin");

            _ = Task.Run(async () =>
            {
                try
                {
                    await cache.RefreshAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Manual channel refresh failed");
                }
            });

            return Results.Accepted(value: new { status = "Started" });
        });

        admin.MapGet("/logs", (InMemoryLogStore logs, long? afterId) =>
        {
            return Results.Ok(logs.GetAfter(afterId ?? 0));
        });

        admin.MapPost("/logout", (HttpContext context, AdminAuthService auth) =>
        {
            auth.Logout(context.Request);

            return Results.Ok(new { status = "LoggedOut" });
        });

        app.Run();
    }

    // already the real client address when the request came through the frontend, see UseForwardedHeaders
    private static string GetClientIp(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;

        if (address == null)
            return "unknown";

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    // comma separated addresses in TRUSTED_PROXIES, nobody is trusted when it's not set
    private static IPAddress[] TrustedProxies()
    {
        var value = Environment.GetEnvironmentVariable("TRUSTED_PROXIES") ?? "";

        return Array.ConvertAll(
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IPAddress.Parse);
    }
}
