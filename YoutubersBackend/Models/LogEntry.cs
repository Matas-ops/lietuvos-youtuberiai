using System;

namespace YoutubersBackend.Models;

public sealed record LogEntry(
    long Id,
    DateTimeOffset Time,
    string Level,
    string Category,
    string Message,
    string? Exception);
