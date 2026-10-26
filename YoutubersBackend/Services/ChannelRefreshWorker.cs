using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace YoutubersBackend.Services;

public sealed class ChannelRefreshWorker : BackgroundService
{
    private readonly ChannelCacheService _cache;

    public ChannelRefreshWorker(ChannelCacheService cache)
    {
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _cache.RefreshAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _cache.RefreshAsync();
        }
    }
}