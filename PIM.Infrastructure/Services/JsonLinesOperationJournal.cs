using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using PIM.Core.Interfaces;
using PIM.Core.Models;

namespace PIM.Infrastructure.Services;

/// <summary>
/// Writes one JSON Lines file per run under
/// %LOCALAPPDATA%\Plex Integrity Manager\Journal (overridable with
/// PIM:JournalDirectory). Every entry is flushed to disk before returning so a
/// crash mid-commit still leaves an accurate record of completed moves.
/// </summary>
public sealed class JsonLinesOperationJournal : IOperationJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public JsonLinesOperationJournal(IConfiguration configuration)
    {
        var configured = configuration["PIM:JournalDirectory"]?.Trim();

        _directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Plex Integrity Manager",
                "Journal")
            : configured;
    }

    public string Directory => _directory;

    public IOperationJournalRun StartRun(
        bool dryRun,
        string destinationRoot,
        int itemCount)
    {
        System.IO.Directory.CreateDirectory(_directory);

        var startedUtc = DateTime.UtcNow;
        var fileName =
            $"{startedUtc:yyyyMMdd-HHmmss-fff}-{(dryRun ? "dry-run" : "live-commit")}-{Guid.NewGuid():N}.jsonl";
        var path = Path.Combine(_directory, fileName);

        var run = new Run(path);

        try
        {
            run.Record(new OperationJournalEntry(
                OperationJournalEvent.RunStarted,
                TargetPath: destinationRoot,
                Status: dryRun ? "Dry Run" : "Live Commit",
                Detail: $"{itemCount} approved item(s)")
            {
                TimestampUtc = startedUtc
            });
        }
        catch
        {
            run.Dispose();
            throw;
        }

        return run;
    }

    private sealed class Run : IOperationJournalRun
    {
        private readonly FileStream _stream;

        public Run(string path)
        {
            Location = path;
            _stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
        }

        public string Location { get; }

        public void Record(OperationJournalEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var line = JsonSerializer.Serialize(entry, SerializerOptions) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush(flushToDisk: true);
        }

        public void Dispose() => _stream.Dispose();
    }
}
