# Plex Integrity Manager (PIM) development guidance

## Purpose and priorities

PIM is a Windows-hosted ASP.NET Core Razor Pages application that scans movie intake folders, parses and enriches movie identity, previews Plex-compatible destination paths, detects duplicates and conflicts, and performs approved moves/renames. Protecting source media and requiring human review when confidence is insufficient take priority over automation.

Preserve existing behavior unless the task explicitly changes it. Investigate the surrounding workflow before editing it; make focused, incremental changes instead of replacing working code or introducing new frameworks or abstractions without a demonstrated need.

## Stack and repository map

- `Plex Integrity Manager (PIM).sln`: application solution. It currently contains the web, Core, Application, and Infrastructure projects, but not `PIM.Tests`; run the test project explicitly.
- `Plex Intake Manager (PIM)/`: `PIM.Web`, a .NET 8 ASP.NET Core Razor Pages app. `Program.cs` is the composition root; `Pages/Index.*` owns the scan/identify/dry-run/commit workflow; `Pages/DestinationProfiles.*` manages destination profiles; `Services/` contains preview-tree, dry-run-preview, and JSON profile-storage services.
- `PIM.Core/`: domain models and service interfaces. `Models/Movies.cs` contains movie identity, review, conflict, approval, and Plex naming state. `Models/DestinationProfiles.cs` contains configurable organization levels and path results.
- `PIM.Infrastructure/`: filesystem scanning, filename parsing, OMDb metadata, destination building/conflict checks, Plex library validation, duplicate decisions, and live rename/move execution.
- `PIM.Application/`: currently a thin placeholder project; do not assume application orchestration lives here.
- `PIM.Tests/`: xUnit tests for path building, parsing, OMDb responses, Plex/destination conflicts, and rename/source-cleanup safety. HTTP tests use stubs and file-operation tests use isolated temporary directories.
- `.github/workflows/build.yml`: Windows CI for the web project plus the explicit test project.
- `Start-PIM.ps1`: owner convenience script that runs `git pull`, builds, and starts PIM. Codex must not run it without explicit approval because it changes repository state through `git pull`.

## Current architecture and behavior

`Program.cs` registers Razor Pages, memory caching, scoped domain services, typed `HttpClient` services for OMDb and Plex, singleton destination-profile storage, and singleton progress/status objects. Keep registrations in the composition root and inject abstractions rather than constructing infrastructure services in PageModels.

The main workflow in `Pages/Index.cshtml.cs` is:

1. `IFileScanner` discovers supported video files and `IFileNameParser` extracts title, year, IMDb ID, and edition.
2. `IMetadataService` enriches missing data through OMDb.
3. `IDuplicateService` assigns duplicate/review/approval decisions.
4. `IRenameService.GeneratePreview` uses `IDestinationPathBuilder` to create the proposed path.
5. `IMovieConflictDetectionService` checks incoming collisions, the destination filesystem, and the read-only Plex library index.
6. `IDryRunPreviewService` and `PreviewTreeService` present the plan, review items, and errors.
7. A live commit is allowed only when its profile revision and plan fingerprint match a preceding cached dry run. `RenameService` rechecks conflicts, refuses overwrite, and constrains targets under the selected destination root.

Plex-compatible naming is centralized on `Movie` and `DestinationPathBuilder`:

`Title (Year) {imdb-ttXXXXXXX}\Title (Year) {edition-Tag?} {imdb-ttXXXXXXX}.ext`

Editions remain files within their own normal Plex movie folder path; do not introduce nested parent movie folders. Edition detection currently includes Edited, Family Edit, Clean/Clean Version, TV Edit, Director's Cut, Extended Edition, Theatrical Cut, IMAX, Unrated, and Remastered.

Persistent mutable settings and destination profiles live under `%LOCALAPPDATA%\Plex Integrity Manager` by default, not in the repository. `appsettings.json` provides defaults; User Secrets provide credentials; `PIM:ProfileDataDirectory` can override profile storage.

## Commands

Run commands from the repository root.

```powershell
dotnet restore "Plex Integrity Manager (PIM).sln"
dotnet build "Plex Integrity Manager (PIM).sln" --configuration Debug
dotnet test "PIM.Tests\PIM.Tests.csproj" --configuration Debug
dotnet run --project "Plex Intake Manager (PIM)\PIM.Web.csproj" --launch-profile http
```

For release-equivalent verification, use `--configuration Release`. Do not use `Start-PIM.ps1` unless the owner explicitly approves its `git pull`. Development HTTPS requires a valid local ASP.NET Core development certificate; the `http` launch profile is acceptable for loopback-only development.

## C# and Razor Pages conventions

- Target `net8.0`; nullable reference types and implicit usings are enabled. Use four-space indentation and follow the namespace/bracing style of the file being edited (the repository currently contains both block and file-scoped namespaces).
- Keep domain state and contracts in Core, external I/O in Infrastructure, and HTTP/UI orchestration in the web project. Do not expand the placeholder Application project merely to impose a preferred architecture.
- Add new services through interfaces when they cross a meaningful boundary or need substitution in tests. Register lifetimes deliberately in `Program.cs`; typed external API clients remain typed `HttpClient` registrations.
- PageModels own Razor handlers, binding, validation, redirects, and UI messages. Keep destructive filesystem logic out of `.cshtml` and PageModels. Preserve handler names and form contracts when changing a page, and inspect its `.cshtml`, PageModel, JavaScript, and service dependencies together.
- Use `async`/`await` end to end for I/O and suffix asynchronous methods with `Async`. Do not block asynchronous HTTP with `.Result`, `.Wait()`, or `GetAwaiter().GetResult()` in new code. Accept and propagate `CancellationToken` where a request or long operation can be cancelled. Do not introduce unobserved fire-and-forget tasks.
- Preserve fail-closed behavior: errors or uncertain identity/conflict state must clear commit approval and surface as Error or Needs Review.
- Keep path creation centralized in `IDestinationPathBuilder`; preview, conflict detection, and commit must use the same `Movie.TargetPath` rather than reimplement naming rules.

## Media, filesystem, and Plex safety

- Never use a real media library for automated tests. Use a newly created isolated temporary source and destination; verify resolved paths before any move or deletion and clean up only the exact test directory.
- Dry-run first for every rename/move change. A task that touches commit logic must prove that dry run performs no move, overwrite, cleanup, or Plex mutation and that live commit still requires matching dry-run approval.
- Never overwrite an existing destination file. Preserve destination-root containment checks, conflict rechecks immediately before moves, and fail-closed review state.
- Treat source cleanup as destructive. Never delete recursively in production media paths; retain the current protections for source root, top-level organization folders, reparse points, and non-empty folders.
- Do not scan, enumerate, rename, move, or delete files outside repository-local/test temporary paths unless the owner explicitly authorizes the exact source and destination. Normal development does not require media-library access.
- Plex conflict detection is currently read-only and uses GET requests. Do not add Plex library mutation, metadata refresh, collection creation, deletion, or other write calls without explicit approval and dedicated tests. Stub Plex in automated tests.

## Configuration and credentials

- Never print, log, paste, commit, or copy values for `Plex:Token`, `Omdb:ApiKey`, passwords, connection strings, or other credentials.
- Keep credentials in .NET User Secrets or narrowly scoped environment variables. Do not put them in `appsettings*.json`, launch settings, source, tests, query logs, or example commands.
- Treat `Plex:BaseUrl`, `PIM:ScanPath`, and `PIM:OutputPath` as machine-specific/sensitive operational configuration. Do not replace or exercise them during routine development.
- External API tests must use stubbed handlers. Real OMDb, Plex, local-network, or other API calls require explicit task need and appropriate scoped network permission.

## Tests and completion criteria

- Add or update focused tests for changed parsing, naming, approval, conflict, preview, and filesystem behavior. High-risk move/cleanup behavior needs both dry-run and live-operation tests against temporary directories.
- Run the explicit test project because it is not included in the solution. Before declaring a code task complete, restore when dependencies changed, build the solution, run relevant tests (normally the full `PIM.Tests` suite), and inspect `git diff` plus `git status`.
- Treat build errors, test failures, analyzer warnings, restore warnings, and unobserved exceptions as findings to investigate. Do not suppress warnings or broadly catch exceptions merely to make verification pass; explain existing issues separately from changes introduced by the task.
- After each task, summarize every changed file, behavior affected, commands run, results, remaining warnings/errors, and any manual verification the owner should perform.

## Git expectations

Preserve all existing work. Do not discard, overwrite, reset, clean, stash, rebase, merge, commit, push, force-push, or change branches unless the owner explicitly requests that exact Git action. Keep diffs focused and do not edit generated `bin/`, `obj/`, `.vs/`, test-results, logs, or local settings. Never run commands that implicitly pull or publish changes without approval.
