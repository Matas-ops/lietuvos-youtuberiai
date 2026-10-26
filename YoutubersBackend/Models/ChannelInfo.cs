namespace YoutubersBackend.Models;

public sealed class ChannelInfo
{
    public required string ChannelId { get; init; }
    public required string Name { get; init; }
    public required string IconUrl { get; init; }
    public required string[] Tags { get; init; }
}