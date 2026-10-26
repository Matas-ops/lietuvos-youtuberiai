using CsvHelper.Configuration;
using YoutubersBackend.DTOs.GoogleSheets;

namespace YoutubersBackend.DTOs.GoogleSheets;

public sealed class GoogleSheetChannelRowMapper : ClassMap<GoogleSheetChannelRow>
{
    public GoogleSheetChannelRowMapper()
    {
        Map(x => x.ChannelId).Name("ID");
        Map(x => x.Tags).Name("žymos");
    }
}