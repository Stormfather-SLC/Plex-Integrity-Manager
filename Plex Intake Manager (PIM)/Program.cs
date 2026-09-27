using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.FileSystem;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Parsing;
using PIM.Infrastructure.Services;
using PIM.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var userSettingsDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Plex Integrity Manager");
Directory.CreateDirectory(userSettingsDirectory);

// Mutable library settings belong outside the source-controlled project.
// Loading these providers last lets the user's saved settings override the
// development defaults in appsettings.json without editing tracked files.
builder.Configuration.AddJsonFile(
    new PhysicalFileProvider(userSettingsDirectory),
    "library-settings.json",
    optional: true,
    reloadOnChange: true);
builder.Configuration.AddJsonFile(
    new PhysicalFileProvider(userSettingsDirectory),
    "cleanup-settings.json",
    optional: true,
    reloadOnChange: true);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();

builder.Services.AddScoped<IFileScanner, FileScanner>();
builder.Services.AddScoped<IFileNameParser, FileNameParser>();
builder.Services.AddHttpClient<IMetadataService, OmdbMetadataService>();
builder.Services.AddScoped<IDuplicateService, DuplicateService>();
builder.Services.AddScoped<IDestinationPathBuilder, DestinationPathBuilder>();
builder.Services.AddScoped<IDestinationConflictService, DestinationConflictService>();

// Plex validation must remain a typed HttpClient so the validated Plex service
// receives HttpClient correctly and preserves its fail-closed diagnostics.
builder.Services.AddHttpClient<IPlexLibraryConflictService, PlexLibraryConflictService>();
builder.Services.AddScoped<IMovieConflictDetectionService, MovieConflictDetectionService>();
builder.Services.AddScoped<IRenameService, RenameService>();
builder.Services.AddScoped<IMoviePlanService, MoviePlanService>();
builder.Services.AddScoped<IMetadataSuggestionService, MetadataSuggestionService>();

builder.Services.AddSingleton<IDestinationProfileStore, DestinationProfileStore>();
builder.Services.AddSingleton<ScanProgress>();
builder.Services.AddSingleton<SourceCleanupStatus>();
builder.Services.AddScoped<PreviewTreeService>();
builder.Services.AddScoped<IDryRunPreviewService, DryRunPreviewService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

app.MapGet(
    "/api/settings/source-cleanup",
    (IConfiguration configuration) => Results.Json(new
    {
        enabled = configuration.GetValue(
            "PIM:RemoveEmptySourceFolders",
            true)
    }));

app.MapPost(
    "/api/settings/source-cleanup",
    async (
        SourceCleanupSettingRequest request,
        IConfiguration configuration) =>
    {
        var settingsPath = Path.Combine(
            userSettingsDirectory,
            "cleanup-settings.json");
        var temporaryPath = settingsPath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(
                new
                {
                    PIM = new
                    {
                        RemoveEmptySourceFolders = request.Enabled
                    }
                },
                new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, settingsPath, overwrite: true);

            configuration["PIM:RemoveEmptySourceFolders"] =
                request.Enabled.ToString();

            return Results.Json(new
            {
                enabled = request.Enabled,
                message = request.Enabled
                    ? "Empty source folder cleanup is enabled."
                    : "Empty source folder cleanup is disabled."
            });
        }
        catch (Exception ex)
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Preserve the original settings error.
                }
            }

            return Results.Problem(
                $"The source cleanup setting could not be saved: {ex.Message}");
        }
    });

app.MapGet(
    "/api/status/source-cleanup",
    (SourceCleanupStatus status) => Results.Json(status.Snapshot()));

app.Run();

internal sealed record SourceCleanupSettingRequest(bool Enabled);
