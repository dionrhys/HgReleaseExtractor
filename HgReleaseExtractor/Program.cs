using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HgReleaseExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 1)
            {
                DisplayUsage();

                // If no args given, displaying usage is intentional and successful.
                // If there were more args given, it was a syntax error.
                Environment.Exit(args.Length == 0 ? 0 : 1);
            }

            Uri sourceUri = null;
            try
            {
                sourceUri = new Uri(args[0]);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Invalid source location");
                Console.WriteLine(ex);
                Environment.Exit(1);
            }

            // Uri.UnescapeDataString won't decode "+" into " ", but that's fine because "+" is only valid as a space character in query strings not paths
            string repoName = Uri.UnescapeDataString(sourceUri.Segments.Last().TrimEnd('/'));
            if (repoName.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            {
                Console.WriteLine("Invalid characters in repository name");
                Environment.Exit(1);
            }

            if (Directory.Exists(repoName))
            {
                Console.WriteLine($"Release directory '{repoName}' already exists");
                Environment.Exit(1);
            }
            try
            {
                Directory.CreateDirectory(repoName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unable to create release directory");
                Console.WriteLine(ex);
                Environment.Exit(1);
            }

            string repoDirectoryName = $@"{repoName}\_repo_";

            int exitCode;
            string output, error;

            // 1. Clone the repository
            // hg clone ...
            Console.WriteLine("Cloning repository...");
            exitCode = RunExecutableProcess("hg.exe", string.Format("clone --noupdate {0} {1}", EncodeCommandLineArg(sourceUri.AbsoluteUri), EncodeCommandLineArg(repoDirectoryName)), "", out output, out error);
            ReportProcessResult(exitCode, output, error);
            if (exitCode != 0)
                Environment.Exit(exitCode);

            // 2. Grab the latest tag
            // hg log --rev tip --template "{latesttag}"
            Console.WriteLine("Getting latest tag...");
            exitCode = RunExecutableProcess("hg.exe", "log --rev tip --template \"{latesttag}\"", repoDirectoryName, out output, out error);
            ReportProcessResult(exitCode, output, error);
            if (exitCode != 0)
                Environment.Exit(exitCode);

            string latestTag;
            using (var reader = new StringReader(output))
            {
                latestTag = reader.ReadLine();
            }
            Console.WriteLine("  " + latestTag);

            // 3. Grab the list of changed files since latest tag and tip
            // hg status --added --modified --no-status --rev "'latest tag'::tip"
            Console.WriteLine("Getting added/modified files since latest tag and tip...");
            exitCode = RunExecutableProcess("hg.exe", "status --added --modified --no-status --rev " + EncodeCommandLineArg($"\"{latestTag}\"::tip"), repoDirectoryName, out output, out error);
            ReportProcessResult(exitCode, output, error);
            if (exitCode != 0)
                Environment.Exit(exitCode);

            List<string> changedFiles = new List<string>();
            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    changedFiles.Add(line);
                    Console.WriteLine("  " + line);
                }
            }

            // 4. Generate a Mercurial pattern listfile with all the changed files
            Console.WriteLine("Generating listfile...");
            var filePatternsToKeep = changedFiles
                .Where(path => !path.StartsWith(".hg", StringComparison.OrdinalIgnoreCase)) // Exclude .hg* files from the generated release files
                .Select(path => "path:" + path); // Prefix each path with "path:" so Mercurial treats them as exact paths instead of glob patterns
            string patternFileName = "releasefiles.txt";
            string patternFilePath = $@"{repoDirectoryName}\{patternFileName}";
            try
            {
                File.WriteAllLines(
                    patternFilePath,
                    filePatternsToKeep,
                    // Mercurial on Windows doesn't support Unicode filenames well at all, so use ASCII and throw if any non-ASCII characters are detected
                    Encoding.GetEncoding("us-ascii", new EncoderExceptionFallback(), new DecoderExceptionFallback())
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unable to write listfile!");
                Console.WriteLine(ex);
                Environment.Exit(1);
            }

            // 5. Generate a directory-based archive with only the changed files
            // hg archive --rev tip --include "listfile:'list file path' ."
            Console.WriteLine("Extracting changed files to release directory...");
            exitCode = RunExecutableProcess("hg.exe", string.Format(
                "archive --rev tip --type files --config ui.archivemeta=false --include {0} {1}",
                EncodeCommandLineArg($"listfile:{patternFileName}"),
                EncodeCommandLineArg(@"..")
            ), repoDirectoryName, out output, out error);
            ReportProcessResult(exitCode, output, error);
            if (exitCode != 0)
                Environment.Exit(exitCode);

            // 6. Delete repository directory
            Console.WriteLine("Deleting repository directory...");
            try
            {
                Directory.Delete(repoDirectoryName, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unable to delete repository directory!");
                Console.WriteLine(ex);
                Environment.Exit(1);
            }
            
            Console.WriteLine("Complete!");
        }

        private static void ReportProcessResult(int exitCode, string output, string error)
        {
            if (exitCode == 0)
                return;

            Console.WriteLine("Process exited with code {0}!", exitCode);
            Console.WriteLine();

            Console.WriteLine("Standard Error:");
            Console.WriteLine(error);

            Console.WriteLine("Standard Output:");
            Console.WriteLine(output);
        }

        private static void DisplayUsage()
        {
            Console.Write(
@"Brief description of what this program does.

Usage: HgReleaseExtractor <source>

  source    Location of the source Mercurial repository.

Example: HgReleaseExtractor https://hg.libsdl.org/SDL
         Help description saying what the example does.
");
        }

        private static string EncodeCommandLineArg(string arg)
        {
            // http://stackoverflow.com/a/6040946/1125059
            arg = Regex.Replace(arg, @"(\\*)""", @"$1$1\""");
            arg = "\"" + Regex.Replace(arg, @"(\\+)$", @"$1$1") + "\"";
            return arg;
        }

        private static int RunExecutableProcess(string fileName, string arguments, string workingDirectory, out string stdout, out string stderr)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("A file name must be specified", nameof(fileName));
            if (arguments == null)
                throw new ArgumentNullException(nameof(arguments));
            if (workingDirectory == null)
                throw new ArgumentNullException(nameof(workingDirectory));

            var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory
            };

            var outSb = new StringBuilder();
            p.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null) // An event with a null Data property is sent when the stream has been closed
                {
                    outSb.AppendLine(e.Data);
                }
            };

            var errSb = new StringBuilder();
            p.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) // An event with a null Data property is sent when the stream has been closed
                {
                    errSb.AppendLine(e.Data);
                }
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();

            stdout = outSb.ToString();
            stderr = errSb.ToString();
            return p.ExitCode;
        }
    }
}
