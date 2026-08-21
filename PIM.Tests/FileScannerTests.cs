using PIM.Infrastructure.FileSystem;
using PIM.Infrastructure.Parsing;
using Xunit;

namespace PIM.Tests;

public sealed class FileScannerTests
{
    [Fact]
    public void Scan_DiscoversNestedMovieAndPreservesFullOriginalPath()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var movieDirectory = Directory.CreateDirectory(Path.Combine(
                testRoot,
                "Edited Movies",
                "Some Movie Folder"));
            var moviePath = Path.Combine(
                movieDirectory.FullName,
                "Movie File 2024.mkv");
            File.WriteAllBytes(moviePath, Array.Empty<byte>());
            File.WriteAllText(
                Path.Combine(movieDirectory.FullName, "notes.txt"),
                "not media");

            var scanner = new FileScanner(new FileNameParser());

            var movies = scanner.Scan(testRoot);

            var movie = Assert.Single(movies);
            Assert.Equal(Path.GetFullPath(moviePath), movie.OriginalFilePath);
            Assert.Equal(movieDirectory.FullName, movie.DirectoryPath);
            Assert.Equal("Movie File 2024.mkv", movie.FileName);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public void GetFiles_ExcludesDestinationTreeNestedUnderSource()
    {
        var testRoot = CreateTestRoot();

        try
        {
            var intakeDirectory = Directory.CreateDirectory(Path.Combine(
                testRoot,
                "Edited Movies",
                "Incoming Movie"));
            var destinationDirectory = Directory.CreateDirectory(Path.Combine(
                testRoot,
                "Plex Destination",
                "Existing Movie",
                "Deeper"));
            var incomingPath = Path.Combine(
                intakeDirectory.FullName,
                "Incoming Movie 2024.mkv");
            var destinationMoviePath = Path.Combine(
                destinationDirectory.FullName,
                "Existing Movie 2020.mp4");
            File.WriteAllBytes(incomingPath, Array.Empty<byte>());
            File.WriteAllBytes(destinationMoviePath, Array.Empty<byte>());

            var scanner = new FileScanner(new FileNameParser());

            var files = scanner.GetFiles(
                testRoot,
                Path.Combine(testRoot, "Plex Destination"));

            Assert.Equal(
                new[] { Path.GetFullPath(incomingPath) },
                files);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"PIM-FileScannerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestRoot(string path)
    {
        if (!Directory.Exists(path))
            return;

        var fullPath = Path.GetFullPath(path);
        var expectedParent = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
                expectedParent,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) ||
            !Path.GetFileName(fullPath).StartsWith(
                "PIM-FileScannerTests-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to clean unexpected test path '{fullPath}'.");
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
