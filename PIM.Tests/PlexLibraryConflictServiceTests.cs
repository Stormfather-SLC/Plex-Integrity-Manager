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
    public void Check_MovieOnFinalPageOfLargeLibrary_LoadsEveryPageAndFindsConflict()
    {
        const int totalSize = 1198;
        const string targetPath =
            @"G:\[PLEX]\Movies\PG\Final Page Movie (2024) {imdb-tt9999999}\Final Page Movie (2024) {imdb-tt9999999}.mp4";

        var requests = new List<(int Start, int Size)>();
        var pages = new Dictionary<int, string>
        {
            [0] = CreateLibraryJson(
                totalSize,
                Enumerable.Range(0, 500).Select(index => CreateMoviePayload(index))),
            [500] = CreateLibraryJson(
                totalSize,
                Enumerable.Range(500, 500).Select(index => CreateMoviePayload(index))),
            [1000] = CreateLibraryJson(
                totalSize,
                Enumerable.Range(1000, 197)
                    .Select(index => CreateMoviePayload(index))
                    .Append(CreateMoviePayload(
                        1197,
                        "Final Page Movie",
                        2024,
                        targetPath)))
        };

        var service = CreateService(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(CreateSectionsJson(("1", "Movies")));

            if (path.Equals("/library/sections/1/all", StringComparison.OrdinalIgnoreCase))
            {
                var start = GetRequiredHeaderValue(request, "X-Plex-Container-Start");
                var size = GetRequiredHeaderValue(request, "X-Plex-Container-Size");
                requests.Add((start, size));

                return pages.TryGetValue(start, out var page)
                    ? JsonResponse(page)
                    : new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = service.Check(new Movie
        {
            Title = "Final Page Movie",
            Year = 2024,
            ImdbId = "tt9999999",
            TargetPath = targetPath
        });

        Assert.True(result.HasConflict);
        Assert.Equal(
            PlexLibraryConflictType.AlreadyExistsAtTargetPath,
            result.ConflictType);
        Assert.Equal(
            new[] { (0, 500), (500, 500), (1000, 500) },
            requests);
    }

    [Fact]
    public void Check_PlexReturnsFewerMoviesThanReported_FailsClosed()
    {
        var pageRequestStarts = new List<int>();

        var service = CreateService(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
                return JsonResponse(CreateSectionsJson(("1", "Movies")));

            if (path.Equals("/library/sections/1/all", StringComparison.OrdinalIgnoreCase))
            {
                var start = GetRequiredHeaderValue(request, "X-Plex-Container-Start");
                pageRequestStarts.Add(start);

                return JsonResponse(start == 0
                    ? CreateLibraryJson(
                        totalSize: 3,
                        Enumerable.Range(0, 2).Select(index => CreateMoviePayload(index)))
                    : CreateLibraryJson(totalSize: 3, Array.Empty<object>()));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = service.Check(new Movie
        {
            Title = "A Movie Not In Plex",
            Year = 2024,
            ImdbId = "tt1234567",
            TargetPath = @"G:\[PLEX]\Movies\A Movie Not In Plex (2024).mp4"
        });

        Assert.True(result.HasConflict);
        Assert.Equal(PlexLibraryConflictType.LibraryMismatch, result.ConflictType);
        Assert.Contains("reported 3 movies, but PIM loaded 2", result.Message!);
        Assert.Equal(new[] { 0, 2 }, pageRequestStarts);
    }

    [Fact]
    public void Check_MovieInSecondMovieLibrarySection_AggregatesSections()
    {
        const string targetPath =
            @"G:\[PLEX]\Kids Movies\G\The Goonies (1985) {imdb-tt0089218}\The Goonies (1985) {imdb-tt0089218}.mp4";

        var requestedSections = new List<string>();

        var service = CreateService(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Equals("/library/sections", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(CreateSectionsJson(
                    ("1", "Movies"),
                    ("2", "Kids Movies")));
            }

            if (path.Equals("/library/sections/1/all", StringComparison.OrdinalIgnoreCase))
            {
                requestedSections.Add("1");
                return JsonResponse(CreateLibraryJson(
                    totalSize: 1,
                    new[] { CreateMoviePayload(0) }));
            }

            if (path.Equals("/library/sections/2/all", StringComparison.OrdinalIgnoreCase))
            {
                requestedSections.Add("2");
                return JsonResponse(CreateLibraryJson(
                    totalSize: 1,
                    new[]
                    {
                        CreateMoviePayload(
                            1,
                            "The Goonies",
                            1985,
                            targetPath)
                    }));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = service.Check(new Movie
        {
            Title = "The Goonies",
            Year = 1985,
            ImdbId = "tt0089218",
            TargetPath = targetPath
        });

        Assert.True(result.HasConflict);
        Assert.Equal(
            PlexLibraryConflictType.AlreadyExistsAtTargetPath,
            result.ConflictType);
        Assert.Equal(new[] { "1", "2" }, requestedSections);
    }

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

    private static PlexLibraryConflictService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plex:Enabled"] = "true",
                ["Plex:Token"] = "stub-token",
                ["Plex:BaseUrl"] = "http://localhost:32400"
            })
            .Build();

        return new PlexLibraryConflictService(
            httpClient,
            configuration,
            NullLogger<PlexLibraryConflictService>.Instance);
    }

    private static int GetRequiredHeaderValue(
        HttpRequestMessage request,
        string name)
    {
        return int.Parse(request.Headers.GetValues(name).Single());
    }

    private static string CreateSectionsJson(
        params (string Key, string Title)[] sections)
    {
        return JsonSerializer.Serialize(new
        {
            MediaContainer = new
            {
                Directory = sections.Select(section => new
                {
                    key = section.Key,
                    type = "movie",
                    title = section.Title
                })
            }
        });
    }

    private static string CreateLibraryJson(
        int totalSize,
        IEnumerable<object> movies)
    {
        return JsonSerializer.Serialize(new
        {
            MediaContainer = new
            {
                totalSize,
                Metadata = movies
            }
        });
    }

    private static object CreateMoviePayload(
        int index,
        string? title = null,
        int? year = null,
        string? path = null)
    {
        return new
        {
            title = title ?? $"Test Movie {index}",
            year = year ?? 2000,
            Media = new[]
            {
                new
                {
                    Part = new[]
                    {
                        new
                        {
                            file = path ?? $@"G:\[PLEX]\Movies\Test Movie {index}.mp4"
                        }
                    }
                }
            }
        };
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
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(_handler(request));
        }
    }
}
