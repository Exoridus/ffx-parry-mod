using Microsoft.Win32;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using static Nuke.Common.Assert;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed partial class BuildScript
{
    BuildLogVerbosity? _resolvedLogVerbosity;

    void RunChecked(string fileName, string args, string description, string? workingDirectory = null, bool showSpinner = false, bool silent = false)
    {
        var result = RunProcess(fileName, args, description, workingDirectory, showSpinner, silent);
        if (result.ExitCode != 0)
        {
            Fail($"{description} failed with code {result.ExitCode}.{Environment.NewLine}Command: {fileName} {args}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StdOut}");
        }
    }

    ProcessResult RunProcess(
        string fileName,
        string args,
        string description,
        string? workingDirectory = null,
        bool showSpinner = false,
        bool silent = false,
        bool showStdOutOnSuccess = false,
        bool showStdErrOnSuccess = false)
    {
        var effectiveWorkingDirectory = workingDirectory ?? RootDirectory;
        if (!silent && IsLogVerbosityAtLeast(BuildLogVerbosity.Diagnostic))
        {
            Log.Information($"[TRACE] EXEC {fileName} {args}");
            Log.Information($"[TRACE] CWD  {effectiveWorkingDirectory}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = effectiveWorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start()) return new ProcessResult(-1, string.Empty, "Failed to start process.");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.ToString());
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (showSpinner && !Console.IsOutputRedirected)
        {
            var frames = new[] { '|', '/', '-', '\\' };
            var i = 0;
            while (!process.WaitForExit(120))
            {
                Console.Write($"\r{description} {frames[i++ % frames.Length]}");
            }

            Console.Write("\r");
            Console.Write(new string(' ', Math.Min(Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80, 120)));
            Console.Write("\r");
        }
        else
        {
            process.WaitForExit();
        }

        process.WaitForExit();

        var result = new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        EmitProcessOutput(
            description,
            result,
            silent,
            showStdOutOnSuccess,
            showStdErrOnSuccess);
        return result;
    }

    void EmitProcessOutput(
        string description,
        ProcessResult result,
        bool silent,
        bool showStdOutOnSuccess,
        bool showStdErrOnSuccess)
    {
        if (silent)
        {
            return;
        }

        var level = ResolveLogVerbosity();
        var failed = result.ExitCode != 0;
        var stdoutLines = SplitProcessOutputLines(result.StdOut);
        var stderrLines = SplitProcessOutputLines(result.StdErr);

        var emitStdOut = failed
                         || level is BuildLogVerbosity.Detailed or BuildLogVerbosity.Diagnostic
                         || (level == BuildLogVerbosity.Normal && showStdOutOnSuccess);
        var emitStdErr = failed
                         || level is BuildLogVerbosity.Detailed or BuildLogVerbosity.Diagnostic
                         || (level == BuildLogVerbosity.Normal && showStdErrOnSuccess);

        if (!failed && (level is BuildLogVerbosity.Quiet or BuildLogVerbosity.Minimal))
        {
            emitStdOut = false;
            emitStdErr = false;
        }

        if (emitStdOut)
        {
            foreach (var line in stdoutLines)
            {
                if (level == BuildLogVerbosity.Diagnostic)
                {
                    Log.Information($"[TRACE][{description}][stdout] {line}");
                }
                else
                {
                    Log.Information(line);
                }
            }
        }

        if (emitStdErr)
        {
            foreach (var line in stderrLines)
            {
                if (level == BuildLogVerbosity.Diagnostic)
                {
                    Log.Warning($"[TRACE][{description}][stderr] {line}");
                }
                else
                {
                    Log.Warning(line);
                }
            }
        }
    }

    static IReadOnlyList<string> SplitProcessOutputLines(string value)
    {
        return value
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    static void EnsureDir(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) Directory.CreateDirectory(path);
    }

    static void RecreateDir(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    AbsolutePath ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(RootDirectory, path));
    }

    static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    void ValidateVerbosityFlags() => _ = ResolveLogVerbosity();

    BuildLogVerbosity ResolveLogVerbosity()
    {
        if (_resolvedLogVerbosity is { } cached)
        {
            return cached;
        }

        var fromParameter = (LogVerbosity ?? string.Empty).Trim();
        _resolvedLogVerbosity = string.IsNullOrWhiteSpace(fromParameter)
            ? BuildLogVerbosity.Normal
            : ParseLogVerbosity(fromParameter);
        return _resolvedLogVerbosity.Value;
    }

    static BuildLogVerbosity ParseLogVerbosity(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "quiet" or "q")
            return BuildLogVerbosity.Quiet;
        if (normalized is "minimal" or "min" or "m")
            return BuildLogVerbosity.Minimal;
        if (normalized is "normal" or "n")
            return BuildLogVerbosity.Normal;
        if (normalized is "detailed" or "detail" or "d")
            return BuildLogVerbosity.Detailed;
        if (normalized is "diagnostic" or "diag")
            return BuildLogVerbosity.Diagnostic;

        Fail($"Invalid verbosity '{value}'. Use: quiet, minimal, normal, detailed, diagnostic.");
        return BuildLogVerbosity.Normal;
    }

    bool IsLogVerbosityAtLeast(BuildLogVerbosity level) => ResolveLogVerbosity() >= level;

    static void WriteHelpLine(string value) => Console.WriteLine(value);

    string ResolveMsBuildVerbosity()
    {
        return ResolveLogVerbosity() switch
        {
            BuildLogVerbosity.Quiet => "quiet",
            BuildLogVerbosity.Minimal => "minimal",
            BuildLogVerbosity.Normal => "minimal",
            BuildLogVerbosity.Detailed => "normal",
            BuildLogVerbosity.Diagnostic => "diagnostic",
            _ => "minimal"
        };
    }

    string ResolveDotNetCliVerbosity()
    {
        return ResolveLogVerbosity() switch
        {
            BuildLogVerbosity.Quiet => "quiet",
            BuildLogVerbosity.Minimal => "minimal",
            BuildLogVerbosity.Normal => "minimal",
            BuildLogVerbosity.Detailed => "normal",
            BuildLogVerbosity.Diagnostic => "detailed",
            _ => "minimal"
        };
    }

    enum BuildLogVerbosity
    {
        Quiet = 0,
        Minimal = 1,
        Normal = 2,
        Detailed = 3,
        Diagnostic = 4
    }

    readonly record struct WorkflowHelpBlock(
        string Workflow,
        string Purpose,
        IReadOnlyList<string> Parameters,
        IReadOnlyList<string> Examples);

    readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
