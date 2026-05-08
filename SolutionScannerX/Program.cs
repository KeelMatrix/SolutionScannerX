using System.Diagnostics;
using System.Text;

namespace SolutionScannerX
{
    class Program
    {
        private const string ConfigFileName = "solution_path_config.txt";

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string solutionPath = GetSolutionPath();

            if (string.IsNullOrEmpty(solutionPath) || !Directory.Exists(solutionPath))
            {
                Console.WriteLine("Invalid solution path provided or path does not exist. Exiting.");
                return;
            }

            // Get the set of ignored files and directories from Git
            HashSet<string> ignoredItems = GetIgnoredFilesFromGit(solutionPath);

            string solutionName = new DirectoryInfo(solutionPath).Name;
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string outputFileName = Path.Combine(desktopPath, $"{solutionName}-{timestamp}.txt");

            //Console.WriteLine($"Output will be saved to: {outputFileName}");

            try
            {
                using (StreamWriter writer = new StreamWriter(outputFileName, append: false, Encoding.UTF8))
                {
                    await ProcessDirectory(solutionPath, solutionPath, ignoredItems, writer);
                }

                Console.WriteLine($"Successfully created and wrote to {outputFileName}");

                // open the file:
                //try
                //{
                //    ProcessStartInfo psi = new ProcessStartInfo(outputFileName)
                //    {
                //        UseShellExecute = true
                //    };
                //    Process.Start(psi);
                //}
                //catch (Exception ex)
                //{
                //    Console.WriteLine($"Could not automatically open the file. Error: {ex.Message}");
                //    Console.WriteLine($"Please find it at: {outputFileName}");
                //}
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during processing: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("Program finished. Press any key to exit.");
            Console.ReadKey();
        }

        static string GetSolutionPath()
        {
            string configFilePath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            string? existingPath = null;

            if (File.Exists(configFilePath))
            {
                try
                {
                    existingPath = File.ReadAllText(configFilePath).Trim();
                    if (!Directory.Exists(existingPath))
                    {
                        Console.WriteLine($"Stored path '{existingPath}' not found or is invalid.");
                        existingPath = null;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading config file: {ex.Message}");
                    existingPath = null;
                }
            }

            //if (!string.IsNullOrEmpty(existingPath))
            //{
            //    Console.WriteLine($"Found existing path: {existingPath}");
            //    Console.Write("Do you want to use this path (1) or provide a new one (2)? Enter 1 or 2: ");
            //    string? choice = Console.ReadLine();
            //    if (choice == "1")
            //    {
            //        return existingPath;
            //    }
            //}

            if (!string.IsNullOrEmpty(existingPath))
            {
                Console.WriteLine($"Saved path: {existingPath}");
                Console.Write("Press Enter to use it, or type a new path: ");
                string? typed = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(typed))
                {
                    return existingPath;
                }
                else
                {
                    string newTyped = typed.Trim();
                    while (!Directory.Exists(newTyped))
                    {
                        Console.Write("Invalid path. Enter a valid full path: ");
                        newTyped = (Console.ReadLine() ?? "").Trim();
                    }
                    try
                    {
                        File.WriteAllText(configFilePath, newTyped);
                        Console.WriteLine($"New path saved to {configFilePath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error saving path to config file: {ex.Message}");
                    }
                    return newTyped;
                }
            }

            string? newPath = null;
            while (true)
            {
                Console.WriteLine("Enter the full path to the solution folder:");
                newPath = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(newPath) && Directory.Exists(newPath))
                {
                    break;
                }
                Console.WriteLine("Invalid path or path does not exist. Please try again.");
            }

            try
            {
                File.WriteAllText(configFilePath, newPath);
                Console.WriteLine($"New path saved to {configFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving path to config file: {ex.Message}");
            }
            return newPath;
        }

        static HashSet<string> GetIgnoredFilesFromGit(string solutionPath)
        {
            // Use OrdinalIgnoreCase for HashSet to be case-insensitive like most file systems (especially Windows)
            // Git itself can be configured for case sensitivity, but git status output on Windows usually matches case.
            // However, paths in .gitignore can be case sensitive on case-sensitive file systems.
            // For robustness with git output, it's often safer to normalize case if comparing.
            // Here, we'll store as git outputs and normalize the checked path.
            var ignoredItems = new HashSet<string>();
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c \"(git ls-files --cached & git ls-files -o -i --exclude-standard) ^| git check-ignore --no-index --stdin\"",
                WorkingDirectory = solutionPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Console.WriteLine($"Running 'git {processStartInfo.Arguments}' in '{solutionPath}'...");

            try
            {
                using (var process = Process.Start(processStartInfo))
                {
                    if (process == null)
                    {
                        Console.WriteLine("Error: Could not start git process.");
                        return ignoredItems;
                    }

                    string? line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        string ignoredPath = line.Trim();
                        // Git output uses forward slashes. Normalize and store.
                        string normalizedIgnoredPath = ignoredPath.Replace('\\', '/');
                        ignoredItems.Add(normalizedIgnoredPath);
                    }

                    string errorOutput = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Console.WriteLine($"Git command exited with error code {process.ExitCode}.");
                        if (!string.IsNullOrWhiteSpace(errorOutput))
                        {
                            Console.WriteLine($"Git Error Output: {errorOutput}");
                        }
                        Console.WriteLine("Warning: Could not reliably determine ignored files from git. No files will be skipped based on git status.");
                    }
                    else
                    {
                        Console.WriteLine($"Found {ignoredItems.Count} ignored items via git.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing git command: {ex.Message}");
                Console.WriteLine("Ensure git is installed and in your PATH. No files will be skipped based on git status.");
            }

            return ignoredItems;
        }


        static async Task ProcessDirectory(string targetDirectory, string solutionRootPath, HashSet<string> ignoredItems, StreamWriter writer)
        {
            // Explicitly skip .git directory
            if (new DirectoryInfo(targetDirectory).Name == ".git")
            {
                //Console.WriteLine($"Skipping Git metadata directory: {Path.GetRelativePath(solutionRootPath, targetDirectory)}");
                return;
            }

            // Process files in the current directory
            foreach (string filePath in Directory.GetFiles(targetDirectory))
            {
                await ProcessFile(new FileInfo(filePath), solutionRootPath, ignoredItems, writer);
            }

            // Recursively process subdirectories
            foreach (string directoryPath in Directory.GetDirectories(targetDirectory))
            {
                string relativeDirPath = Path.GetRelativePath(solutionRootPath, directoryPath)
                                             .Replace(Path.DirectorySeparatorChar, '/');

                // Check if the directory itself or its version with a trailing slash is ignored
                if (ignoredItems.Contains(relativeDirPath) || ignoredItems.Contains(relativeDirPath + "/"))
                {
                    //Console.WriteLine($"Skipping ignored directory: {relativeDirPath}");
                    continue;
                }
                await ProcessDirectory(directoryPath, solutionRootPath, ignoredItems, writer);
            }
        }

        static async Task ProcessFile(FileInfo fileInfo, string solutionRootPath, HashSet<string> ignoredItems, StreamWriter writer)
        {
            string relativeFilePath = Path.GetRelativePath(solutionRootPath, fileInfo.FullName)
                                          .Replace(Path.DirectorySeparatorChar, '/');

            if (ignoredItems.Contains(relativeFilePath) ||
                fileInfo.FullName.ToLower().EndsWith(".png") ||
                fileInfo.FullName.ToLower().EndsWith(".jpg") ||
                fileInfo.FullName.ToLower().EndsWith(".jpeg") ||
                fileInfo.FullName.ToLower().EndsWith(".wasm") ||
                fileInfo.FullName.ToLower().EndsWith(".ico") ||
                fileInfo.FullName.ToLower().EndsWith("blueprint.txt") ||
                fileInfo.FullName.ToLower().EndsWith(".gitignore"))
            {
                //Console.WriteLine($"Skipping ignored file: {relativeFilePath}");
                return;
            }

            // Check if the containing directory of the file is ignored.
            // This is a bit more involved as git might ignore a whole directory like "bin/"
            // which means "bin/somefile.txt" should be ignored.
            // The GetIgnoredFilesFromGit should ideally return directories with a trailing slash if the whole dir is ignored.
            string? parentDirRelative = Path.GetDirectoryName(relativeFilePath)?.Replace(Path.DirectorySeparatorChar, '/');
            if (!string.IsNullOrEmpty(parentDirRelative) && parentDirRelative != ".") // Check for non-root files
            {
                if (ignoredItems.Contains(parentDirRelative + "/") || ignoredItems.Contains(parentDirRelative))
                {
                    // Check if there's an explicit unignore for this specific file
                    bool isUnignored = ignoredItems.Any(rule => rule.StartsWith("!") &&
                                                               rule.Substring(1).Equals(relativeFilePath, StringComparison.OrdinalIgnoreCase));
                    if (!isUnignored)
                    {
                        //Console.WriteLine($"Skipping file in ignored directory: {relativeFilePath}");
                        return;
                    }
                    else
                    {
                        Console.WriteLine($"File explicitly unignored, processing: {relativeFilePath}");
                    }
                }
            }


            Console.WriteLine($"Processing file: {relativeFilePath}");

            string header = "#########################################";

            await writer.WriteLineAsync(header);
            //await writer.WriteLineAsync();
            await writer.WriteLineAsync($"PATH: \"{Path.DirectorySeparatorChar}{relativeFilePath.Replace('/', Path.DirectorySeparatorChar)}\"");
            await writer.WriteLineAsync();
            //try
            //{
            //    await writer.WriteLineAsync($"DATE CREATED: {fileInfo.CreationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
            //    await writer.WriteLineAsync($"LAST MODIFIED: {fileInfo.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
            //}
            //catch (Exception ex)
            //{
            //    await writer.WriteLineAsync($"DATE CREATED: ERROR reading date ({ex.GetType().Name})");
            //    await writer.WriteLineAsync($"LAST MODIFIED: ERROR reading date ({ex.GetType().Name})");
            //}
            //await writer.WriteLineAsync();
            //await writer.WriteLineAsync("CONTENTS:");

            string fileContent;
            try
            {
                using (StreamReader reader = new StreamReader(fileInfo.OpenRead(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    fileContent = await reader.ReadToEndAsync();
                }
            }
            catch (Exception ex)
            {
                fileContent = $"ERROR: CANNOT READ THE FILE: {fileInfo.Name}. Reason: {ex.Message}";
                Console.WriteLine($"   -> Error reading content for {relativeFilePath}: {ex.Message}");
            }

            await writer.WriteLineAsync(fileContent);
            await writer.WriteLineAsync();
            await writer.WriteLineAsync();
        }
    }
}
