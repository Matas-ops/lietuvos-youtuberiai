using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
//using YoutubersBackend.Services;

namespace YoutubersFrontend;

public class Program
{
    // over HTTPS the __Host- prefix makes browsers reject the cookie when a subdomain or plain HTTP page tries to set it
    private const string SecureAdminCookie = "__Host-admin_session";
    private const string AdminCookie = "admin_session";

    // scripts and styles only from files, nothing inline, so injected markup can't run code
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' https://cdn.jsdelivr.net; " +
        "img-src 'self' data: https:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'none'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.AddHttpClient();

        // the HTTPS proxy (caddy) sits in front, take the real scheme and client address from it
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // only the caddy container is trusted, anyone else reaching this app directly is taken at face value
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in TrustedProxies())
                options.KnownProxies.Add(proxy);
        });

        var app = builder.Build();

        app.UseForwardedHeaders();

        // tells browsers to stick to HTTPS, skipped for localhost and plain HTTP requests
        app.UseHsts();

        app.Use((context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;

            return next(context);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/channels", async (IHttpClientFactory factory) =>
        {
            var backendUrl =
                Environment.GetEnvironmentVariable("BACKEND_URL")
                ?? "http://backend:8080";

            var client = factory.CreateClient();

            var result =
                await client.GetFromJsonAsync<object>(
                    $"{backendUrl}/api/channels");

            return Results.Ok(result);
        });

        app.MapGet("/api/health", async (IHttpClientFactory factory) =>
        {
            var backendUrl =
                Environment.GetEnvironmentVariable("BACKEND_URL")
                ?? "http://backend:8080";

            var client = factory.CreateClient();

            var result =
                await client.GetFromJsonAsync<object>(
                    $"{backendUrl}/api/health");

            return Results.Ok(result);
        });

        // -------------------------
        // Admin endpoints, proxied to the backend
        // -------------------------

        app.MapPost("/api/admin/login", async (HttpContext context, IHttpClientFactory factory) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            if (!IsHttps(context.Request) && !IsLocalhost(context.Request) && !AllowInsecureHttp())
                return Results.Json(new { error = "Administratoriaus prisijungimas leidžiamas tik per HTTPS" }, statusCode: 403);

            if (context.Request.ContentLength > 4096)
                return Results.Json(new { error = "Užklausa per didelė" }, statusCode: 413);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BackendUrl()}/api/admin/login")
            {
                // streamed straight through, the password is never parsed or kept here
                Content = new StreamContent(context.Request.Body)
            };

            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            using var response = await factory.CreateClient().SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return await ToResultAsync(context, response);

            var session = await response.Content.ReadFromJsonAsync<AdminSession>();

            if (session == null)
                return Results.Json(new { error = "Netinkamas serverio atsakymas" }, statusCode: 502);

            // the token lives in an HttpOnly cookie, page scripts can't read it
            var cookieOptions = AdminCookieOptions(context.Request);
            cookieOptions.Expires = session.ExpiresAt;

            context.Response.Cookies.Append(AdminCookieName(context.Request), session.Token, cookieOptions);

            return Results.Ok(new { expiresAt = session.ExpiresAt });
        });

        app.MapGet("/api/admin/status", (HttpContext context, IHttpClientFactory factory) =>
            ForwardAdminAsync(context, factory, HttpMethod.Get, "/api/admin/status"));

        app.MapGet("/api/admin/logs", (HttpContext context, IHttpClientFactory factory, long? afterId) =>
            ForwardAdminAsync(context, factory, HttpMethod.Get,
                $"/api/admin/logs?afterId={(afterId ?? 0).ToString(CultureInfo.InvariantCulture)}"));

        app.MapPost("/api/admin/refresh", (HttpContext context, IHttpClientFactory factory) =>
            ForwardAdminAsync(context, factory, HttpMethod.Post, "/api/admin/refresh"));

        app.MapPost("/api/admin/logout", async (HttpContext context, IHttpClientFactory factory) =>
        {
            var result = await ForwardAdminAsync(context, factory, HttpMethod.Post, "/api/admin/logout");

            context.Response.Cookies.Delete(AdminCookieName(context.Request), AdminCookieOptions(context.Request));

            return result;
        });

        app.Run();
    }

    private static string BackendUrl()
    {
        return Environment.GetEnvironmentVariable("BACKEND_URL") ?? "http://backend:8080";
    }

    // comma separated addresses in TRUSTED_PROXIES, nobody is trusted when it's not set
    private static IPAddress[] TrustedProxies()
    {
        var value = Environment.GetEnvironmentVariable("TRUSTED_PROXIES") ?? "";

        return Array.ConvertAll(
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IPAddress.Parse);
    }

    // the prefixed name needs Secure, so plain HTTP (localhost development) keeps the old name
    private static string AdminCookieName(HttpRequest request)
    {
        return IsHttps(request) ? SecureAdminCookie : AdminCookie;
    }

    // __Host- also requires Path=/ and no Domain
    private static CookieOptions AdminCookieOptions(HttpRequest request)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = IsHttps(request),
            SameSite = SameSiteMode.Strict,
            Path = "/"
        };
    }

    private static async Task<IResult> ForwardAdminAsync(
        HttpContext context,
        IHttpClientFactory factory,
        HttpMethod method,
        string pathAndQuery)
    {
        using var request = new HttpRequestMessage(method, $"{BackendUrl()}{pathAndQuery}");

        if (context.Request.Cookies.TryGetValue(AdminCookieName(context.Request), out var token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await factory.CreateClient().SendAsync(request);

        return await ToResultAsync(context, response);
    }

    private static async Task<IResult> ToResultAsync(HttpContext context, HttpResponseMessage response)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
            context.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        var body = await response.Content.ReadAsStringAsync();

        return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
    }

    // behind the proxy the scheme is already taken from X-Forwarded-Proto by UseForwardedHeaders
    private static bool IsHttps(HttpRequest request)
    {
        return request.IsHttps;
    }

    // plain HTTP is fine when the browser talks to this machine only
    private static bool IsLocalhost(HttpRequest request)
    {
        return request.Host.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }

    private static bool AllowInsecureHttp()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("ADMIN_ALLOW_INSECURE_HTTP"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AdminSession(string Token, DateTimeOffset ExpiresAt);
}
