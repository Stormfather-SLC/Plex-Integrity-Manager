$ErrorActionPreference = "Stop"

Write-Host "Updating PIM..." -ForegroundColor Cyan
git pull

if ($LASTEXITCODE -ne 0) {
    throw "Git pull failed."
}

Write-Host "Building PIM..." -ForegroundColor Cyan
dotnet build ".\Plex Intake Manager (PIM)\PIM.Web.csproj"

if ($LASTEXITCODE -ne 0) {
    throw "PIM build failed."
}

Write-Host "Starting PIM..." -ForegroundColor Green
dotnet run --project ".\Plex Intake Manager (PIM)\PIM.Web.csproj"