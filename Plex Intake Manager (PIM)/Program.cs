using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.FileSystem;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Parsing;
using PIM.Infrastructure.Services;
using PIM.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();

builder.Services.AddScoped<IFileScanner, FileScanner>();
builder.Services.AddScoped<IFileNameParser, FileNameParser>();
builder.Services.AddHttpClient<IMetadataService, OmdbMetadataService>();
builder.Services.AddScoped<IDuplicateService, DuplicateService>();
builder.Services.AddScoped<IDestinationPathBuilder, DestinationPathBuilder>();
builder.Services.AddScoped<IDestinationConflictService, DestinationConflictService>();
builder.Services.AddScoped<IPlexLibraryConflictService, PlexLibraryConflictService>();
builder.Services.AddScoped<IMovieConflictDetectionService, MovieConflictDetectionService>();
builder.Services.AddScoped<IRenameService, RenameService>();

builder.Services.AddSingleton<IDestinationProfileStore, DestinationProfileStore>();
builder.Services.AddSingleton<ScanProgress>();
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

app.Run();
