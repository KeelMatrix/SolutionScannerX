using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SolutionScannerX;

internal static class Program
{
    private const string ConfigFileName = "solution_path_config.txt";
    private const string DumpFormatVersion = "3";
    private const string PreservedGitIgnoredCommand =
        "/d /c \"(git ls-files --cached & git ls-files -o -i --exclude-standard) ^| git check-ignore --no-index --stdin\"";

    private const int MaxTreeEntriesPerDirectory = 80;
    private const int MaxTreeDepth = 16;
    private const int MaxIgnoredPathsPerRenderedSubtree = 250;
    private const int MaxIgnoredPathChildrenPerDirectory = 80;
    private const int MaxIgnoredPathRenderDepth = 12;
    private const int MaxOtherNotIncludedItemsToWrite = 2000;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico",
        ".wasm", ".dll", ".exe", ".pdb", ".obj", ".bin",
        ".zip", ".7z", ".rar", ".gz", ".tar", ".nupkg",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".sqlite", ".sqlite3", ".db",
        ".mp3", ".wav", ".ogg", ".mp4", ".mov", ".avi", ".mkv",
        ".ttf", ".otf", ".woff", ".woff2",
        ".class", ".jar", ".so", ".dylib", ".a", ".lib"
    };

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        string solutionPath = GetSolutionPath(args);

        if (string.IsNullOrWhiteSpace(solutionPath) || !Directory.Exists(solutionPath))
        {
            Console.WriteLine("Invalid solution path provided or path does not exist. Exiting.");
            return;
        }

        solutionPath = Path.GetFullPath(solutionPath);

        string solutionName = new DirectoryInfo(solutionPath).Name;
        string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string outputFileName = Path.Combine(desktopPath, $"{solutionName}-{timestamp}.txt");

        Console.WriteLine();
        Console.WriteLine($"Repository root: {solutionPath}");
        Console.WriteLine($"Output file:     {outputFileName}");
        Console.WriteLine();

        GitIgnoredResult ignoredResult = await GetIgnoredFilesFromGit(solutionPath);
        HashSet<string> trackedGitIgnoreFiles = await GetTrackedGitIgnoreFiles(solutionPath);

        Console.WriteLine("Building compact tree /f-style directory overview.");
        TreeBuildResult treeResult = BuildCompactDirectoryTreeOutput(solutionPath, ignoredResult.IgnoredPaths, trackedGitIgnoreFiles);

        Console.WriteLine("Scanning repository files.");
        ScanResult scanResult = ScanRepository(solutionPath, ignoredResult.IgnoredPaths, trackedGitIgnoreFiles);

        Console.WriteLine($"Included text files:          {scanResult.IncludedFiles.Count}");
        Console.WriteLine($"Included tracked .gitignore:  {scanResult.IncludedFiles.Count(x => IsGitIgnorePath(x.RelativePath))}");
        Console.WriteLine($"Git ignored paths reported:   {ignoredResult.IgnoredPaths.Count}");
        Console.WriteLine($"Other scanner exclusions:     {scanResult.SkippedItems.Count}");
        Console.WriteLine($"Tree entries written:         {treeResult.WrittenEntries}");
        Console.WriteLine($"Tree entries compacted:       {treeResult.OmittedEntries}");
        Console.WriteLine();

        try
        {
            DumpWriteStats writeStats;

            await using (var writer = new StreamWriter(outputFileName, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writeStats = await WriteDump(
                    writer,
                    solutionPath,
                    solutionName,
                    ignoredResult,
                    trackedGitIgnoreFiles,
                    treeResult,
                    scanResult);
            }

            long outputFileSizeBytes = new FileInfo(outputFileName).Length;

            Console.WriteLine();
            Console.WriteLine("Dump created successfully.");
            Console.WriteLine($"Path:             {outputFileName}");
            Console.WriteLine($"Dump file size:   {FormatByteSize(outputFileSizeBytes)}");
            Console.WriteLine($"Files written:    {writeStats.FilesWritten}");
            Console.WriteLine($"Read errors:      {writeStats.ReadErrors}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Processing failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }

        Console.WriteLine();
        Console.WriteLine("Program finished. Press any key to exit.");
        Console.ReadKey();
    }

    private static string GetSolutionPath(string[] args)
    {
        string configFilePath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            string argumentPath = args[0].Trim();

            if (!Directory.Exists(argumentPath))
            {
                Console.WriteLine($"Command-line path is invalid: {argumentPath}");
                return string.Empty;
            }

            SavePath(configFilePath, argumentPath);
            return argumentPath;
        }

        string? existingPath = ReadSavedPath(configFilePath);

        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            Console.WriteLine($"Saved path: {existingPath}");
            Console.Write("Press Enter to use it, or type a new path: ");
            string? typed = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(typed))
            {
                return existingPath;
            }

            string newTyped = typed.Trim();

            while (!Directory.Exists(newTyped))
            {
                Console.Write("Invalid path. Enter a valid full path: ");
                newTyped = (Console.ReadLine() ?? string.Empty).Trim();
            }

            SavePath(configFilePath, newTyped);
            return newTyped;
        }

        while (true)
        {
            Console.WriteLine("Enter the full path to the solution or repository folder:");
            string candidate = (Console.ReadLine() ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                SavePath(configFilePath, candidate);
                return candidate;
            }

            Console.WriteLine("Invalid path or path does not exist. Please try again.");
        }
    }

    private static string? ReadSavedPath(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            return null;
        }

        try
        {
            string existingPath = File.ReadAllText(configFilePath).Trim();

            if (Directory.Exists(existingPath))
            {
                return existingPath;
            }

            Console.WriteLine($"Stored path '{existingPath}' not found or is invalid.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading config file: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void SavePath(string configFilePath, string path)
    {
        try
        {
            File.WriteAllText(configFilePath, path);
            Console.WriteLine($"Path saved to {configFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving path to config file: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<GitIgnoredResult> GetIgnoredFilesFromGit(string solutionPath)
    {
        var ignoredItems = new HashSet<string>(PathComparer);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = PreservedGitIgnoredCommand,
            WorkingDirectory = solutionPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Console.WriteLine($"Running 'git {processStartInfo.Arguments}' in '{solutionPath}'...");

        ProcessCapture result = await RunProcess(processStartInfo);

        foreach (string line in ReadOutputLines(result.Output))
        {
            string normalizedPath = NormalizeGitPath(line);

            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                ignoredItems.Add(normalizedPath);
            }
        }

        bool noIgnoredPathsExit = result.ExitCode == 1 && ignoredItems.Count == 0;
        bool successfulEnough = result.ExitCode == 0 || noIgnoredPathsExit || ignoredItems.Count > 0;

        if (successfulEnough)
        {
            Console.WriteLine($"Found {ignoredItems.Count} ignored items via git.");
        }
        else
        {
            Console.WriteLine($"Git command exited with error code {FormatExitCode(result.ExitCode)}.");

            if (!string.IsNullOrWhiteSpace(result.ErrorOutput))
            {
                Console.WriteLine($"Git error output: {result.ErrorOutput.Trim()}");
            }

            Console.WriteLine("Warning: ignored files could not be determined reliably.");
        }

        return new GitIgnoredResult(ignoredItems, result.ExitCode, result.ErrorOutput);
    }

    private static async Task<HashSet<string>> GetTrackedGitIgnoreFiles(string solutionPath)
    {
        var trackedGitIgnoreFiles = new HashSet<string>(PathComparer);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c \"git ls-files\"",
            WorkingDirectory = solutionPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        ProcessCapture result = await RunProcess(processStartInfo);

        if (result.ExitCode != 0)
        {
            Console.WriteLine("Warning: tracked .gitignore files could not be determined. Non-ignored .gitignore files will still be included.");
            return trackedGitIgnoreFiles;
        }

        foreach (string line in ReadOutputLines(result.Output))
        {
            string normalizedPath = NormalizeGitPath(line);

            if (IsGitIgnorePath(normalizedPath))
            {
                trackedGitIgnoreFiles.Add(normalizedPath);
            }
        }

        Console.WriteLine($"Tracked .gitignore files found: {trackedGitIgnoreFiles.Count}");
        return trackedGitIgnoreFiles;
    }

    private static TreeBuildResult BuildCompactDirectoryTreeOutput(
        string solutionPath,
        HashSet<string> ignoredItems,
        HashSet<string> trackedGitIgnoreFiles)
    {
        var output = new StringBuilder();
        var stats = new TreeBuildStats();
        string rootName = new DirectoryInfo(solutionPath).Name;

        output.AppendLine(rootName + "/");
        RenderTreeDirectory(solutionPath, solutionPath, ignoredItems, trackedGitIgnoreFiles, output, "  ", 0, stats);

        return new TreeBuildResult(
            output.ToString(),
            stats.WrittenEntries,
            stats.OmittedEntries,
            stats.CompactedIgnoredSubtrees,
            stats.MaxDepthOmissions,
            stats.ErrorCount,
            MaxTreeEntriesPerDirectory,
            MaxTreeDepth);
    }

    private static void RenderTreeDirectory(
        string directoryPath,
        string solutionPath,
        HashSet<string> ignoredItems,
        HashSet<string> trackedGitIgnoreFiles,
        StringBuilder output,
        string indent,
        int depth,
        TreeBuildStats stats)
    {
        if (depth >= MaxTreeDepth)
        {
            output.AppendLine($"{indent}... [tree depth cap reached; subtree omitted]");
            stats.OmittedEntries++;
            stats.MaxDepthOmissions++;
            return;
        }

        List<TreeEntry> entries;

        try
        {
            entries = Directory.GetDirectories(directoryPath)
                .Select(path => CreateTreeEntry(path, solutionPath, isDirectory: true, ignoredItems))
                .Concat(Directory.GetFiles(directoryPath)
                    .Select(path => CreateTreeEntry(path, solutionPath, isDirectory: false, ignoredItems)))
                .OrderBy(entry => entry.IsIgnored)
                .ThenBy(entry => entry.IsDirectory ? 0 : 1)
                .ThenBy(entry => entry.IsDirectory ? 0 : GetFileSortGroup(entry.RelativePath))
                .ThenBy(entry => entry.Name, PathComparer)
                .ToList();
        }
        catch (Exception ex)
        {
            output.AppendLine($"{indent}[tree enumeration error: {ex.GetType().Name}: {ex.Message}]");
            stats.ErrorCount++;
            return;
        }

        int writtenInDirectory = 0;

        foreach (TreeEntry entry in entries)
        {
            if (writtenInDirectory >= MaxTreeEntriesPerDirectory)
            {
                int omitted = entries.Count - writtenInDirectory;
                output.AppendLine($"{indent}... [omitted {omitted} more entries in this directory; TREE_MAX_ENTRIES_PER_DIRECTORY={MaxTreeEntriesPerDirectory}]");
                stats.OmittedEntries += omitted;
                break;
            }

            writtenInDirectory++;
            stats.WrittenEntries++;

            if (entry.IsDirectory)
            {
                if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    output.AppendLine($"{indent}.git/ [git metadata omitted]");
                    stats.OmittedEntries++;
                    continue;
                }

                if (entry.IsReparsePoint)
                {
                    output.AppendLine($"{indent}{entry.Name}/ [reparse point omitted]");
                    stats.OmittedEntries++;
                    continue;
                }

                bool canCompactIgnoredDirectory = entry.IsIgnored && !HasTrackedGitIgnoreUnderDirectory(trackedGitIgnoreFiles, entry.RelativePath);

                if (canCompactIgnoredDirectory)
                {
                    DirectChildCount childCount = CountDirectChildren(entry.FullPath);
                    output.AppendLine($"{indent}{entry.Name}/ [ignored subtree compacted; direct_dirs={childCount.DirectoriesText}; direct_files={childCount.FilesText}]");
                    stats.CompactedIgnoredSubtrees++;
                    stats.OmittedEntries += childCount.KnownTotal;
                    continue;
                }

                output.AppendLine($"{indent}{entry.Name}/");
                RenderTreeDirectory(entry.FullPath, solutionPath, ignoredItems, trackedGitIgnoreFiles, output, indent + "  ", depth + 1, stats);
            }
            else
            {
                output.AppendLine(entry.IsIgnored
                    ? $"{indent}{entry.Name} [ignored file; content not dumped]"
                    : $"{indent}{entry.Name}");
            }
        }
    }

    private static TreeEntry CreateTreeEntry(string path, string solutionPath, bool isDirectory, HashSet<string> ignoredItems)
    {
        string relativePath = NormalizeRelativePath(solutionPath, path);
        string name = Path.GetFileName(path);
        bool isIgnored = IsPathIgnoredByGit(relativePath, ignoredItems);
        bool isReparsePoint = false;

        if (isDirectory)
        {
            try
            {
                isReparsePoint = IsReparsePoint(new DirectoryInfo(path).Attributes);
            }
            catch
            {
                isReparsePoint = false;
            }
        }

        return new TreeEntry(path, relativePath, name, isDirectory, isIgnored, isReparsePoint);
    }

    private static DirectChildCount CountDirectChildren(string directoryPath)
    {
        try
        {
            int directories = Directory.GetDirectories(directoryPath).Length;
            int files = Directory.GetFiles(directoryPath).Length;
            return DirectChildCount.Known(directories, files);
        }
        catch
        {
            return DirectChildCount.Unknown();
        }
    }

    private static ScanResult ScanRepository(
        string solutionPath,
        HashSet<string> ignoredItems,
        HashSet<string> trackedGitIgnoreFiles)
    {
        var includedFiles = new List<FileEntry>();
        var skippedItems = new List<SkippedItem>();

        CollectDirectory(solutionPath, solutionPath, ignoredItems, trackedGitIgnoreFiles, includedFiles, skippedItems);

        List<FileEntry> sortedIncludedFiles = [.. includedFiles
            .OrderBy(x => GetFileSortGroup(x.RelativePath))
            .ThenBy(x => x.RelativePath, PathComparer)];

        List<SkippedItem> sortedSkippedItems = [.. skippedItems
            .OrderBy(x => x.RelativePath, PathComparer)
            .ThenBy(x => x.Reason, StringComparer.Ordinal)];

        return new ScanResult(sortedIncludedFiles, sortedSkippedItems);
    }

    private static void CollectDirectory(
        string currentDirectory,
        string solutionPath,
        HashSet<string> ignoredItems,
        HashSet<string> trackedGitIgnoreFiles,
        List<FileEntry> includedFiles,
        List<SkippedItem> skippedItems)
    {
        DirectoryInfo directoryInfo;

        try
        {
            directoryInfo = new DirectoryInfo(currentDirectory);
        }
        catch (Exception ex)
        {
            skippedItems.Add(new SkippedItem(
                NormalizeRelativePathSafe(solutionPath, currentDirectory),
                "DIRECTORY_INFO_ERROR",
                $"{ex.GetType().Name}: {ex.Message}"));
            return;
        }

        if (directoryInfo.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            skippedItems.Add(new SkippedItem(
                EnsureTrailingSlash(NormalizeRelativePathSafe(solutionPath, currentDirectory)),
                "GIT_METADATA_DIRECTORY",
                "Git internal metadata is never dumped as file content."));
            return;
        }

        if (IsReparsePoint(directoryInfo.Attributes))
        {
            skippedItems.Add(new SkippedItem(
                EnsureTrailingSlash(NormalizeRelativePathSafe(solutionPath, currentDirectory)),
                "REPARSE_POINT_DIRECTORY",
                "Skipped to avoid symlink or junction loops."));
            return;
        }

        string relativeDirectoryPath = NormalizeRelativePath(solutionPath, currentDirectory);

        if (relativeDirectoryPath != "." &&
            IsPathIgnoredByGit(relativeDirectoryPath, ignoredItems) &&
            !HasTrackedGitIgnoreUnderDirectory(trackedGitIgnoreFiles, relativeDirectoryPath))
        {
            return;
        }

        foreach (string filePath in SafeGetFiles(currentDirectory, solutionPath, skippedItems))
        {
            CollectFile(filePath, solutionPath, ignoredItems, trackedGitIgnoreFiles, includedFiles, skippedItems);
        }

        foreach (string directoryPath in SafeGetDirectories(currentDirectory, solutionPath, skippedItems))
        {
            CollectDirectory(directoryPath, solutionPath, ignoredItems, trackedGitIgnoreFiles, includedFiles, skippedItems);
        }
    }

    private static void CollectFile(
        string filePath,
        string solutionPath,
        HashSet<string> ignoredItems,
        HashSet<string> trackedGitIgnoreFiles,
        List<FileEntry> includedFiles,
        List<SkippedItem> skippedItems)
    {
        string relativeFilePath = NormalizeRelativePath(solutionPath, filePath);
        bool isTrackedGitIgnore = trackedGitIgnoreFiles.Contains(relativeFilePath);

        if (IsPathIgnoredByGit(relativeFilePath, ignoredItems) && !isTrackedGitIgnore)
        {
            return;
        }

        FileInfo fileInfo;

        try
        {
            fileInfo = new FileInfo(filePath);
        }
        catch (Exception ex)
        {
            skippedItems.Add(new SkippedItem(relativeFilePath, "FILE_INFO_ERROR", $"{ex.GetType().Name}: {ex.Message}"));
            return;
        }

        if (!isTrackedGitIgnore && BinaryExtensions.Contains(fileInfo.Extension))
        {
            skippedItems.Add(new SkippedItem(relativeFilePath, "BINARY_EXTENSION", $"Extension '{fileInfo.Extension}' is excluded from text dumps."));
            return;
        }

        if (!isTrackedGitIgnore)
        {
            BinaryProbeResult binaryProbe = LooksBinaryBySample(filePath);

            if (!binaryProbe.Success)
            {
                skippedItems.Add(new SkippedItem(relativeFilePath, "READ_CHECK_FAILED", binaryProbe.ErrorMessage ?? "Could not inspect file."));
                return;
            }

            if (binaryProbe.IsBinary)
            {
                skippedItems.Add(new SkippedItem(relativeFilePath, "BINARY_CONTENT", "NUL bytes detected in file sample."));
                return;
            }
        }

        includedFiles.Add(new FileEntry(filePath, relativeFilePath));
    }

    private static async Task<DumpWriteStats> WriteDump(
        StreamWriter writer,
        string solutionPath,
        string solutionName,
        GitIgnoredResult ignoredResult,
        HashSet<string> trackedGitIgnoreFiles,
        TreeBuildResult treeResult,
        ScanResult scanResult)
    {
        int filesWritten = 0;
        int readErrors = 0;
        CompactPathListResult compactIgnoredPaths = BuildCompactIgnoredPathList(ignoredResult.IgnoredPaths);

        await WriteMainHeader(writer, solutionPath, solutionName, ignoredResult, trackedGitIgnoreFiles, treeResult, compactIgnoredPaths, scanResult);
        await WriteDirectoryTreeSection(writer, treeResult);
        await WriteNotIncludedSection(writer, ignoredResult, compactIgnoredPaths, scanResult);
        await WriteIncludedManifestSection(writer, scanResult.IncludedFiles);

        await WriteSectionStart(writer, "DUMPED FILE CONTENTS");

        foreach (FileEntry file in scanResult.IncludedFiles)
        {
            filesWritten++;

            bool success = await WriteFileBlock(writer, file, filesWritten);

            if (!success)
            {
                readErrors++;
            }

            if (filesWritten % 100 == 0)
            {
                Console.WriteLine($"Written {filesWritten}/{scanResult.IncludedFiles.Count} files.");
            }
        }

        await WriteSectionEnd(writer, "DUMPED FILE CONTENTS");

        await writer.WriteLineAsync("========== DUMP END ==========");

        return new DumpWriteStats(filesWritten, readErrors);
    }

    private static async Task WriteMainHeader(
        StreamWriter writer,
        string solutionPath,
        string solutionName,
        GitIgnoredResult ignoredResult,
        HashSet<string> trackedGitIgnoreFiles,
        TreeBuildResult treeResult,
        CompactPathListResult compactIgnoredPaths,
        ScanResult scanResult)
    {
        DateTimeOffset now = DateTimeOffset.Now;

        await writer.WriteLineAsync("========== DUMP START ==========");
        await writer.WriteLineAsync($"FORMAT_VERSION: {DumpFormatVersion}");
        await writer.WriteLineAsync($"ROOT_DIRECTORY_NAME: {solutionName}");
        await writer.WriteLineAsync($"ROOT_ABSOLUTE_PATH: {solutionPath}");
        await writer.WriteLineAsync($"GENERATED_LOCAL: {now:O}");
        await writer.WriteLineAsync($"GENERATED_UTC: {now.ToUniversalTime():O}");
        await writer.WriteLineAsync($"INCLUDED_TEXT_FILE_COUNT: {scanResult.IncludedFiles.Count}");
        await writer.WriteLineAsync($"TRACKED_GITIGNORE_INCLUDED_COUNT: {scanResult.IncludedFiles.Count(x => IsGitIgnorePath(x.RelativePath))}");
        await writer.WriteLineAsync($"TRACKED_GITIGNORE_FOUND_COUNT: {trackedGitIgnoreFiles.Count}");
        await writer.WriteLineAsync($"GIT_IGNORED_REPORTED_COUNT: {ignoredResult.IgnoredPaths.Count}");
        await writer.WriteLineAsync($"GIT_IGNORED_RENDERED_ENTRIES: {compactIgnoredPaths.WrittenEntries}");
        await writer.WriteLineAsync($"GIT_IGNORED_COMPACTED_SUBTREES: {compactIgnoredPaths.CompactedSubtrees}");
        await writer.WriteLineAsync($"GIT_IGNORED_COMPACTED_PATH_COUNT: {compactIgnoredPaths.OmittedPathCount}");
        await writer.WriteLineAsync($"OTHER_NOT_INCLUDED_COUNT: {scanResult.SkippedItems.Count}");
        await writer.WriteLineAsync($"TREE_ENTRIES_WRITTEN: {treeResult.WrittenEntries}");
        await writer.WriteLineAsync($"TREE_ENTRIES_COMPACTED_OR_OMITTED: {treeResult.OmittedEntries}");
        await writer.WriteLineAsync();

        await WriteSectionStart(writer, "AI NAVIGATION HEADER");
        await writer.WriteLineAsync("PURPOSE:");
        await writer.WriteLineAsync("This file is a repository/application text dump intended for AI code review, debugging, refactoring, and navigation.");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("PARSING_GUIDELINES:");
        await writer.WriteLineAsync("1. Paths are repository-relative and normalized with forward slashes.");
        await writer.WriteLineAsync("2. The DIRECTORY TREE section is a compact tree /f-style filesystem view, not a git file list. It may omit high-volume ignored subtrees and capped directory overflow.");
        await writer.WriteLineAsync("3. The NOT INCLUDED section is the scope boundary. Compacted ignored subtrees are explicit omissions; do not infer their contents from nearby dumped files.");
        await writer.WriteLineAsync("4. Each dumped file is enclosed by FILE START / CONTENT START / CONTENT END / FILE END markers.");
        await writer.WriteLineAsync("5. Dumped file content lines are prefixed as '000001 | '. Line numbers restart at 1 for each file and are not part of the original file content.");
        await writer.WriteLineAsync("6. Prefer RELATIVE_PATH metadata over any path-like text inside file contents.");
        await writer.WriteLineAsync("7. .gitignore files tracked by git are intentionally included even if ignore matching would otherwise exclude them.");
        await writer.WriteLineAsync("8. The preserved git ignored-file command is listed in the NOT INCLUDED section.");
        await writer.WriteLineAsync("9. Remove the fixed-width line-number prefix before applying patches or reconstructing a source file from this dump.");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("RECOMMENDED_AI_WORKFLOW:");
        await writer.WriteLineAsync("1. Read this header.");
        await writer.WriteLineAsync("2. Use INCLUDED FILE MANIFEST to identify relevant dumped files.");
        await writer.WriteLineAsync("3. Use DIRECTORY TREE to discover nearby tracked, untracked, ignored, and generated paths without reading every file block.");
        await writer.WriteLineAsync("4. Use NOT INCLUDED PATHS before assuming a file is absent from the real repo.");
        await writer.WriteLineAsync("5. Read only needed FILE blocks to reduce token use.");
        await writer.WriteLineAsync("6. Do not include direct references of this file or code line numbers in your response / generated code unless user explicitly says otherwise, OR if such references would be HIGHLY relevant and helpful to the user.");
        await WriteSectionEnd(writer, "AI NAVIGATION HEADER");
    }

    private static async Task WriteDirectoryTreeSection(StreamWriter writer, TreeBuildResult treeResult)
    {
        await WriteSectionStart(writer, "DIRECTORY TREE");

        await writer.WriteLineAsync("AI_NOTE: Compact tree /f-style output generated by filesystem enumeration. It includes tracked and untracked paths, and marks ignored paths when detected.");
        await writer.WriteLineAsync("AI_NOTE: Raw PowerShell 'tree /f' output is intentionally not dumped when it would explode token count; high-volume ignored subtrees are compacted instead.");
        await writer.WriteLineAsync("POWERSHELL_COMMAND_REFERENCE: powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; tree /f\"");
        await writer.WriteLineAsync($"TREE_MAX_ENTRIES_PER_DIRECTORY: {treeResult.MaxEntriesPerDirectory}");
        await writer.WriteLineAsync($"TREE_MAX_DEPTH: {treeResult.MaxDepth}");
        await writer.WriteLineAsync($"TREE_ENTRIES_WRITTEN: {treeResult.WrittenEntries}");
        await writer.WriteLineAsync($"TREE_ENTRIES_COMPACTED_OR_OMITTED: {treeResult.OmittedEntries}");
        await writer.WriteLineAsync($"TREE_IGNORED_SUBTREES_COMPACTED: {treeResult.CompactedIgnoredSubtrees}");
        await writer.WriteLineAsync($"TREE_DEPTH_OMISSIONS: {treeResult.MaxDepthOmissions}");
        await writer.WriteLineAsync($"TREE_ENUMERATION_ERRORS: {treeResult.ErrorCount}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("TREE_OUTPUT_START");
        await writer.WriteAsync(treeResult.Output);

        if (treeResult.Output.Length == 0 || treeResult.Output[^1] != '\n')
        {
            await writer.WriteLineAsync();
        }

        await writer.WriteLineAsync("TREE_OUTPUT_END");
        await WriteSectionEnd(writer, "DIRECTORY TREE");
    }

    private static async Task WriteNotIncludedSection(
        StreamWriter writer,
        GitIgnoredResult ignoredResult,
        CompactPathListResult compactIgnoredPaths,
        ScanResult scanResult)
    {
        await WriteSectionStart(writer, "NOT INCLUDED PATHS");

        await writer.WriteLineAsync("AI_NOTE: These paths were not dumped as file contents. Compacted subtree lines are intentional scope summaries for large ignored path sets.");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("PRESERVED_GIT_IGNORED_COMMAND:");
        await writer.WriteLineAsync($"cmd.exe {PreservedGitIgnoredCommand}");
        await writer.WriteLineAsync($"GIT_COMMAND_EXIT_CODE: {FormatExitCode(ignoredResult.ExitCode)}");

        if (!string.IsNullOrWhiteSpace(ignoredResult.ErrorOutput))
        {
            await writer.WriteLineAsync("GIT_COMMAND_STDERR:");
            await writer.WriteLineAsync(ignoredResult.ErrorOutput.TrimEnd());
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"GIT_IGNORED_PATHS_COUNT: {compactIgnoredPaths.TotalInputPaths}");
        await writer.WriteLineAsync($"GIT_IGNORED_RENDERED_ENTRIES: {compactIgnoredPaths.WrittenEntries}");
        await writer.WriteLineAsync($"GIT_IGNORED_COMPACTED_SUBTREES: {compactIgnoredPaths.CompactedSubtrees}");
        await writer.WriteLineAsync($"GIT_IGNORED_COMPACTED_PATH_COUNT: {compactIgnoredPaths.OmittedPathCount}");
        await writer.WriteLineAsync("GIT_IGNORED_PATHS_START");
        await writer.WriteAsync(compactIgnoredPaths.Output);

        if (compactIgnoredPaths.Output.Length == 0 || compactIgnoredPaths.Output[^1] != '\n')
        {
            await writer.WriteLineAsync();
        }

        await writer.WriteLineAsync("GIT_IGNORED_PATHS_END");
        await writer.WriteLineAsync();

        await writer.WriteLineAsync($"OTHER_NOT_INCLUDED_COUNT: {scanResult.SkippedItems.Count}");
        await writer.WriteLineAsync($"OTHER_NOT_INCLUDED_MAX_RENDERED: {MaxOtherNotIncludedItemsToWrite}");
        await writer.WriteLineAsync("OTHER_NOT_INCLUDED_START");
        await writer.WriteLineAsync("REASON\tRELATIVE_PATH\tDETAIL");
        await WriteSkippedItems(writer, scanResult.SkippedItems);
        await writer.WriteLineAsync("OTHER_NOT_INCLUDED_END");

        await WriteSectionEnd(writer, "NOT INCLUDED PATHS");
    }

    private static async Task WriteSkippedItems(StreamWriter writer, IReadOnlyList<SkippedItem> skippedItems)
    {
        if (skippedItems.Count == 0)
        {
            await writer.WriteLineAsync("(none)");
            return;
        }

        foreach (SkippedItem item in skippedItems.Take(MaxOtherNotIncludedItemsToWrite))
        {
            await writer.WriteLineAsync($"{item.Reason}\t{item.RelativePath}\t{item.Detail}");
        }

        if (skippedItems.Count <= MaxOtherNotIncludedItemsToWrite)
        {
            return;
        }

        await writer.WriteLineAsync($"COMPACTED\t...\tOmitted {skippedItems.Count - MaxOtherNotIncludedItemsToWrite} additional scanner-excluded items. Counts by reason follow.");

        foreach (var group in skippedItems
            .Skip(MaxOtherNotIncludedItemsToWrite)
            .GroupBy(item => item.Reason)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            await writer.WriteLineAsync($"COMPACTED_SUMMARY\t{group.Key}\t{group.Count()} omitted items");
        }
    }

    private static async Task WriteIncludedManifestSection(StreamWriter writer, IReadOnlyList<FileEntry> includedFiles)
    {
        await WriteSectionStart(writer, "INCLUDED FILE MANIFEST");

        await writer.WriteLineAsync("AI_NOTE: File contents appear later in this same order.");
        await writer.WriteLineAsync("COLUMNS: INDEX, SIZE_BYTES, LAST_WRITE_UTC, RELATIVE_PATH");
        await writer.WriteLineAsync();

        for (int i = 0; i < includedFiles.Count; i++)
        {
            FileEntry file = includedFiles[i];
            FileMetadata metadata = GetFileMetadata(file.FullPath);

            await writer.WriteLineAsync(
                $"{i + 1:D5}\t{metadata.SizeBytesText}\t{metadata.LastWriteUtcText}\t{file.RelativePath}");
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("GITIGNORE_FILES_INCLUDED_START");

        foreach (FileEntry file in includedFiles.Where(x => IsGitIgnorePath(x.RelativePath)))
        {
            await writer.WriteLineAsync(file.RelativePath);
        }

        if (!includedFiles.Any(x => IsGitIgnorePath(x.RelativePath)))
        {
            await writer.WriteLineAsync("(none)");
        }

        await writer.WriteLineAsync("GITIGNORE_FILES_INCLUDED_END");

        await WriteSectionEnd(writer, "INCLUDED FILE MANIFEST");
    }

    private static async Task<bool> WriteFileBlock(StreamWriter writer, FileEntry file, int index)
    {
        await writer.WriteLineAsync($"========== FILE START: {file.RelativePath} ==========");
        await writer.WriteLineAsync($"FILE_INDEX: {index:D5}");
        await writer.WriteLineAsync($"RELATIVE_PATH: {file.RelativePath}");

        FileMetadata metadata = GetFileMetadata(file.FullPath);
        await writer.WriteLineAsync($"SIZE_BYTES: {metadata.SizeBytesText}");
        await writer.WriteLineAsync($"LAST_WRITE_UTC: {metadata.LastWriteUtcText}");

        FileContentReadResult readResult = await ReadFileContent(file.FullPath);

        await writer.WriteLineAsync($"READ_STATUS: {(readResult.Success ? "OK" : "ERROR")}");
        await writer.WriteLineAsync($"DETECTED_ENCODING: {readResult.EncodingName}");
        await writer.WriteLineAsync($"LINE_COUNT: {readResult.LineCountText}");
        await writer.WriteLineAsync("LINE_NUMBER_FORMAT: 000001 | <original line text>");
        await writer.WriteLineAsync($"SHA256: {readResult.Sha256Text}");
        await writer.WriteLineAsync("========== CONTENT START ==========");

        if (readResult.Success)
        {
            await WriteNumberedContent(writer, readResult.Content);
        }
        else
        {
            await writer.WriteLineAsync($"000001 | [SOLUTIONSCANNERX_READ_ERROR] {readResult.ErrorMessage}");
        }

        await writer.WriteLineAsync("========== CONTENT END ==========");
        await writer.WriteLineAsync($"========== FILE END: {file.RelativePath} ==========");
        await writer.WriteLineAsync();

        return readResult.Success;
    }

    private static async Task WriteNumberedContent(StreamWriter writer, string content)
    {
        using var reader = new StringReader(content);
        int lineNumber = 1;

        while (reader.ReadLine() is { } line)
        {
            await writer.WriteLineAsync($"{lineNumber:D6} | {line}");
            lineNumber++;
        }
    }

    private static async Task<FileContentReadResult> ReadFileContent(string filePath)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath);
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            await using var memoryStream = new MemoryStream(bytes);
            using var reader = new StreamReader(memoryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string content = await reader.ReadToEndAsync();
            string encodingName = reader.CurrentEncoding.WebName;

            return FileContentReadResult.Ok(
                content,
                encodingName,
                CountLines(content),
                sha256);
        }
        catch (Exception ex)
        {
            return FileContentReadResult.Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static FileMetadata GetFileMetadata(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            return new FileMetadata(
                fileInfo.Exists ? fileInfo.Length.ToString() : "MISSING",
                fileInfo.Exists ? fileInfo.LastWriteTimeUtc.ToString("O") : "MISSING");
        }
        catch (Exception ex)
        {
            return new FileMetadata("ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static BinaryProbeResult LooksBinaryBySample(string filePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] buffer = new byte[8192];
            int read = stream.Read(buffer, 0, buffer.Length);

            if (read == 0)
            {
                return BinaryProbeResult.Text();
            }

            if (HasTextBom(buffer, read))
            {
                return BinaryProbeResult.Text();
            }

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return BinaryProbeResult.Binary();
                }
            }

            return BinaryProbeResult.Text();
        }
        catch (Exception ex)
        {
            return BinaryProbeResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool HasTextBom(byte[] bytes, int length)
    {
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return true;
        }

        if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return true;
        }

        if (length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return true;
        }

        if (length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return true;
        }

        if (length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return true;
        }

        return false;
    }

    private static async Task<ProcessCapture> RunProcess(ProcessStartInfo processStartInfo)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = processStartInfo
            };

            bool started = process.Start();

            if (!started)
            {
                return ProcessCapture.NotStarted("Process.Start returned false.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string output = await outputTask;
            string error = await errorTask;

            return new ProcessCapture(true, process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return ProcessCapture.NotStarted($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string[] SafeGetFiles(string directoryPath, string solutionPath, List<SkippedItem> skippedItems)
    {
        try
        {
            return Directory.GetFiles(directoryPath);
        }
        catch (Exception ex)
        {
            skippedItems.Add(new SkippedItem(
                EnsureTrailingSlash(NormalizeRelativePathSafe(solutionPath, directoryPath)),
                "FILE_ENUMERATION_FAILED",
                $"{ex.GetType().Name}: {ex.Message}"));
            return [];
        }
    }

    private static string[] SafeGetDirectories(string directoryPath, string solutionPath, List<SkippedItem> skippedItems)
    {
        try
        {
            return Directory.GetDirectories(directoryPath);
        }
        catch (Exception ex)
        {
            skippedItems.Add(new SkippedItem(
                EnsureTrailingSlash(NormalizeRelativePathSafe(solutionPath, directoryPath)),
                "DIRECTORY_ENUMERATION_FAILED",
                $"{ex.GetType().Name}: {ex.Message}"));
            return [];
        }
    }

    private static CompactPathListResult BuildCompactIgnoredPathList(HashSet<string> ignoredPaths)
    {
        if (ignoredPaths.Count == 0)
        {
            return new CompactPathListResult("(none)" + Environment.NewLine, 1, 0, 0, 0);
        }

        var root = new PathTrieNode();

        foreach (string ignoredPath in ignoredPaths.OrderBy(path => path, PathComparer))
        {
            string normalized = NormalizeGitPath(ignoredPath).TrimEnd('/');

            if (string.IsNullOrWhiteSpace(normalized) || normalized == ".")
            {
                continue;
            }

            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            PathTrieNode current = root;
            current.LeafCount++;

            foreach (string part in parts)
            {
                current = current.GetOrAdd(part);
                current.LeafCount++;
            }

            current.IsTerminal = true;
        }

        var output = new StringBuilder();
        var stats = new CompactPathListStats();

        foreach (KeyValuePair<string, PathTrieNode> child in root.Children.OrderBy(x => x.Key, PathComparer))
        {
            RenderCompactIgnoredPathNode(child.Key, child.Value, child.Key, depth: 1, output, stats);
        }

        return new CompactPathListResult(
            output.ToString(),
            stats.WrittenEntries,
            stats.CompactedSubtrees,
            stats.OmittedPathCount,
            ignoredPaths.Count);
    }

    private static void RenderCompactIgnoredPathNode(
        string name,
        PathTrieNode node,
        string path,
        int depth,
        StringBuilder output,
        CompactPathListStats stats)
    {
        bool hasChildren = node.Children.Count > 0;
        string displayPath = hasChildren ? path + "/" : path;

        if (hasChildren && depth >= 1 && node.LeafCount > MaxIgnoredPathsPerRenderedSubtree)
        {
            output.AppendLine($"{displayPath} [compacted ignored subtree: {node.LeafCount} ignored paths]");
            stats.WrittenEntries++;
            stats.CompactedSubtrees++;
            stats.OmittedPathCount += node.LeafCount;
            return;
        }

        if (node.IsTerminal)
        {
            output.AppendLine(path);
            stats.WrittenEntries++;
        }

        if (!hasChildren)
        {
            return;
        }

        if (depth >= MaxIgnoredPathRenderDepth)
        {
            output.AppendLine($"{path}/... [compacted by depth: {node.LeafCount} ignored paths under this prefix]");
            stats.WrittenEntries++;
            stats.CompactedSubtrees++;
            stats.OmittedPathCount += node.LeafCount;
            return;
        }

        int childIndex = 0;
        int omittedChildCount = 0;
        int omittedPathCount = 0;

        foreach (KeyValuePair<string, PathTrieNode> child in node.Children.OrderBy(x => x.Key, PathComparer))
        {
            if (childIndex >= MaxIgnoredPathChildrenPerDirectory)
            {
                omittedChildCount++;
                omittedPathCount += child.Value.LeafCount;
                continue;
            }

            string childPath = path + "/" + child.Key;
            RenderCompactIgnoredPathNode(child.Key, child.Value, childPath, depth + 1, output, stats);
            childIndex++;
        }

        if (omittedChildCount > 0)
        {
            output.AppendLine($"{path}/... [compacted {omittedChildCount} sibling subtrees containing {omittedPathCount} ignored paths]");
            stats.WrittenEntries++;
            stats.CompactedSubtrees += omittedChildCount;
            stats.OmittedPathCount += omittedPathCount;
        }
    }

    private static bool IsPathIgnoredByGit(string relativePath, HashSet<string> ignoredItems)
    {
        string normalized = NormalizeGitPath(relativePath);

        if (string.IsNullOrWhiteSpace(normalized) || normalized == ".")
        {
            return false;
        }

        string withoutTrailingSlash = normalized.TrimEnd('/');

        if (ignoredItems.Contains(normalized) ||
            ignoredItems.Contains(withoutTrailingSlash) ||
            ignoredItems.Contains(withoutTrailingSlash + "/"))
        {
            return true;
        }

        string current = withoutTrailingSlash;

        while (true)
        {
            int slashIndex = current.LastIndexOf('/');

            if (slashIndex <= 0)
            {
                return false;
            }

            current = current[..slashIndex];

            if (ignoredItems.Contains(current) || ignoredItems.Contains(current + "/"))
            {
                return true;
            }
        }
    }

    private static bool HasTrackedGitIgnoreUnderDirectory(HashSet<string> trackedGitIgnoreFiles, string relativeDirectoryPath)
    {
        string directory = NormalizeGitPath(relativeDirectoryPath).TrimEnd('/');

        if (string.IsNullOrWhiteSpace(directory) || directory == ".")
        {
            return trackedGitIgnoreFiles.Count > 0;
        }

        string prefix = directory + "/";

        return trackedGitIgnoreFiles.Any(path => path.StartsWith(prefix, PathComparison));
    }

    private static string NormalizeRelativePath(string rootPath, string path)
    {
        string relativePath = Path.GetRelativePath(rootPath, path);
        return NormalizeGitPath(relativePath);
    }

    private static string NormalizeRelativePathSafe(string rootPath, string path)
    {
        try
        {
            return NormalizeRelativePath(rootPath, path);
        }
        catch
        {
            return NormalizeGitPath(path);
        }
    }

    private static string NormalizeGitPath(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');

        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static bool IsGitIgnorePath(string relativePath)
    {
        return GetFileNameFromNormalizedPath(relativePath).Equals(".gitignore", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFileNameFromNormalizedPath(string relativePath)
    {
        string normalized = NormalizeGitPath(relativePath);
        int slashIndex = normalized.LastIndexOf('/');

        return slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
    }

    private static int GetFileSortGroup(string relativePath)
    {
        if (IsGitIgnorePath(relativePath))
        {
            return 0;
        }

        string fileName = GetFileNameFromNormalizedPath(relativePath);

        if (!relativePath.Contains('/', StringComparison.Ordinal) && IsProjectNavigationFile(fileName))
        {
            return 1;
        }

        if (IsProjectNavigationFile(fileName))
        {
            return 2;
        }

        return 3;
    }

    private static bool IsProjectNavigationFile(string fileName)
    {
        string lower = fileName.ToLowerInvariant();
        string extension = Path.GetExtension(lower);

        if (lower is
            "readme.md" or
            "package.json" or
            "package-lock.json" or
            "pnpm-lock.yaml" or
            "yarn.lock" or
            "composer.json" or
            "requirements.txt" or
            "pyproject.toml" or
            "cargo.toml" or
            "go.mod" or
            "pom.xml" or
            "build.gradle" or
            "settings.gradle" or
            "dockerfile" or
            "docker-compose.yml" or
            "docker-compose.yaml")
        {
            return true;
        }

        return extension is
            ".sln" or
            ".slnx" or
            ".csproj" or
            ".fsproj" or
            ".vbproj" or
            ".props" or
            ".targets" or
            ".proj";
    }

    private static bool IsReparsePoint(FileAttributes attributes)
    {
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static string EnsureTrailingSlash(string path)
    {
        string normalized = NormalizeGitPath(path);

        if (normalized == ".")
        {
            return normalized;
        }

        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        using var reader = new StringReader(text);
        int count = 0;

        while (reader.ReadLine() is not null)
        {
            count++;
        }

        return count;
    }

    private static IEnumerable<string> ReadOutputLines(string text)
    {
        using var reader = new StringReader(text);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static string FormatExitCode(int? exitCode)
    {
        return exitCode.HasValue ? exitCode.Value.ToString() : "N/A";
    }

    private static string FormatByteSize(long bytes)
    {
        double kilobytes = bytes / 1024d;
        double megabytes = kilobytes / 1024d;
        return $"{megabytes:0.00} MB ({kilobytes:0.0} KB, {bytes:N0} bytes)";
    }

    private static async Task WriteSectionStart(StreamWriter writer, string sectionName)
    {
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"========== SECTION START: {sectionName} ==========");
    }

    private static async Task WriteSectionEnd(StreamWriter writer, string sectionName)
    {
        await writer.WriteLineAsync($"========== SECTION END: {sectionName} ==========");
        await writer.WriteLineAsync();
    }

    private sealed record FileEntry(string FullPath, string RelativePath);

    private sealed record SkippedItem(string RelativePath, string Reason, string Detail);

    private sealed record ScanResult(IReadOnlyList<FileEntry> IncludedFiles, IReadOnlyList<SkippedItem> SkippedItems);

    private sealed record GitIgnoredResult(HashSet<string> IgnoredPaths, int? ExitCode, string ErrorOutput);

    private sealed record FileMetadata(string SizeBytesText, string LastWriteUtcText);

    private sealed record DumpWriteStats(int FilesWritten, int ReadErrors);

    private sealed record TreeEntry(
        string FullPath,
        string RelativePath,
        string Name,
        bool IsDirectory,
        bool IsIgnored,
        bool IsReparsePoint);

    private sealed record TreeBuildResult(
        string Output,
        int WrittenEntries,
        int OmittedEntries,
        int CompactedIgnoredSubtrees,
        int MaxDepthOmissions,
        int ErrorCount,
        int MaxEntriesPerDirectory,
        int MaxDepth);

    private sealed record CompactPathListResult(
        string Output,
        int WrittenEntries,
        int CompactedSubtrees,
        int OmittedPathCount,
        int TotalInputPaths);

    private sealed record ProcessCapture(bool Started, int? ExitCode, string Output, string ErrorOutput)
    {
        public static ProcessCapture NotStarted(string errorMessage)
        {
            return new ProcessCapture(false, null, string.Empty, errorMessage);
        }
    }

    private sealed record DirectChildCount(int? Directories, int? Files)
    {
        public int KnownTotal => (Directories ?? 0) + (Files ?? 0);

        public string DirectoriesText => Directories.HasValue ? Directories.Value.ToString() : "unknown";

        public string FilesText => Files.HasValue ? Files.Value.ToString() : "unknown";

        public static DirectChildCount Known(int directories, int files)
        {
            return new DirectChildCount(directories, files);
        }

        public static DirectChildCount Unknown()
        {
            return new DirectChildCount(null, null);
        }
    }

    private sealed record BinaryProbeResult(bool Success, bool IsBinary, string? ErrorMessage)
    {
        public static BinaryProbeResult Text()
        {
            return new BinaryProbeResult(true, false, null);
        }

        public static BinaryProbeResult Binary()
        {
            return new BinaryProbeResult(true, true, null);
        }

        public static BinaryProbeResult Failed(string errorMessage)
        {
            return new BinaryProbeResult(false, false, errorMessage);
        }
    }

    private sealed record FileContentReadResult(
        bool Success,
        string Content,
        string EncodingName,
        string LineCountText,
        string Sha256Text,
        string ErrorMessage)
    {
        public static FileContentReadResult Ok(string content, string encodingName, int lineCount, string sha256)
        {
            return new FileContentReadResult(true, content, encodingName, lineCount.ToString(), sha256, string.Empty);
        }

        public static FileContentReadResult Error(string errorMessage)
        {
            return new FileContentReadResult(false, string.Empty, "N/A", "N/A", "N/A", errorMessage);
        }
    }

    private sealed class TreeBuildStats
    {
        public int WrittenEntries { get; set; }

        public int OmittedEntries { get; set; }

        public int CompactedIgnoredSubtrees { get; set; }

        public int MaxDepthOmissions { get; set; }

        public int ErrorCount { get; set; }
    }

    private sealed class CompactPathListStats
    {
        public int WrittenEntries { get; set; }

        public int CompactedSubtrees { get; set; }

        public int OmittedPathCount { get; set; }
    }

    private sealed class PathTrieNode
    {
        public Dictionary<string, PathTrieNode> Children { get; } = new(PathComparer);

        public bool IsTerminal { get; set; }

        public int LeafCount { get; set; }

        public PathTrieNode GetOrAdd(string segment)
        {
            if (!Children.TryGetValue(segment, out PathTrieNode? child))
            {
                child = new PathTrieNode();
                Children.Add(segment, child);
            }

            return child;
        }
    }
}
