using System.Buffers;
using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace TakeoutEngine;

public static partial class Program
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".png", ".webp", ".gif", ".tiff", ".dng",
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".webm"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".webm"
    };

    public static async Task<int> Main(string[] args)
    {
        // If no arguments or interactive flag provided, launch interactive wizard!
        if (args.Length == 0 || args.Contains("--interactive", StringComparer.OrdinalIgnoreCase) || args.Contains("-i", StringComparer.OrdinalIgnoreCase))
        {
            return await RunInteractiveWizardAsync();
        }

        var inputOption = new Option<DirectoryInfo>(
            name: "--input",
            description: "Directory containing downloaded Takeout .zip files.") { IsRequired = true };

        var outputOption = new Option<DirectoryInfo>(
            name: "--output",
            description: "Target directory for organized media files.") { IsRequired = true };

        var tempOption = new Option<DirectoryInfo?>(
            name: "--temp",
            description: "Intermediate staging directory for uncompressed files (defaults to 'takeout_staging' on input drive).",
            getDefaultValue: () => null);

        var workersOption = new Option<int>(
            name: "--workers",
            description: "Number of concurrent persistent ExifTool worker instances.",
            getDefaultValue: () => Math.Max(1, Environment.ProcessorCount / 2));

        var cleanStagingOption = new Option<bool>(
            name: "--clean-staging",
            description: "Delete the temporary staging directory after processing completes.",
            getDefaultValue: () => false);

        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Simulate processing, resolution, and deduplication without moving files or writing metadata.",
            getDefaultValue: () => false);

        var resumeOption = new Option<bool>(
            name: "--resume",
            description: "Resume processing using existing staging files without re-extracting completed zips.",
            getDefaultValue: () => false);

        var rootCommand = new RootCommand("TakeoutEngine - Automated, high-performance Google Takeout processor (.NET 10)")
        {
            inputOption, outputOption, tempOption, workersOption, cleanStagingOption, dryRunOption, resumeOption
        };

        rootCommand.SetHandler(async (input, output, temp, workers, cleanStaging, dryRun, resume) =>
        {
            var effectiveTemp = temp ?? GetDefaultStagingDir(input);
            await ProcessTakeoutAsync(input, output, effectiveTemp, workers, cleanStaging, dryRun, resume);
        }, inputOption, outputOption, tempOption, workersOption, cleanStagingOption, dryRunOption, resumeOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<int> RunInteractiveWizardAsync()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(
            new FigletText("TakeoutEngine")
                .Centered()
                .Color(Color.Cyan1));

        var introPanel = new Panel(
            new Markup(
                "[bold white]Welcome to TakeoutEngine Setup Wizard![/]\n\n" +
                "This wizard guides you through extracting, repairing metadata, deduplicating, preserving albums, and organizing your Google Takeout archives into a clean YYYY/MM library ready for your NAS.\n\n" +
                "[grey]Tip: Press Enter to accept default values when provided in brackets.[/]"))
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 1)
        };
        AnsiConsole.Write(introPanel);
        AnsiConsole.WriteLine();

        // 1. Input directory
        var inputPathStr = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold cyan]Folder containing Takeout .zip files:[/]")
                .Validate(path =>
                {
                    var clean = path.Trim('\"', '\'');
                    if (!Directory.Exists(clean)) return ValidationResult.Error("[red]Directory does not exist![/]");
                    var zips = Directory.GetFiles(clean, "*.zip", SearchOption.TopDirectoryOnly);
                    if (zips.Length == 0) return ValidationResult.Error("[red]No .zip archives found in this directory![/]");
                    return ValidationResult.Success();
                }));

        var inputDir = new DirectoryInfo(inputPathStr.Trim('\"', '\''));
        var zipCount = inputDir.GetFiles("*.zip").Length;
        AnsiConsole.MarkupLine($"[green]✓ Found {zipCount} Takeout zip archive(s).[/]\n");

        // 2. Output directory
        var defaultOutputDir = Path.Combine(inputDir.Root.FullName, "Organized_Photos");
        var outputPathStr = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold green]Destination folder for organized library:[/]")
                .DefaultValue(defaultOutputDir));
        var outputDir = new DirectoryInfo(outputPathStr.Trim('\"', '\''));

        // 3. Staging directory
        var defaultStagingDir = GetDefaultStagingDir(inputDir).FullName;
        var tempPathStr = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold blue]Temporary staging directory:[/]")
                .DefaultValue(defaultStagingDir));
        var tempDir = new DirectoryInfo(tempPathStr.Trim('\"', '\''));

        // 4. Workers
        var defaultWorkers = Math.Max(1, Environment.ProcessorCount / 2);
        var workers = AnsiConsole.Prompt(
            new TextPrompt<int>("[bold magenta]Concurrent ExifTool workers (recommended: CPU cores / 2):[/]")
                .DefaultValue(defaultWorkers)
                .Validate(w => w is >= 1 and <= 32 ? ValidationResult.Success() : ValidationResult.Error("[red]Must be between 1 and 32[/]")));

        // 5. Resume (if staging directory already exists)
        var resume = false;
        var manifestPath = Path.Combine(tempDir.FullName, ".takeout_staging_manifest.json");
        if (File.Exists(manifestPath) || (tempDir.Exists && tempDir.GetFileSystemInfos().Length > 0))
        {
            resume = AnsiConsole.Prompt(
                new ConfirmationPrompt("[bold yellow]Existing staging files detected. Resume and skip re-extracting completed zips?[/]")
                {
                    DefaultValue = true
                });
        }

        // 6. Clean staging (default: false, preserves raw data!)
        var cleanStaging = AnsiConsole.Prompt(
            new ConfirmationPrompt("[bold yellow]Clean up temporary staging directory when finished? (Default: No, keep raw data)[/]")
            {
                DefaultValue = false
            });

        // 7. Dry run
        var dryRun = AnsiConsole.Prompt(
            new ConfirmationPrompt("[bold yellow]Run in simulation mode (dry run)? (No files will be modified)[/]")
            {
                DefaultValue = false
            });

        // Summary table
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Configured Value[/]");

        summaryTable.AddRow("[cyan]Takeout Archives[/]", $"{Markup.Escape(inputDir.FullName)} ({zipCount} zips)");
        summaryTable.AddRow("[green]Output Library[/]", Markup.Escape(outputDir.FullName));
        summaryTable.AddRow("[blue]Temporary Staging[/]", Markup.Escape(tempDir.FullName));
        summaryTable.AddRow("[magenta]Parallel Workers[/]", workers.ToString());
        summaryTable.AddRow("Resume Existing", resume ? "[green]Yes[/]" : "[grey]No[/]");
        summaryTable.AddRow("Clean Staging After", cleanStaging ? "[yellow]Yes[/]" : "[green]No (Preserve raw data)[/]");
        summaryTable.AddRow("Mode", dryRun ? "[bold yellow]DRY RUN (Simulation)[/]" : "[bold green]Normal (Live)[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("[bold yellow]Ready to start processing?[/]", defaultValue: true))
        {
            AnsiConsole.MarkupLine("[grey]Operation cancelled by user.[/]");
            return 0;
        }

        AnsiConsole.WriteLine();
        await ProcessTakeoutAsync(inputDir, outputDir, tempDir, workers, cleanStaging, dryRun, resume);
        return 0;
    }

    private static DirectoryInfo GetDefaultStagingDir(DirectoryInfo inputDir)
    {
        var root = inputDir.Root.FullName;
        var stagingPath = Path.Combine(root, "takeout_staging");
        return new DirectoryInfo(stagingPath);
    }

    private static string? GetAlbumName(FileInfo file)
    {
        var dir = file.Directory;
        if (dir == null) return null;
        var dirName = dir.Name;

        if (dirName.StartsWith("Photos from ", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(dirName, "Google Photos", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(dirName, "Takeout", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(dirName, "Trash", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(dirName, "Bin", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(dirName, "Locked Folder", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(dirName, "Archive", StringComparison.OrdinalIgnoreCase)) return null;

        var clean = Regex.Replace(dirName, @"\(\d+\)$", "").Trim();
        return string.IsNullOrEmpty(clean) ? dirName : clean;
    }

    private sealed record ProcessItem(FileInfo File, List<string> Albums);

    private static async Task ProcessTakeoutAsync(
        DirectoryInfo inputDir,
        DirectoryInfo outputDir,
        DirectoryInfo tempDir,
        int workerCount,
        bool cleanStaging = false,
        bool dryRun = false,
        bool resume = false)
    {
        outputDir.Create();
        tempDir.Create();

        var panel = new Panel(
            new Markup(
                $"[bold cyan]TakeoutEngine (.NET 10)[/]\n\n" +
                $"[grey]Input:[/]    [yellow]{Markup.Escape(inputDir.FullName)}[/]\n" +
                $"[grey]Output:[/]   [green]{Markup.Escape(outputDir.FullName)}[/]\n" +
                $"[grey]Staging:[/]  [blue]{Markup.Escape(tempDir.FullName)}[/]\n" +
                $"[grey]Workers:[/]  [magenta]{workerCount}[/]\n" +
                $"[grey]Mode:[/]     {(dryRun ? "[bold yellow]DRY RUN (Simulation)[/]" : "[bold green]Normal[/]")}\n" +
                $"[grey]Clean:[/]    {(cleanStaging ? "[yellow]Yes[/]" : "[green]No (Preserve raw staging data)[/]")}"
            ))
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader(dryRun ? "[bold yellow] SIMULATION PIPELINE (DRY-RUN) [/]" : "[bold white] ARCHIVE PROCESSING PIPELINE [/]", Justify.Center),
            Padding = new Padding(1, 1)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        // 1. Extract archives with explicit UTF-8 support and manifest tracking
        var zipFiles = inputDir.GetFiles("*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (zipFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]No .zip archives found in the input directory.[/]");
            return;
        }

        var manifestPath = Path.Combine(tempDir.FullName, ".takeout_staging_manifest.json");
        var extractedZips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(manifestPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(manifestPath));
                if (existing != null) extractedZips = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(Spinner.Known.Dots)
            )
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]1/4 Extracting zip archives[/]", maxValue: zipFiles.Length);
                foreach (var zip in zipFiles)
                {
                    if (resume && extractedZips.Contains(zip.Name))
                    {
                        task.Description = $"[cyan]1/4 Skipped (already extracted):[/] [grey]{zip.Name}[/]";
                        task.Increment(1);
                        continue;
                    }

                    task.Description = $"[cyan]1/4 Extracting:[/] [grey]{zip.Name}[/]";
                    using var archive = ZipFile.Open(zip.FullName, ZipArchiveMode.Read, Encoding.UTF8);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        var destPath = Path.Combine(tempDir.FullName, entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }

                    extractedZips.Add(zip.Name);
                    try
                    {
                        File.WriteAllText(manifestPath, JsonSerializer.Serialize(extractedZips.ToList()));
                    }
                    catch { }

                    task.Increment(1);
                }
                task.Description = $"[green]1/4 Staged {zipFiles.Length} zip archives[/]";
            });

        // 2. Discover media files
        List<FileInfo> mediaFiles = null!;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[yellow]2/4 Indexing media files in staging...[/]", async ctx =>
            {
                mediaFiles = tempDir.GetFiles("*", SearchOption.AllDirectories)
                    .Where(f => MediaExtensions.Contains(f.Extension))
                    .ToList();
            });

        AnsiConsole.MarkupLine($"[green]2/4 Discovered {mediaFiles.Count:N0} total media files.[/]");

        // 3. Metadata-aware deduplication & album preservation via XxHash64
        var hashGroups = new ConcurrentDictionary<ulong, ConcurrentBag<FileInfo>>();
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

        try
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(Spinner.Known.Dots)
                )
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[yellow]3/4 Calculating checksums (XxHash64)[/]", maxValue: mediaFiles.Count);
                    foreach (var file in mediaFiles)
                    {
                        var hash = await ComputeXxHash64Async(file.FullName, buffer);
                        hashGroups.GetOrAdd(hash, _ => new ConcurrentBag<FileInfo>()).Add(file);
                        task.Increment(1);
                    }
                    task.Description = $"[green]3/4 Checksums complete for {mediaFiles.Count:N0} media files[/]";
                });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Select best candidate per hash and aggregate all unique album tags
        var uniqueItems = new List<ProcessItem>();
        var duplicateLogs = new List<(ulong Hash, string KeptFile, string DroppedFile, string Reason)>();

        foreach (var (hash, bag) in hashGroups)
        {
            var group = bag.ToList();
            var best = SelectBestFileCandidate(group);

            // Collect all unique album names across duplicate copies
            var albums = group
                .Select(f => GetAlbumName(f))
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(a => a!)
                .ToList();

            uniqueItems.Add(new ProcessItem(best, albums));

            foreach (var dup in group)
            {
                if (dup.FullName != best.FullName)
                {
                    duplicateLogs.Add((hash, best.FullName, dup.FullName, "Identical XxHash64 checksum"));
                }
            }
        }

        var droppedDuplicates = mediaFiles.Count - uniqueItems.Count;
        AnsiConsole.MarkupLine($"[green]3/4 Deduplication complete:[/] [bold]{uniqueItems.Count:N0}[/] unique files (dropped [dim]{droppedDuplicates:N0}[/] duplicates).");

        // 4. Parallel Date Resolution, ExifTool writing & Album/Favorite tagging
        var pool = dryRun ? null : new ExifToolWorkerPool(workerCount);
        var total = uniqueItems.Count;
        var manualReviewList = new ConcurrentBag<(string Path, string Reason)>();
        var manualFolder = Path.Combine(outputDir.FullName, "_Manual_Review");

        var favoritedCount = 0;
        var albumTaggedCount = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(Spinner.Known.Dots)
            )
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[magenta]4/4 Tagging & Organizing ({workerCount} workers)[/]", maxValue: total);

                await Parallel.ForEachAsync(uniqueItems, new ParallelOptions { MaxDegreeOfParallelism = workerCount }, async (item, ct) =>
                {
                    var file = item.File;
                    var albums = item.Albums;
                    var rawMeta = ResolveMetadataFromJson(file);
                    var decision = AutomatedDateResolver.Resolve(file, rawMeta.Timestamp);

                    if (rawMeta.IsFavorited) Interlocked.Increment(ref favoritedCount);
                    if (albums.Count > 0) Interlocked.Increment(ref albumTaggedCount);

                    if (decision.Strategy == ResolutionStrategy.Failed)
                    {
                        var targetPath = Path.Combine(manualFolder, file.Name);
                        if (!dryRun)
                        {
                            Directory.CreateDirectory(manualFolder);
                            if (File.Exists(targetPath))
                            {
                                targetPath = Path.Combine(manualFolder, $"{Guid.NewGuid().ToString()[..6]}_{file.Name}");
                            }

                            File.Move(file.FullName, targetPath, overwrite: true);
                        }
                        manualReviewList.Add((targetPath, decision.Description));
                    }
                    else
                    {
                        var ts = decision.ResolvedDate!.Value;
                        string destinationFolder = decision.Strategy == ResolutionStrategy.FolderYearOnly
                            ? Path.Combine(outputDir.FullName, ts.ToString("yyyy"), "_Estimated_Year")
                            : Path.Combine(outputDir.FullName, ts.ToString("yyyy"), ts.ToString("MM"));

                        var prefix = decision.Strategy switch
                        {
                            ResolutionStrategy.FolderYearOnly => $"ESTIMATED_{ts:yyyyMMdd}_",
                            _ => $"{ts:yyyyMMdd_HHmmss}_"
                        };

                        var cleanName = $"{prefix}{file.Name}";
                        var targetPath = Path.Combine(destinationFolder, cleanName);

                        if (!dryRun)
                        {
                            Directory.CreateDirectory(destinationFolder);

                            if (File.Exists(targetPath))
                            {
                                targetPath = Path.Combine(destinationFolder, $"{prefix}{Guid.NewGuid().ToString()[..6]}_{file.Name}");
                            }

                            // Move file to destination first
                            File.Move(file.FullName, targetPath, overwrite: true);

                            // Write metadata via ExifTool
                            if (pool != null)
                            {
                                var worker = await pool.LeaseWorkerAsync();
                                try
                                {
                                    var isVideo = VideoExtensions.Contains(file.Extension);
                                    await worker.ApplyMetadataAsync(
                                        targetPath,
                                        ts,
                                        rawMeta.Latitude,
                                        rawMeta.Longitude,
                                        rawMeta.Description,
                                        isVideo,
                                        rawMeta.IsFavorited,
                                        albums);
                                }
                                finally
                                {
                                    pool.ReleaseWorker(worker);
                                }
                            }

                            // Update filesystem timestamps on destination file
                            try
                            {
                                File.SetCreationTimeUtc(targetPath, ts);
                                File.SetLastWriteTimeUtc(targetPath, ts);
                            }
                            catch { }
                        }
                    }

                    task.Increment(1);
                });

                task.Description = $"[green]4/4 Processed {total:N0} files successfully[/]";
            });

        if (pool != null)
        {
            await pool.DisposeAsync();
        }

        // 5. Summary Table & Logging
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Execution Mode", dryRun ? "[bold yellow]DRY RUN (Simulation - No files modified)[/]" : "[bold green]Normal (Live Execution)[/]");
        table.AddRow("Total Media Files Found", $"{mediaFiles.Count:N0}");
        table.AddRow("Duplicates Dropped", $"{droppedDuplicates:N0}");
        table.AddRow("Unique Files Processed", $"{uniqueItems.Count:N0}");
        table.AddRow("Successfully Dated & Tagged", $"[green]{uniqueItems.Count - manualReviewList.Count:N0}[/]");
        table.AddRow("⭐ Favorited Media Tagged", $"[yellow]{favoritedCount:N0}[/]");
        table.AddRow("🏷️ Album-Tagged Media", $"[cyan]{albumTaggedCount:N0}[/]");

        if (duplicateLogs.Count > 0 && !dryRun)
        {
            var dupCsvPath = Path.Combine(outputDir.FullName, "duplicates.csv");
            var lines = duplicateLogs.Select(d => $"\"{d.Hash:X16}\";\"{d.KeptFile}\";\"{d.DroppedFile}\";\"{d.Reason}\"");
            await File.WriteAllLinesAsync(dupCsvPath, new[] { "Hash;KeptFile;DroppedDuplicate;Reason" }.Concat(lines));
            table.AddRow("Duplicate Audit Log", $"[cyan]{Markup.Escape(dupCsvPath)}[/]");
        }

        if (!manualReviewList.IsEmpty)
        {
            table.AddRow("Quarantined (_Manual_Review)", $"[yellow]{manualReviewList.Count:N0}[/]");
            if (!dryRun)
            {
                var logPath = Path.Combine(outputDir.FullName, "manual_review.csv");
                var lines = manualReviewList.Select(m => $"\"{m.Path}\";\"{m.Reason}\"");
                await File.WriteAllLinesAsync(logPath, new[] { "File;Reason" }.Concat(lines));
                table.AddRow("Review Log Written", $"[yellow]{Markup.Escape(logPath)}[/]");
            }
        }
        else
        {
            table.AddRow("Quarantined", "[green]0[/]");
        }

        table.AddRow("Output Directory", $"[cyan]{Markup.Escape(outputDir.FullName)}[/]");

        if (cleanStaging && !dryRun)
        {
            try
            {
                tempDir.Delete(recursive: true);
                table.AddRow("Staging Directory", "[yellow]Cleaned up (Deleted)[/]");
            }
            catch (Exception ex)
            {
                table.AddRow("Staging Directory", $"[grey]Could not delete: {Markup.Escape(ex.Message)}[/]");
            }
        }
        else
        {
            table.AddRow("Staging Directory", $"[grey]Preserved raw data ({Markup.Escape(tempDir.FullName)})[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);

        if (dryRun)
        {
            AnsiConsole.MarkupLine("\n[bold yellow]Dry-run simulation completed. No files were moved or altered.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[bold green]Processing completed successfully! Library is ready for NAS synchronization.[/]");
        }
    }

    private static FileInfo SelectBestFileCandidate(IEnumerable<FileInfo> candidates)
    {
        var list = candidates.ToList();
        if (list.Count == 1) return list[0];

        // 1. Prefer candidate with existing parseable JSON sidecar
        foreach (var file in list)
        {
            var meta = ResolveMetadataFromJson(file);
            if (meta.Timestamp.HasValue) return file;
        }

        // 2. Prefer candidate with filename-based date
        foreach (var file in list)
        {
            if (AutomatedDateResolver.HasFilenameDate(file.Name)) return file;
        }

        // 3. Fallback to first
        return list[0];
    }

    private static async Task<ulong> ComputeXxHash64Async(string filePath, byte[] buffer)
    {
        var hasher = new XxHash64();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, bytesRead));
        }
        return hasher.GetCurrentHashAsUInt64();
    }

    public static RawJsonMetadata ResolveMetadataFromJson(FileInfo mediaFile)
    {
        var dir = mediaFile.Directory;
        if (dir == null || !dir.Exists) return new RawJsonMetadata(null, null, null, null, false);

        var name = mediaFile.Name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = mediaFile.Extension;

        // Base stems without -edited, -effects, -cover
        var uneditedStem = Regex.Replace(stem, @"-(edited|effects|cover)$", "", RegexOptions.IgnoreCase);

        // Candidate sidecar paths in priority order
        var candidateNames = new List<string>
        {
            $"{name}.supplemental-metadata.json",
            $"{name}.supplemental-meta.json",
            $"{name}.supplemental-metad.json",
            $"{name}.supplemen.json",
            $"{name}.supplemental.json",
            $"{name}.supplemental-me.json",
            $"{name}.suppl.json",
            $"{name}..json",
            $"{name}.json",
            $"{stem}.json",
            $"{stem}.supplemental-metadata.json",
            $"{stem}.supplemental-meta.json",
            $"{stem}.supplemental-metad.json",
            $"{stem}.supplemen.json",
            $"{stem}.supplemental.json",
            $"{stem}.suppl.json"
        };

        if (!string.Equals(uneditedStem, stem, StringComparison.OrdinalIgnoreCase))
        {
            candidateNames.Add($"{uneditedStem}{ext}.supplemental-metadata.json");
            candidateNames.Add($"{uneditedStem}{ext}.supplemental-meta.json");
            candidateNames.Add($"{uneditedStem}{ext}.supplemental-metad.json");
            candidateNames.Add($"{uneditedStem}{ext}.supplemen.json");
            candidateNames.Add($"{uneditedStem}{ext}.supplemental.json");
            candidateNames.Add($"{uneditedStem}{ext}.suppl.json");
            candidateNames.Add($"{uneditedStem}{ext}.json");
            candidateNames.Add($"{uneditedStem}.json");
            candidateNames.Add($"{uneditedStem}.supplemental-metadata.json");
            candidateNames.Add($"{uneditedStem}.supplemen.json");
            candidateNames.Add($"{uneditedStem}.suppl.json");
        }

        // Numbered duplicate patterns e.g. stem(1).ext -> stem.ext.supplemental-metadata(1).json
        var dupMatch = Regex.Match(stem, @"^(?<base>.*)[\(\[\-_\s](?<num>\d+)[\)\]]?$");
        if (dupMatch.Success)
        {
            var baseStem = dupMatch.Groups["base"].Value;
            var num = dupMatch.Groups["num"].Value;
            candidateNames.Add($"{baseStem}{ext}.supplemental-metadata({num}).json");
            candidateNames.Add($"{baseStem}{ext}.supplemental-metad({num}).json");
            candidateNames.Add($"{baseStem}{ext}.supplemen({num}).json");
            candidateNames.Add($"{baseStem}{ext}.suppl({num}).json");
            candidateNames.Add($"{baseStem}{ext}({num}).json");
            candidateNames.Add($"{baseStem}{ext}.supplemental-metadata.json");
            candidateNames.Add($"{baseStem}{ext}.supplemental-metad.json");
            candidateNames.Add($"{baseStem}{ext}.supplemen.json");
            candidateNames.Add($"{baseStem}{ext}.suppl.json");
            candidateNames.Add($"{baseStem}{ext}.json");
            candidateNames.Add($"{baseStem}.json");
            candidateNames.Add($"{baseStem}.supplemental-metadata.json");
            candidateNames.Add($"{baseStem}.suppl.json");
        }

        FileInfo? sidecar = null;
        foreach (var cName in candidateNames)
        {
            var candidate = new FileInfo(Path.Combine(dir.FullName, cName));
            if (candidate.Exists)
            {
                sidecar = candidate;
                break;
            }
        }

        // Wildcard directory search for sidecars matching the exact filename or stem
        if (sidecar == null)
        {
            sidecar = dir.GetFiles($"{name}*.json").FirstOrDefault()
                   ?? dir.GetFiles($"{stem}*.json").FirstOrDefault();

            if (sidecar == null && !string.Equals(uneditedStem, stem, StringComparison.OrdinalIgnoreCase))
            {
                sidecar = dir.GetFiles($"{uneditedStem}*.json").FirstOrDefault();
            }
        }

        // Live Photo fallback: video file inherits sidecar from paired still photo sidecars in the same directory
        if (sidecar == null && VideoExtensions.Contains(ext))
        {
            foreach (var stillExt in new[] { ".heic", ".HEIC", ".jpg", ".JPG", ".jpeg", ".JPEG", ".dng", ".DNG" })
            {
                var stillSidecar = dir.GetFiles($"{stem}{stillExt}*.json").FirstOrDefault();
                if (stillSidecar != null && stillSidecar.Exists)
                {
                    sidecar = stillSidecar;
                    break;
                }
            }
        }

        // Prefix match for truncated long names (>= 20 chars)
        if (sidecar == null && stem.Length >= 20)
        {
            var prefix = stem[..Math.Min(32, stem.Length)];
            sidecar = dir.GetFiles($"{prefix}*.json").FirstOrDefault();
        }

        if (sidecar == null || !sidecar.Exists) return new RawJsonMetadata(null, null, null, null, false);

        try
        {
            using var stream = File.OpenRead(sidecar.FullName);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            DateTime? dt = null;
            if (root.TryGetProperty("photoTakenTime", out var takenProp) &&
                takenProp.TryGetProperty("timestamp", out var tsProp) &&
                long.TryParse(tsProp.GetString(), out var unixSec) && unixSec > 0)
            {
                dt = DateTimeOffset.FromUnixTimeSeconds(unixSec).UtcDateTime;
            }

            double? lat = null, lng = null;
            var geoElem = root.TryGetProperty("geoDataExif", out var exifGeo) ? exifGeo :
                          root.TryGetProperty("geoData", out var regularGeo) ? regularGeo : default;

            if (geoElem.ValueKind == JsonValueKind.Object)
            {
                if (geoElem.TryGetProperty("latitude", out var latProp) && latProp.GetDouble() != 0.0) lat = latProp.GetDouble();
                if (geoElem.TryGetProperty("longitude", out var lngProp) && lngProp.GetDouble() != 0.0) lng = lngProp.GetDouble();
            }

            string? description = root.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;

            bool isFavorited = false;
            if (root.TryGetProperty("favorited", out var favProp) && favProp.ValueKind == JsonValueKind.True)
            {
                isFavorited = true;
            }

            return new RawJsonMetadata(dt, lat, lng, description, isFavorited);
        }
        catch
        {
            return new RawJsonMetadata(null, null, null, null, false);
        }
    }
}

public readonly record struct RawJsonMetadata(DateTime? Timestamp, double? Latitude, double? Longitude, string? Description, bool IsFavorited);

public enum ResolutionStrategy
{
    JsonSidecar,
    FilenameDate,
    FilenameUnixEpoch,
    FolderExactDate,
    FolderYearOnly,
    Failed
}

public readonly record struct ResolutionDecision(DateTime? ResolvedDate, ResolutionStrategy Strategy, string Description);

public static partial class AutomatedDateResolver
{
    [GeneratedRegex(@"(?<year>19\d\d|20\d\d)[-_]?(?<month>0[1-9]|1[0-2])[-_]?(?<day>0[1-9]|[12]\d|3[01])(?:[-_](?<hour>[01]\d|2[0-3])[-_]?(?<minute>[0-5]\d)[-_]?(?<second>[0-5]\d)?)?")]
    private static partial Regex StandardDatePattern();

    [GeneratedRegex(@"\b(1[2-7]\d{11})\b")]
    private static partial Regex EpochMillisPattern();

    [GeneratedRegex(@"\b(1[2-7]\d{8})\b")]
    private static partial Regex EpochSecondsPattern();

    [GeneratedRegex(@"(?<year>199\d|20[0-2]\d)")]
    private static partial Regex YearInTextPattern();

    public static bool HasFilenameDate(string filename) => TryExtractStandardDate(filename, out _);

    public static ResolutionDecision Resolve(FileInfo file, DateTime? jsonDate)
    {
        // 1. JSON Sidecar
        if (jsonDate.HasValue && jsonDate.Value.Year > 1971)
        {
            return new ResolutionDecision(jsonDate.Value, ResolutionStrategy.JsonSidecar, "Exact date from JSON sidecar");
        }

        // 2. Standard timestamp in filename (including WhatsApp IMG-YYYYMMDD-WA... pattern)
        if (TryExtractStandardDate(file.Name, out var dateFromName))
        {
            return new ResolutionDecision(dateFromName, ResolutionStrategy.FilenameDate, "Parsed YYYYMMDD date from filename");
        }

        // 3. Unix epoch in filename (ignore Snapchat random ID strings)
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        if (!stem.StartsWith("Snapchat-", StringComparison.OrdinalIgnoreCase))
        {
            var msMatch = EpochMillisPattern().Match(stem);
            if (msMatch.Success && long.TryParse(msMatch.Value, out var ms))
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                if (dt.Year >= 2005 && dt.Year <= DateTime.UtcNow.Year)
                {
                    return new ResolutionDecision(dt, ResolutionStrategy.FilenameUnixEpoch, "Parsed Unix epoch (ms) from filename");
                }
            }

            var secMatch = EpochSecondsPattern().Match(stem);
            if (secMatch.Success && long.TryParse(secMatch.Value, out var sec))
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
                if (dt.Year >= 2005 && dt.Year <= DateTime.UtcNow.Year)
                {
                    return new ResolutionDecision(dt, ResolutionStrategy.FilenameUnixEpoch, "Parsed Unix epoch (seconds) from filename");
                }
            }
        }

        // 4. Full date from parent directory hierarchy
        var currentDir = file.Directory;
        while (currentDir != null && !currentDir.Name.Equals("Takeout", StringComparison.OrdinalIgnoreCase))
        {
            if (TryExtractStandardDate(currentDir.Name, out var dateFromDir))
            {
                return new ResolutionDecision(dateFromDir, ResolutionStrategy.FolderExactDate, $"Extracted full date from folder '{currentDir.Name}'");
            }
            currentDir = currentDir.Parent;
        }

        // 5. Year-only from parent directory hierarchy
        currentDir = file.Directory;
        while (currentDir != null && !currentDir.Name.Equals("Takeout", StringComparison.OrdinalIgnoreCase))
        {
            var yrMatch = YearInTextPattern().Match(currentDir.Name);
            if (yrMatch.Success && int.TryParse(yrMatch.Groups["year"].Value, out var year))
            {
                var estimatedYear = new DateTime(year, 7, 1, 12, 0, 0, DateTimeKind.Utc);
                return new ResolutionDecision(estimatedYear, ResolutionStrategy.FolderYearOnly, $"Inferred year {year} from folder '{currentDir.Name}'");
            }
            currentDir = currentDir.Parent;
        }

        // 6. Complete failure
        return new ResolutionDecision(null, ResolutionStrategy.Failed, "No valid date found in JSON, filename, epoch, or folder hierarchy");
    }

    private static bool TryExtractStandardDate(string text, out DateTime extractedDate)
    {
        extractedDate = default;
        var match = StandardDatePattern().Match(text);
        if (!match.Success) return false;

        var y = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var d = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);

        var hour = match.Groups["hour"].Success ? int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture) : 12;
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        var second = match.Groups["second"].Success ? int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture) : 0;

        try
        {
            extractedDate = new DateTime(y, m, d, hour, minute, second, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ExifToolWorkerPool : IAsyncDisposable
{
    private readonly List<ExifToolWorker> _workers = new();
    private readonly SemaphoreSlim _semaphore;

    public ExifToolWorkerPool(int size)
    {
        _semaphore = new SemaphoreSlim(size, size);
        for (var i = 0; i < size; i++)
        {
            _workers.Add(new ExifToolWorker());
        }
    }

    public async Task<ExifToolWorker> LeaseWorkerAsync()
    {
        await _semaphore.WaitAsync();
        lock (_workers)
        {
            var worker = _workers.First(w => !w.IsBusy);
            worker.IsBusy = true;
            return worker;
        }
    }

    public void ReleaseWorker(ExifToolWorker worker)
    {
        lock (_workers)
        {
            worker.IsBusy = false;
        }
        _semaphore.Release();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers)
        {
            await worker.DisposeAsync();
        }
    }
}

public sealed class ExifToolWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    public bool IsBusy { get; set; }

    public ExifToolWorker(string? exifToolExecutable = null)
    {
        var exePath = exifToolExecutable ?? ResolveExifToolPath();

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-stay_open True -@ - -api QuickTimeUTC",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8
            }
        };

        _process.Start();
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
    }

    private static string ResolveExifToolPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, @"Programs\ExifTool\exiftool.exe"),
            @"C:\Program Files\ExifTool\exiftool.exe",
            @"C:\Program Files (x86)\ExifTool\exiftool.exe",
            @"C:\Windows\exiftool.exe",
            "exiftool"
        };

        foreach (var candidate in candidates)
        {
            if (candidate == "exiftool" || File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "exiftool";
    }

    public async Task ApplyMetadataAsync(
        string targetFilePath,
        DateTime timestamp,
        double? lat,
        double? lng,
        string? description,
        bool isVideo,
        bool isFavorited = false,
        IReadOnlyList<string>? albums = null)
    {
        var dt = timestamp.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);

        await _stdin.WriteLineAsync("-overwrite_original");
        await _stdin.WriteLineAsync($"-AllDates={dt}");
        await _stdin.WriteLineAsync($"-FileModifyDate={dt}");

        if (lat.HasValue && lng.HasValue)
        {
            await _stdin.WriteLineAsync($"-GPSLatitude={Math.Abs(lat.Value).ToString(CultureInfo.InvariantCulture)}");
            await _stdin.WriteLineAsync($"-GPSLatitudeRef={(lat.Value >= 0 ? "N" : "S")}");
            await _stdin.WriteLineAsync($"-GPSLongitude={Math.Abs(lng.Value).ToString(CultureInfo.InvariantCulture)}");
            await _stdin.WriteLineAsync($"-GPSLongitudeRef={(lng.Value >= 0 ? "E" : "W")}");
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            await _stdin.WriteLineAsync($"-ImageDescription={description.Replace("\n", " ").Replace("\r", "")}");
        }

        if (isFavorited)
        {
            await _stdin.WriteLineAsync("-Rating=5");
            await _stdin.WriteLineAsync("-RatingPercent=100");
        }

        if (albums is { Count: > 0 })
        {
            foreach (var album in albums)
            {
                var cleanAlbum = album.Replace("\n", " ").Replace("\r", "");
                await _stdin.WriteLineAsync($"-Keywords+={cleanAlbum}");
                await _stdin.WriteLineAsync($"-Subject+={cleanAlbum}");
            }
        }

        await _stdin.WriteLineAsync(targetFilePath);
        await _stdin.WriteLineAsync("-execute");
        await _stdin.FlushAsync();

        while (await _stdout.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("{ready}", StringComparison.Ordinal)) break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stdin.WriteLineAsync("-stay_open");
            await _stdin.WriteLineAsync("False");
            await _stdin.FlushAsync();
            await _process.WaitForExitAsync();
        }
        catch { }
        finally
        {
            _process.Dispose();
        }
    }
}
