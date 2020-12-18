using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.CommandLine.Parsing;
using System.Threading;

namespace Tmds.DotnetRunSource
{
    class Program
    {
        const string DefaultPollInterval = "5m";
        const string Origin = "origin";

        static int Main(string[] args)
        {
            var rootCommand = new RootCommand
            {
                new Option<string>(
                    "--project",
                    description: "Path in the repository to the .NET project to run."),
                new Option<string>(
                    "--branch",
                    description: "Source repository branch to use."),
                new Option<TimeSpan>(
                    "--poll-interval",
                    parseArgument: result => ParseTimeSpan(result, DefaultPollInterval),
                    isDefault: true,
                    description: $"Interval at which to poll the source repository for changes (default: {DefaultPollInterval})."),
            };
            rootCommand.AddArgument(new Argument<string>("repo", "Url to source repository."));
            rootCommand.Description = "Run an application from a source repository.";

            rootCommand.Handler = CommandHandler.Create(new Func<string, string, string, TimeSpan, ParseResult, /*CancellationToken, */Task<int>>(CreateSource));

            return rootCommand.InvokeAsync(args).Result;
        }

        private static TimeSpan ParseTimeSpan(System.CommandLine.Parsing.ArgumentResult result, string defaultInterval)
        {
            string tokenValue = result.Tokens.FirstOrDefault()?.Value ?? defaultInterval;
            return TimeSpan.FromSeconds(5); // TODO: parse tokenValue
        }

        static async Task<int> CreateSource(string repo, string branch, string project, TimeSpan pollInterval,
                                            ParseResult parseResult /* TODO: support passing arguments to application */
                                            /*  CancellationToken cancellationTokenTODO: support cancellation */)
        {
            string repoUrl = repo;
            string repoBranch = branch ?? "HEAD";
            string repoProject = project;

            string workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            System.Diagnostics.Process process = null;
            try
            {
                string repoDir = Path.Combine(workingDirectory, "repo");
                string projDir = Path.Combine(repoDir, repoProject);
                string publishDir = Path.Combine(workingDirectory, "published");

                Directory.CreateDirectory(repoDir);
                Git.Init(repoDir);

                Git.RemoteAdd(repoDir, Origin, repoUrl);

                Log.Info($"Checking repository for latest commit");
                string commit = Git.LsRemote(repoUrl, repoBranch);

                while (true)
                {
                    await Git.FetchAsync(repoDir, Origin, commit);

                    Log.Info($"Fetching {commit}");
                    Git.Checkout(repoDir, commit);

                    Log.Info($"Publishing application");
                    Dotnet.Publish(projDir, publishDir);

                    string runtimeConfigFile = Directory.GetFiles(publishDir, "*.runtimeconfig.json").FirstOrDefault();
                    string entryPointAssemblyFile = runtimeConfigFile.Substring(0, runtimeConfigFile.Length - 19) + ".dll";

                    Log.Info($"Run application");
                    process = ProcessUtils.Start("dotnet", new[] { entryPointAssemblyFile });

                    while (true)
                    {
                        await Task.Delay(pollInterval);
                        string latestCommit = Git.LsRemote(repoUrl, repoBranch);
                        if (latestCommit != commit)
                        {
                            Log.Info($"The repository has changed");
                            commit = latestCommit;
                            break;
                        }
                    }

                    Log.Info($"Terminating application");
                    ProcessUtils.Terminate(process);

                    Directory.Delete(publishDir, recursive: true);
                }
            }
            catch (ProcessFailedException pfe)
            {
                Log.Error(pfe.ToString());
            }
            finally
            {
                if (process != null)
                {
                    ProcessUtils.Terminate(process);
                }
                Directory.Delete(workingDirectory, recursive: true);
            }
            return -1;
        }
    }

    static class Log
    {
        public static void Info(string message)
        {
            Console.WriteLine(message);
        }

        public static void Error(string message)
        {
            Console.Error.WriteLine("ERROR: " + message);
        }
    }

    static class Libc
    {
        public const int SIGTERM = 15;

        [DllImport("libc")]
        public static extern int kill(int pid, int signal);
    }

    static class Dotnet
    {
        public static void Publish(string project, string outputDirectory, string configuration = "Release")
        {
            ProcessUtils.Run("dotnet", new[] { "publish", project, "-c", configuration, "-o", outputDirectory });
        }
    }

    static class Git
    {
        public static void Init(string directory)
        {
            ProcessUtils.Run("git", new[] { "init", directory });
        }

        public static void RemoteAdd(string directory, string name, string url)
        {
            ProcessUtils.Run("git", new[] { "remote", "add", name, url }, directory);
        }

        public static Task FetchAsync(string directory, string repository, string refspec)
        {
            return ProcessUtils.RunAsync("git", new[] { "fetch", repository, refspec }, directory);
        }

        public static void Checkout(string directory, string commit)
        {
            ProcessUtils.Run("git", new[] { "checkout", commit }, directory);
        }

        public static string LsRemote(string repoUrl, string refspec)
        {
            StringBuilder processOutput = new StringBuilder();
            var process = ProcessUtils.Start("git", new[] { "ls-remote", repoUrl, refspec }, processOutput);
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                string line = processOutput.ToString();
                return line.Substring(0, line.IndexOf('\t'));
            }
            return null;
        }

        public static void Clean(string directory)
        {
            ProcessUtils.Run("git", new[] { "clean", "-fdx" }, directory);
        }
    }

    class ProcessFailedException : System.Exception
    {
        public ProcessFailedException(string commandline, int exitcode, string output) : base(ComposeMessage(commandline, exitcode, output))
        {
            CommandLine = commandline;
            ExitCode = exitcode;
            Output = output;
        }

        private static string ComposeMessage(string commandline, int exitcode, string output)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Process '{commandline}' failed with exitcode {exitcode}");
            sb.AppendLine($"Process output:");
            sb.AppendLine(output);
            return sb.ToString();
        }

        public string CommandLine { get; set; }
        public int ExitCode { get; }
        public string Output { get; }
    }

    static class ProcessUtils
    {
        public static void Terminate(System.Diagnostics.Process process)
        {
            do
            {
                Libc.kill(process.Id, Libc.SIGTERM);
            } while (!process.WaitForExit(500));
        }

        public static System.Diagnostics.Process Start(string filename, string[] arguments, StringBuilder processOutput = null, string workingDirectory = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = filename,
            };
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }
            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = filename;
            foreach (var arg in arguments)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.RedirectStandardInput = true;
            if (processOutput != null)
            {
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.ErrorDataReceived += (object sender, DataReceivedEventArgs e) =>
                {
                    if (e.Data != null)
                    {
                        processOutput.AppendLine("error: " + e.Data);
                    }
                };
                process.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
                {
                    if (e.Data != null)
                    {
                        processOutput.AppendLine(e.Data);
                    }
                };
            }
            process.Start();
            process.StandardInput.Close();
            if (processOutput != null)
            {
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
            }
            return process;
        }

        public static void Run(string filename, string[] arguments, string workingDirectory = null)
        {
            StringBuilder sb = new StringBuilder();
            var process = Start(filename, arguments, sb, workingDirectory);
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new ProcessFailedException(commandline: $"{filename} {string.Join(' ', arguments)}", exitcode: process.ExitCode, output: sb.ToString());
            }
        }

        public static Task RunAsync(string filename, string[] arguments, string workingDirectory = null)
        {
            Run(filename, arguments, workingDirectory);
            return Task.CompletedTask;
        }
    }
}
