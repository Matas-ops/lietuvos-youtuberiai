using System;

namespace YoutubersBackend.Models;

public sealed record AdminSession(string Token, DateTimeOffset ExpiresAt);
