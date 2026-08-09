using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PIM.Core.Models;
using PIM.Infrastructure.Services;
using Xunit;

namespace PIM.Tests;

public sealed class PlexLibraryConflictServiceTests
{
    [Fact]
    public void Check_ExistingPlexEntryAtProposedTarget_BlocksCommit()
    {
        const string targetPath =
            @"G:\[PLEX]\Movies\PG\The Hunt for Red October (1990) {imdb-tt0099810}\The Hunt for Red October (1990) {imdb-tt0099810}.mp4";

        var sectionsJson = JsonSerializer.Serialize(new
        {
            MediaContainer = new
            {
                Directory = new[]
                {
                    new { key = "1", type = "movie", title = "Movies" }
                }
            }
        });

        var libraryJson = JsonSerializer.Serialize(new
        {
            MediaContainer = new
            {
                totalSize = 1,
                Metadata = new[]
                {
                    new
                    {
                        title = "The Hunt for Red October",
                        year = 1990,
                        Media = new[]
                        {
                            new
                            {
                                Part = new[]
                                {
                                    new { file = targetPath }
                                }
                            }
                        }
                    }
                }
            }
        });

        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;

                if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse(sectionsJson);

                if (path.Equals("/library/sections/1/all", StringComparison.OrdinalIgnoreCase))
                    return JsonResponse(libraryJson);

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plex:Enabled"] = "true",
                ["Plex:Token"] = "test-token",
                ["Plex:BaseUrl"] = "http://localhost:32400"
            })
            .Build();

        var service = new PlexLibraryConflictService(
            httpClient,
            configuration,
            NullLogger<PlexLibraryConflictService>.Instance);

        var movie = new Movie
        {
            Title = "The Hunt for Red October",
            Year = 1990,
            ImdbId = "tt0099810",
            TargetPath = targetPath
        };

        var result = service.Check(movie);

        Assert.True(result.HasConflict);
        Assert.Equal(
            PlexLibraryConflictType.AlreadyExistsAtTargetPath,
            result.ConflictType);
        Assert.Contains("proposed target path", result.Message!);
        Assert.Contains("The Hunt for Red October", result.ExistingPath!);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
