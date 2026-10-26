using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubersBackend.DTOs.GoogleSheets;
using YoutubersBackend.DTOs.Youtube;
using YoutubersBackend.Models;

namespace YoutubersBackend.Services;

public sealed class ChannelCacheService
{
    private readonly GoogleSheetsService _sheets;
    private readonly YoutubeScraperService _scraper;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private List<ChannelInfo> _channels = [];
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private bool _isRefreshing;
    private int _scrapedCount;
    private int _totalCount;

    public ChannelCacheService(
        GoogleSheetsService sheets,
        YoutubeScraperService scraper)
    {
        _sheets = sheets;
        _scraper = scraper;
    }

    public IReadOnlyList<ChannelInfo> Channels => _channels;

    public DateTimeOffset LastRefresh => _lastRefresh;

    public bool IsRefreshing => _isRefreshing;

    public int ScrapedCount => _scrapedCount;

    public int TotalCount => _totalCount;

    public async Task RefreshAsync()
    {
        // prevent concurrent refreshes
        if (!await _lock.WaitAsync(0))
            return;

        try
        {
            _isRefreshing = true;

            var rows = await _sheets.GetChannelsAsync();

            var entries = new List<(string ChannelId, string[] Tags)>(rows.Count);

            foreach (var row in rows)
            {
                var channelId = row.ChannelId?.Trim();

                if (string.IsNullOrWhiteSpace(channelId))
                    continue;

                var tags = row.Tags?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    ?? [];

                entries.Add((channelId, tags));
            }

            var channelIds = entries
                .Select(entry => entry.ChannelId)
                .Distinct()
                .ToList();

            // channels not scraped yet in this run keep their data from the previous refresh
            var previous = new Dictionary<string, ChannelInfo>();

            foreach (var channel in _channels)
                previous.TryAdd(channel.ChannelId, channel);

            var scraped = new Dictionary<string, YoutubeChannelPage?>(channelIds.Count);

            _scrapedCount = 0;
            _totalCount = channelIds.Count;

            await _scraper.ScrapeChannelsAsync(channelIds, (channelId, page) =>
            {
                // called from several scraper threads, publish a new snapshot after every channel
                lock (scraped)
                {
                    scraped[channelId] = page;
                    _scrapedCount = scraped.Count;
                    _channels = BuildChannels(entries, scraped, previous);
                }
            });

            _channels = BuildChannels(entries, scraped, previous);
            _lastRefresh = DateTimeOffset.UtcNow;
        }
        finally
        {
            _isRefreshing = false;
            _lock.Release();
        }
    }

    private static List<ChannelInfo> BuildChannels(
        List<(string ChannelId, string[] Tags)> entries,
        Dictionary<string, YoutubeChannelPage?> scraped,
        Dictionary<string, ChannelInfo> previous)
    {
        var result = new List<ChannelInfo>(entries.Count);

        foreach (var (channelId, tags) in entries)
        {
            if (scraped.TryGetValue(channelId, out var page))
            {
                // scraped in this run but failed, drop it like before
                if (page == null)
                    continue;

                result.Add(new ChannelInfo
                {
                    ChannelId = channelId,
                    Name = string.IsNullOrWhiteSpace(page.Title) ? "Unknown" : page.Title,
                    IconUrl = page.AvatarUrl,
                    Tags = tags
                });
            }
            else if (previous.TryGetValue(channelId, out var old))
            {
                result.Add(new ChannelInfo
                {
                    ChannelId = channelId,
                    Name = old.Name,
                    IconUrl = old.IconUrl,
                    Tags = tags
                });
            }
        }

        return result;
    }
}
