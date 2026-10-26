using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CsvHelper;
using Microsoft.Extensions.Configuration;
using YoutubersBackend.DTOs.GoogleSheets;

namespace YoutubersBackend.Services;

public sealed class GoogleSheetsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public GoogleSheetsService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<GoogleSheetChannelRow>> GetChannelsAsync()
    {
        var csvUrl = _configuration["GoogleSheets:CsvUrl"]!;

        var csv = await _httpClient.GetStringAsync(csvUrl);

        using var reader = new StringReader(csv);
        using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);

        csvReader.Context.RegisterClassMap<GoogleSheetChannelRowMapper>();

        return csvReader.GetRecords<GoogleSheetChannelRow>().ToList();
    }
}