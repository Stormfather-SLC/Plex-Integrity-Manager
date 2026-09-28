using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PIM.Core.Models;
using PIM.Infrastructure.Parsing;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

/// <summary>
/// The existing Plex copy's resolution and size, and the new file's
/// resolution from its name, are shown side by side so the user can judge a
/// possible duplicate. All read-only; Plex is stubbed.
/// </summary>
public sealed class PlexMediaDetailsTests
{
    [Theory]
    [InlineData("4k", "4K")]
    [InlineData("1080", "1080p")]
    [InlineData("720", "720p")]
    [InlineData("sd", "SD")]
    [InlineData(null, null)]
    public void FromPlex_NormalizesPlexResolutionValues(string? plex, string? expected)
    {
        Assert.Equal(expected, VideoResolution.FromPlex(plex));
    }

    [Theory]
    [InlineData("Dune.2021.2160p.WEB-DL.mkv", "4K")]
    [InlineData("Dune 2021 UHD BluRay.mkv", "4K")]
    [InlineData("Heat.1995.1080i.HDTV.mkv", "1080p")]
    [InlineData("Alien.1979.720p.mkv", "720p")]
    [InlineData("Inception (2010) {imdb-tt1375666}.mkv", null)]
    [InlineData("The.4000.Hours.mkv", null)]
    public void FromFileName_ReadsOnlyExplicitResolutionTags(string fileName, string? expected)
    {
        Assert.Equal(expected, VideoResolution.FromFileName(fileName));
    }

    [Theory]
    [InlineData(8_804_682_956L, "8.2 GB")]
    [InlineData(734_003_200L, "700 MB")]
    [InlineData(null, "size unknown")]
    [InlineData(0L, "size unknown")]
    public void FormatSize_IsReadable(long? bytes, string expected)
    {
        Assert.Equal(expected, VideoResolution.FormatSize(bytes));
    }

    [Fact]
    public void Parser_KeepsTheResolutionHintAndStillStripsItFromTheTitle()
    {
        var movie = new Movie { FileName = "Inception.2010.2160p.UHD.mkv" };

        new FileNameParser().Parse(movie);

        Assert.Equal("4K", movie.SourceResolution);
        Assert.Equal("Inception", movie.Title);
    }

    [Theory]
    [InlineData("\"1080\"")]
    [InlineData("1080")]
    public void PlexConflict_CarriesTheExistingCopysResolutionAndSize(string videoResolutionJson)
    {
        var service = CreateService(
            $$"""
            {
              "MediaContainer": {
                "totalSize": 1,
                "Metadata": [
                  {
                    "title": "Inception",
                    "year": 2010,
                    "Media": [
                      {
                        "videoResolution": {{videoResolutionJson}},
                        "Part": [
                          {
                            "file": "M:\\Movies\\Inception (2010) {imdb-tt1375666}\\Inception (2010) {imdb-tt1375666}.mkv",
                            "size": 8804682956
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
            }
            """);

        var result = service.Check(new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666",
            TargetPath = @"D:\New\Inception (2010) {imdb-tt1375666}\Inception (2010) {imdb-tt1375666}.mkv"
        });

        Assert.Equal(PlexLibraryConflictType.SameImdbIdDifferentPath, result.ConflictType);
        Assert.Equal("1080p", result.ExistingResolution);
        Assert.Equal(8_804_682_956L, result.ExistingSizeBytes);
    }

    [Fact]
    public void PlexConflict_WithoutMediaDetails_StillReportsTheConflict()
    {
        var service = CreateService(
            """
            {
              "MediaContainer": {
                "totalSize": 1,
                "Metadata": [
                  {
                    "title": "Inception",
                    "year": 2010,
                    "Media": [ { "Part": [ { "file": "M:\\Movies\\Inception (2010) {imdb-tt1375666}.mkv" } ] } ]
                  }
                ]
              }
            }
            """);

        var result = service.Check(new Movie
        {
            Title = "Inception",
            Year = 2010,
            ImdbId = "tt1375666",
            TargetPath = @"D:\New\Inception (2010) {imdb-tt1375666}\Inception (2010) {imdb-tt1375666}.mkv"
        });

        Assert.True(result.HasConflict);
        Assert.Null(result.ExistingResolution);
        Assert.Null(result.ExistingSizeBytes);
    }

    private static PlexLibraryConflictService CreateService(string libraryJson)
    {
        const string sectionsJson = """
            { "MediaContainer": { "Directory": [ { "key": "1", "type": "movie", "title": "Movies" } ] } }
            """;

        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            return path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase)
                ? Json(sectionsJson)
                : path.Equals("/library/sections/1/all", StringComparison.OrdinalIgnoreCase)
                    ? Json(libraryJson)
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plex:Enabled"] = "true",
                ["Plex:Token"] = "stub-token",
                ["Plex:BaseUrl"] = "http://localhost:32400"
            })
            .Build();

        return new PlexLibraryConflictService(
            new HttpClient(handler),
            configuration,
            NullLogger<PlexLibraryConflictService>.Instance);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(_handler(request));
        }
    }
}
