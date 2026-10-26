using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YoutubersBackend.Models;

namespace YoutubersBackend.Services;

/// <summary>
/// Checks the admin password and keeps admin sessions.
/// The plain password is never kept: the configured one is turned into a salted hash at startup,
/// a submitted one only lives in a buffer that is wiped right after the check.
/// </summary>
public sealed class AdminAuthService
{
    public const string PasswordVariable = "ADMIN_PASSWORD";

    private const int MaxBodyBytes = 4096;
    private const int HashIterations = 100_000;
    private const int HashBytes = 32;

    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);

    private readonly ILogger<AdminAuthService> _logger;
    private readonly TimeSpan _sessionLifetime;
    private readonly TimeSpan _loginCooldown;
    private readonly int _globalAttemptsPerMinute;

    // client -> time of its next allowed attempt, plus the times of everyone's attempts in the last minute
    private readonly Lock _attemptsLock = new();
    private readonly Dictionary<string, DateTimeOffset> _nextAttemptAt = new();
    private readonly Queue<DateTimeOffset> _recentAttempts = new();
    private DateTimeOffset _nextCleanupAt;

    // random per process, so the hash is useless outside of this run
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[]? _passwordHash;

    // token hash -> expiry, raw tokens are only known to the client
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    public AdminAuthService(IConfiguration configuration, ILogger<AdminAuthService> logger)
    {
        _logger = logger;

        _sessionLifetime = TimeSpan.FromMinutes(configuration.GetValue("Admin:SessionMinutes", 60));

        // each client waits between attempts, plus a global cap for attackers spread over many addresses
        _loginCooldown = TimeSpan.FromSeconds(configuration.GetValue("Admin:LoginCooldownSeconds", 15));
        _globalAttemptsPerMinute = configuration.GetValue("Admin:GlobalLoginAttemptsPerMinute", 20);

        _passwordHash = HashConfiguredPassword(configuration);

        if (_passwordHash == null)
            _logger.LogWarning("{Variable} is not set, admin login is disabled", PasswordVariable);
    }

    public bool IsEnabled => _passwordHash != null;

    /// <summary>
    /// Counts a login attempt from <paramref name="clientIp"/>, or refuses it with how long to wait.
    /// </summary>
    public bool TryStartLoginAttempt(string clientIp, out TimeSpan retryAfter)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_attemptsLock)
        {
            RemoveFinishedCooldowns(now);

            if (_nextAttemptAt.TryGetValue(clientIp, out var allowedAt) && allowedAt > now)
            {
                retryAfter = allowedAt - now;
                return false;
            }

            // the wait starts with every attempt, also one the global cap turns away
            _nextAttemptAt[clientIp] = now + _loginCooldown;

            while (_recentAttempts.TryPeek(out var oldest) && oldest <= now - AttemptWindow)
                _recentAttempts.Dequeue();

            if (_recentAttempts.Count >= _globalAttemptsPerMinute)
            {
                var globalWait = _recentAttempts.Peek() + AttemptWindow - now;

                retryAfter = globalWait > _loginCooldown ? globalWait : _loginCooldown;
                return false;
            }

            _recentAttempts.Enqueue(now);

            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>
    /// Reads <c>{"password": "..."}</c> from the request body and opens a session when it matches.
    /// </summary>
    public async Task<AdminSession?> TryLoginAsync(Stream body, CancellationToken cancellationToken)
    {
        if (_passwordHash == null)
            return null;

        var json = ArrayPool<byte>.Shared.Rent(MaxBodyBytes);
        var password = ArrayPool<byte>.Shared.Rent(MaxBodyBytes);

        try
        {
            var length = await ReadBodyAsync(body, json, cancellationToken);

            if (length <= 0)
                return null;

            var passwordLength = ReadPassword(json.AsSpan(0, length), password);

            if (passwordLength <= 0)
                return null;

            var hash = Hash(password.AsSpan(0, passwordLength));

            if (!CryptographicOperations.FixedTimeEquals(hash, _passwordHash))
                return null;

            return CreateSession();
        }
        finally
        {
            // wipe every copy of the submitted password
            ArrayPool<byte>.Shared.Return(json, clearArray: true);
            ArrayPool<byte>.Shared.Return(password, clearArray: true);
        }
    }

    public bool IsAuthorized(HttpRequest request)
    {
        var key = GetTokenKey(request);

        if (key == null || !_sessions.TryGetValue(key, out var expiresAt))
            return false;

        if (expiresAt > DateTimeOffset.UtcNow)
            return true;

        _sessions.TryRemove(key, out _);

        return false;
    }

    public void Logout(HttpRequest request)
    {
        var key = GetTokenKey(request);

        if (key != null)
            _sessions.TryRemove(key, out _);
    }

    private AdminSession CreateSession()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.Add(_sessionLifetime);

        foreach (var (key, expiry) in _sessions)
            if (expiry <= DateTimeOffset.UtcNow)
                _sessions.TryRemove(key, out _);

        _sessions[HashToken(token)] = expiresAt;

        return new AdminSession(token, expiresAt);
    }

    private byte[]? HashConfiguredPassword(IConfiguration configuration)
    {
        var password = Environment.GetEnvironmentVariable(PasswordVariable);

        // drop the copies the app holds, so child processes (chromium) don't inherit it either
        Environment.SetEnvironmentVariable(PasswordVariable, null);
        configuration[PasswordVariable] = null;

        if (string.IsNullOrEmpty(password))
            return null;

        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(password.Length));

        try
        {
            var length = Encoding.UTF8.GetBytes(password, bytes);

            return Hash(bytes.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    private byte[] Hash(ReadOnlySpan<byte> password)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, _salt, HashIterations, HashAlgorithmName.SHA256, HashBytes);
    }

    private static async Task<int> ReadBodyAsync(Stream body, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < MaxBodyBytes)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total, MaxBodyBytes - total), cancellationToken);

            if (read == 0)
                return total;

            total += read;
        }

        // body is larger than any sane login request
        return -1;
    }

    private static int ReadPassword(ReadOnlySpan<byte> json, Span<byte> destination)
    {
        try
        {
            var reader = new Utf8JsonReader(json);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return -1;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isPassword = reader.ValueTextEquals("password"u8);

                if (!reader.Read())
                    return -1;

                // copied straight into the byte buffer, no string is created
                if (isPassword && reader.TokenType == JsonTokenType.String)
                    return reader.CopyString(destination);

                reader.Skip();
            }
        }
        catch (JsonException)
        {
        }

        return -1;
    }

    private static string? GetTokenKey(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            return null;

        var token = header["Bearer ".Length..].Trim();

        return token.Length == 0 ? null : HashToken(token);
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    // forget clients whose wait is over, so the table doesn't keep every address ever seen
    private void RemoveFinishedCooldowns(DateTimeOffset now)
    {
        if (now < _nextCleanupAt)
            return;

        _nextCleanupAt = now + _loginCooldown;

        foreach (var (client, allowedAt) in _nextAttemptAt)
            if (allowedAt <= now)
                _nextAttemptAt.Remove(client);
    }
}
