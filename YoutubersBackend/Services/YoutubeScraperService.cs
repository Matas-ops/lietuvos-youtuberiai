using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using YoutubersBackend.DTOs.Youtube;

namespace YoutubersBackend.Services;

public sealed class YoutubeScraperService
{
    // avatar urls look like ".../xyz=s900-c-k-c0x00ffffff-no-rj", the size part is swappable
    private static readonly Regex AvatarSizeRegex = new(@"=s\d+", RegexOptions.Compiled);

    private readonly IConfiguration _configuration;
    private readonly ILogger<YoutubeScraperService> _logger;

    public YoutubeScraperService(IConfiguration configuration, ILogger<YoutubeScraperService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Scrapes channels with several browsers in parallel, <paramref name="onScraped"/> is called
    /// from worker threads after every channel, with null page when the channel could not be scraped.
    /// </summary>
    public Task ScrapeChannelsAsync(
        IReadOnlyCollection<string> channelIds,
        Action<string, YoutubeChannelPage?> onScraped,
        CancellationToken cancellationToken = default)
    {
        if (channelIds.Count == 0)
            return Task.CompletedTask;

        var queue = new ConcurrentQueue<string>(channelIds);

        var parallelism = Math.Clamp(
            _configuration.GetValue("Selenium:Parallelism", 2), 1, channelIds.Count);

        // Selenium is fully synchronous, every browser gets its own dedicated thread
        var workers = Enumerable
            .Range(0, parallelism)
            .Select(_ => Task.Factory.StartNew(
                () => ScrapeQueue(queue, onScraped, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default));

        return Task.WhenAll(workers);
    }

    private void ScrapeQueue(
        ConcurrentQueue<string> queue,
        Action<string, YoutubeChannelPage?> onScraped,
        CancellationToken cancellationToken)
    {
        // one browser per worker, reused for every channel it takes from the queue
        using var driver = CreateDriver();

        SkipConsent(driver);

        while (queue.TryDequeue(out var channelId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            YoutubeChannelPage? page = null;

            try
            {
                page = ScrapeChannel(driver, channelId);
            }
            catch (WebDriverException ex)
            {
                _logger.LogWarning(ex, "Failed to scrape YouTube channel {ChannelId}", channelId);
            }

            onScraped(channelId, page);
        }
    }

    private YoutubeChannelPage? ScrapeChannel(IWebDriver driver, string channelId)
    {
        driver.Navigate().GoToUrl($"https://www.youtube.com/channel/{Uri.EscapeDataString(channelId)}");

        // consent cookie got rejected or expired, click through the page instead
        if (driver.Url.Contains("consent.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            driver.FindElements(By.CssSelector("form[action*='consent'] button")).FirstOrDefault()?.Click();
            driver.Navigate().GoToUrl($"https://www.youtube.com/channel/{Uri.EscapeDataString(channelId)}");
        }

        // channel name and avatar are server rendered as Open Graph meta tags
        var title = GetMetaContent(driver, "og:title");

        if (string.IsNullOrWhiteSpace(title))
            return null;

        var avatar = GetMetaContent(driver, "og:image") ?? "";

        return new YoutubeChannelPage
        {
            Title = title,
            AvatarUrl = AvatarSizeRegex.Replace(avatar, "=s88", 1)
        };
    }

    private static string? GetMetaContent(IWebDriver driver, string property)
    {
        return driver
            .FindElements(By.CssSelector($"meta[property='{property}']"))
            .FirstOrDefault()?
            .GetDomAttribute("content")?
            .Trim();
    }

    private static void SkipConsent(IWebDriver driver)
    {
        // cookies can only be set for the domain currently open
        driver.Navigate().GoToUrl("https://www.youtube.com/robots.txt");

        driver.Manage().Cookies.AddCookie(
            new Cookie("SOCS", "CAI", ".youtube.com", "/", DateTime.UtcNow.AddYears(1)));
    }

    private ChromeDriver CreateDriver()
    {
        var options = new ChromeOptions
        {
            // meta tags are in the initial html, no need to wait for scripts and images
            PageLoadStrategy = PageLoadStrategy.Eager
        };

        var binaryPath = _configuration["Selenium:ChromeBinaryPath"];

        if (!string.IsNullOrWhiteSpace(binaryPath))
            options.BinaryLocation = binaryPath;

        if (_configuration.GetValue("Selenium:Headless", true))
            options.AddArgument("--headless=new");

        options.AddArguments(
            "--no-sandbox",
            "--disable-dev-shm-usage",
            "--disable-gpu",
            "--lang=en-US",
            "--blink-settings=imagesEnabled=false");

        var driverPath = _configuration["Selenium:ChromeDriverPath"];

        // without an explicit path Selenium Manager resolves a matching driver itself
        var service = string.IsNullOrWhiteSpace(driverPath)
            ? ChromeDriverService.CreateDefaultService()
            : ChromeDriverService.CreateDefaultService(driverPath);

        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;

        var driver = new ChromeDriver(service, options);

        driver.Manage().Timeouts().PageLoad =
            TimeSpan.FromSeconds(_configuration.GetValue("Selenium:PageLoadTimeoutSeconds", 30));

        return driver;
    }
}
