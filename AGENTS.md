# Plex Integrity Manager (PIM) development guidance

## Purpose and priorities

PIM is a Windows-hosted ASP.NET Core Razor Pages application that scans movie intake folders, parses and enriches movie identity, previews Plex-compatible destination paths, detects duplicates and conflicts, and performs approved moves/renames. Protecting source media and requiring human review when confidence is insufficient take priority over automation.

Preserve existing behavior unless the task explicitly changes it. Investigate the surrounding workflow before editing it; make focused, incremental changes instead of replacing working code or introducing new frameworks or abstractions without a demonstrated need.

## Stack and repository map

- `Plex Integrity Manager (PIM).sln`: application solution containing the web, Core, Application, Infrastructure, and `PIM.Tests` projects.
- `Plex Intake Manager (PIM)/`: `PIM.Web`, a .NET 8 ASP.NET Core Razor Pages app. `Program.cs` is the composition root; `Pages/Index.*` owns the scan/identify/dry-run/commit workflow; `Pages/DestinationProfiles.*` manages destination profiles; `Services/` contains preview-tree, dry-run-preview, and JSON profile-storage services.
- `PIM.Core/`: domain models and service interfaces. `Models/Movies.cs` contains movie identity, review, conflict, approval, and Plex naming state. `Models/DestinationProfiles.cs` contains configurable organization levels and path results.
- `PIM.Infrastructure/`: filesystem scanning, filename parsing, OMDb metadata, destination building/conflict checks, Plex library validation, duplicate decisions, and live rename/move execution.
- `PIM.Application/`: currently a thin placeholder project; do not assume application orchestration lives here.
- `PIM.Tests/`: xUnit tests for path building, parsing, OMDb responses, Plex/destination conflicts, and rename/source-cleanup safety. HTTP tests use stubs and file-operation tests use isolated temporary directories.
- `.github/workflows/build.yml`: Windows CI that builds the solution and runs `PIM.Tests` on pull requests and on pushes to `main`, `feature/**`, `feat/**`, `fix/**`, `chore/**`, and `integration/**`.
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

To look at the UI without touching the owner's settings, media, OMDb, or Plex, run an isolated instance: set `ASPNETCORE_ENVIRONMENT=Production` (so User Secrets are not loaded), point `PIM__UserSettingsDirectory`, `PIM__ProfileDataDirectory`, `PIM__WorkflowStateDirectory`, and `PIM__JournalDirectory` at a new temporary folder, set `PIM__ScanPath`/`PIM__OutputPath` to temporary folders, and set `Omdb__ApiKey` and `Plex__Token` to empty strings. Do not click Save Settings against real paths.

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
- Before declaring a code task complete, restore when dependencies changed, build the solution, run relevant tests (normally the full `PIM.Tests` suite), and inspect `git diff` plus `git status`.
- Treat build errors, test failures, analyzer warnings, restore warnings, and unobserved exceptions as findings to investigate. Do not suppress warnings or broadly catch exceptions merely to make verification pass; explain existing issues separately from changes introduced by the task.
- After each task, summarize every changed file, behavior affected, commands run, results, remaining warnings/errors, and any manual verification the owner should perform.

## Git expectations

The owner has delegated Phase One (MVP) development to Claude with full autonomy (2026-09-27):

- Claude may create branches, commit, push, open pull requests, and merge into `main` once the Windows CI build and full test suite pass.
- Work on a short-lived branch per focused change (`feat/`, `fix/`, `chore/`) and merge through a pull request so every change has a reviewable record. Never force-push `main` or rewrite published history.
- Safety-critical changes (path generation, rename/move, source cleanup, dry-run/commit approval, fingerprinting) are allowed but must: include regression tests proving the fix and proving dry run still performs no file operation; be labelled **Safety-critical** in the pull request description with a plain-language explanation of the behavior change.
- The safety rules in "Media, filesystem, and Plex safety" and "Configuration and credentials" are unchanged and still apply in full.

Preserve all existing work. Do not discard, reset, clean, or stash uncommitted work that Claude did not create. Keep diffs focused and do not edit generated `bin/`, `obj/`, `.vs/`, test-results, logs, or local settings. `Start-PIM.ps1` still must not be run by an agent because it runs `git pull` on the owner's machine.

## PIM Product Direction and Roadmap

Plex Integrity Manager (PIM) is being developed through these stages:

PIM Alpha → PIM MVP → PIM Basic → PIM Plus → PIM Pro → PIM Ultimate

MVP is a development milestone, not a commercial tier. PIM Basic is the first intended sellable product after MVP validation.

### MVP Product Promise

PIM must safely:

- identify messy movie files;
- determine intended Plex-compatible destinations;
- compare those planned destinations and identities against the real Plex library;
- clearly explain exactly what it plans to do;
- flag uncertainty or conflicts for human review;
- prevent uncertain items from being executed;
- and perform only explicitly approved file operations.

The objective of MVP is not maximum automation. The objective is trustworthiness.

### Development Priorities

Follow this roadmap unless the user explicitly changes the priority:

1. Stabilize the current codebase and Codex development workflow.
2. Build and validate Plex Library Conflict Detection.
3. Improve Needs Review visibility and behavior.
4. Improve IMDb ID confidence handling.
5. Fix OutputPath changes so they do not unnecessarily re-enrich metadata.
6. Review execution safety and logging.
7. Reach “PIM MVP — TRUSTWORTHY.”
8. Dogfood heavily against the owner's own Plex library.
9. Run a small private beta with roughly 5–10 Plex owners.
10. Fix issues revealed by beta.
11. Launch PIM Basic 1.0.
12. Develop PIM Plus, Pro, and eventually Ultimate afterward.

### Current Roadmap Status

- Codebase/Codex workflow stabilization: substantially established.
- Plex Library Conflict Detection: COMPLETE for MVP.
  - Production pagination validated against a real Plex library.
  - Automated pagination regression tests are present.
  - Real-world read-only validation successfully found a movie at Plex pagination offset 2500 through the normal PIM pipeline.
- Phase One work completed and confirmed by the owner on 2026-09-27 (the original `pim-phase-one-plan.md` is lost; this roadmap is now the plan of record):
  - PRs #14–#18: MVP trustworthiness fixes, cleanup, source-folder normalization, missing-credential startup, durable operation journal.
  - PR #19: scans, review decisions, and dry-run approvals persist across restarts.
- Needs Review visibility and behavior (roadmap item 3): COMPLETE for MVP, manual checks passed and confirmed by the owner on 2026-09-27.
  - PRs #21–#24 and #28: problem items first with filter cards, Keep This Copy for duplicate ties, manual IMDb ID entry, stale-tie fix, Confirm File Name.
  - Related safety fixes found along the way: #26 (Plex check can no longer be skipped silently), #27 (release tags stripped from parsed titles).
- IMDb ID confidence handling (roadmap item 4): COMPLETE for MVP, confirmed by the owner on 2026-10-03.
  - #14, #23, #27, #31 (an unconfirmed title match can no longer confirm itself), #36 (weak first matches run recovery; an exact title one year off is suggested at 90% but always needs confirmation).
- OutputPath changes without re-enrichment (roadmap item 5): COMPLETE for MVP, confirmed by the owner on 2026-10-03 (#25 pins it with a test).
- Execution safety and logging (roadmap item 6): COMPLETE for MVP, confirmed by the owner on 2026-10-03.
  - #26 (Plex check fails closed), #29 (journal records every scanned item, including skipped ones), #30 (source file re-verified before every move; folders created only after the journal gate), #33 (possible Plex duplicates are an explicit, journaled user decision; hard stops stay non-overridable).
  - Deferred by the owner: moving RenameService progress output from Console.WriteLine to ILogger levels. The operation journal remains the audit record.
- Review UI: compact Movie Results (#34, #35) and Dry Run Preview (#37), with possible-duplicate details (#32).
- **PIM MVP — TRUSTWORTHY (roadmap item 7): REACHED, confirmed by the owner on 2026-10-03.**
- Current priority: Dogfood heavily against the owner's own Plex library (roadmap item 8). Agents still follow "Media, filesystem, and Plex safety": no access to real media paths unless the owner authorizes the exact source and destination.

Update this status only when the user explicitly confirms that a roadmap milestone has been completed or reprioritized.

### Product Development Principles

When making implementation recommendations or code changes:

- Protect source media above convenience or automation.
- Plex integration is read-only unless the user explicitly changes the product requirement in the future.
- Prefer Dry Run and preview-first workflows before destructive operations.
- Fail closed whenever identity, destination, conflict state, or execution safety is uncertain.
- Unresolved Needs Review items must not be executable.
- Make uncertainty obvious and understandable to the user.
- Favor focused, high-value MVP changes over broad redesigns.
- Preserve working safety behavior unless there is a demonstrated reason to change it.
- Avoid implementing features intended for Basic/Plus/Pro/Ultimate early unless specifically requested.
- Do not expand a task into adjacent roadmap items without approval.
- Tests should protect safety-critical behavior and regressions.
- A technically elegant solution is not automatically the right solution if it adds unnecessary complexity before MVP.

### Scope Discipline

Before implementing a feature, determine whether it belongs to the current roadmap priority.

If a proposed improvement belongs to a later roadmap stage:

- identify it;
- record/recommend it as future work when useful;
- but do not implement it unless explicitly requested.

For MVP, prefer the smallest design that makes PIM safer, clearer, and more trustworthy.
