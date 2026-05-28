using PIM.Core.Interfaces;
using PIM.Core.Models;
using PIM.Infrastructure.FileSystem;
using PIM.Infrastructure.FileSystem;
using PIM.Infrastructure.Metadata;
using PIM.Infrastructure.Parsing;
using PIM.Infrastructure.Services;
using PIM.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<IFileScanner, FileScanner>();
// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddScoped<IFileNameParser, FileNameParser>();
builder.Services.AddHttpClient<IMetadataService, OmdbMetadataService>();
builder.Services.AddScoped<IDuplicateService, DuplicateService>();
builder.Services.AddScoped<IRenameService, RenameService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ScanProgress>();
builder.Services.AddScoped<PreviewTreeService>();
builder.Services.AddScoped<IDryRunPreviewService, DryRunPreviewService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();
