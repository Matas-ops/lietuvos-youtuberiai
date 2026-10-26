using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using YoutubersBackend.Models;

namespace YoutubersBackend.Logging;

/// <summary>
/// Keeps the most recent log entries in memory so the admin page can show them.
/// </summary>
public sealed class InMemoryLogStore
{
    private readonly int _capacity;
    private readonly Queue<LogEntry> _entries = new();
    private readonly Lock _lock = new();

    private long _lastId;

    public InMemoryLogStore(int capacity)
    {
        _capacity = capacity;
    }

    public void Add(LogLevel level, string category, string message, Exception? exception)
    {
        lock (_lock)
        {
            _entries.Enqueue(new LogEntry(
                ++_lastId,
                DateTimeOffset.UtcNow,
                level.ToString(),
                category,
                message,
                exception?.ToString()));

            if (_entries.Count > _capacity)
                _entries.Dequeue();
        }
    }

    public List<LogEntry> GetAfter(long afterId)
    {
        lock (_lock)
        {
            return _entries.Where(entry => entry.Id > afterId).ToList();
        }
    }
}
