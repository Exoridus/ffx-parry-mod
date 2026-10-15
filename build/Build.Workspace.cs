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
    void DeleteFileMaybeWithAccounting(string path, CleanupAccountingResult result)
    {
        if (!File.Exists(path))
        {
            return;
        }

        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch
        {
            // Best-effort size accounting.
        }

        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Delete file: {path}");
            }

            result.FilesRemoved++;
            result.BytesReclaimed += Math.Max(0, size);
            return;
        }

        File.Delete(path);
        if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
        {
            Log.Information($"Deleted file: {path}");
        }

        result.FilesRemoved++;
        result.BytesReclaimed += Math.Max(0, size);
    }

    void DeleteDirectoryMaybeWithAccounting(string path, CleanupAccountingResult result)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var size = MeasureDirectoryBytes(path);
        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Delete directory: {path}");
            }

            result.DirectoriesRemoved++;
            result.BytesReclaimed += size;
            return;
        }

        Directory.Delete(path, recursive: true);
        if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
        {
            Log.Information($"Deleted directory: {path}");
        }

        result.DirectoriesRemoved++;
        result.BytesReclaimed += size;
    }

    static long MeasureDirectoryBytes(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Select(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                })
                .Sum();
        }
        catch
        {
            return 0;
        }
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var units = new[] { "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = -1;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:F2} {units[index]}";
    }

    sealed class CleanupAccountingResult
    {
        public int FilesRemoved { get; set; }
        public int DirectoriesRemoved { get; set; }
        public long BytesReclaimed { get; set; }
    }

    bool InteractiveSession => !NonInteractive && !IsServerBuild && Environment.UserInteractive;
    AbsolutePath WorkspaceDir => RootDirectory / ".workspace";
    AbsolutePath WorkspaceConfigPath => ResolveWorkspaceConfigPath();
    AbsolutePath ReleaseFahrenheitRefPath => RootDirectory / "fahrenheit.release.ref";
    static readonly Regex InstallPathNamePattern = new(
        @"^Final Fantasy X[-_ ]X-?2(?:\s*-\s*)?HD Remaster$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SteamLibraryPathRegex = new(
        "\"path\"\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SteamLibraryLegacyPathRegex = new(
        "^\\s*\"\\d+\"\\s*\"(?<path>[^\"]+)\"\\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex SteamAppManifestInstallDirRegex = new(
        "\"installdir\"\\s*\"(?<dir>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    const string SteamAppIdFfx = "359870";
    // A full deploy mirrors the whole Fahrenheit tree and deletes whatever the source lacks.
    // "state" holds Fahrenheit's runtime state and every mod's persisted settings, so leaving it
    // out silently resets user configuration on each deploy. "logs" is the session log directory.
    static readonly string[] DefaultPreservePaths = ["config", "logs", "mods/loadorder", "saves", "state"];

    string RequestedConfiguration =>
        ConfigurationOverride;

    string RequestedConfigPath =>
        ConfigPath;

    void SetupHooksCore()
    {
        RequireGitRepository();
        if (!File.Exists(RootDirectory / ".githooks" / "commit-msg"))
        {
            Fail("Missing .githooks/commit-msg.");
        }

        RunChecked("git", "config --local core.hooksPath .githooks", "Setup hooks");
    }

    AbsolutePath ResolveWorkspaceConfigPath()
    {
        var fromArg = (RequestedConfigPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fromArg))
        {
            return WorkspaceDir / "config.local.json";
        }

        return ResolvePath(fromArg);
    }

    string ResolveBuildConfiguration(string configuration)
    {
        var fromArg = (configuration ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromArg))
        {
            if (fromArg.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            {
                return "Debug";
            }

            if (fromArg.Equals("Release", StringComparison.OrdinalIgnoreCase))
            {
                return "Release";
            }

            Fail($"Invalid --configuration value '{configuration}'. Use Debug or Release.");
        }

        var cfg = LoadWorkspaceConfig();
        var fromConfig = (cfg.Configuration ?? string.Empty).Trim();
        if (fromConfig.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        if (fromConfig.Equals("Release", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        Fail($"Invalid Configuration '{cfg.Configuration}' in '{WorkspaceConfigPath}'. Use Debug or Release.");
        return string.Empty;
    }

    void EnsureWingetAvailable()
    {
        if (!CommandExists("winget"))
        {
            Fail("winget is required for automated prerequisite install. Install App Installer first.");
        }
    }

    void EnsureGitInstalled()
    {
        if (CommandExists("git"))
        {
            Log.Information("[OK] Git is already installed.");
            return;
        }

        EnsureWingetAvailable();
        PromptInstallOrFail(
            title: "Git not found.",
            detail: "Git is required for clone/update, changelog generation, tags, and release workflows.",
            adminRequired: true);

        InstallWingetPackage("Git.Git", "Git", overrideArgs: null);

        if (!CommandExists("git"))
        {
            Fail("Git install completed but command not found on PATH yet. Open a new terminal and retry.");
        }

        Log.Information("Git installation verified.");
    }

    void EnsureDotNetSdk10Installed()
    {
        if (CommandExists("dotnet") && DotNetSdkMajorInstalled(10))
        {
            Log.Information("[OK] .NET SDK 10.x is already installed.");
            return;
        }

        EnsureWingetAvailable();
        PromptInstallOrFail(
            title: ".NET SDK 10.x not found.",
            detail: ".NET SDK 10.x is required to run NUKE and to build this project.",
            adminRequired: true);

        InstallWingetPackage("Microsoft.DotNet.SDK.10", ".NET SDK 10.x", overrideArgs: null);

        if (!CommandExists("dotnet") || !DotNetSdkMajorInstalled(10))
        {
            Fail(".NET SDK 10.x verification failed after installation.");
        }

        Log.Information(".NET SDK 10.x installation verified.");
    }

    void EnsureMsbuildInstalled()
    {
        if (CommandExists("msbuild"))
        {
            Log.Information("[OK] MSBuild is already available.");
            return;
        }

        // MSBuild.exe is only on PATH inside a Developer Command Prompt. A regular
        // Visual Studio install still ships it, and that is what build/Build.proj's
        // ResolveNativeMSBuild target uses, so discover it the same way before
        // concluding anything is missing.
        var msbuildExe = ResolveMsbuildExePath();
        if (!string.IsNullOrWhiteSpace(msbuildExe))
        {
            Log.Information($"[OK] MSBuild is already available: {msbuildExe}");
            return;
        }

        EnsureWingetAvailable();
        PromptInstallOrFail(
            title: "MSBuild not found.",
            detail: "Full builds require Visual Studio Build Tools with C++ and .NET desktop workloads.",
            adminRequired: true);

        InstallWingetPackage(
            packageId: "Microsoft.VisualStudio.2022.BuildTools",
            label: "Visual Studio Build Tools",
            overrideArgs: "--wait --quiet --norestart --nocache --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.Workload.VCTools");

        if (!CommandExists("msbuild") && string.IsNullOrWhiteSpace(ResolveMsbuildExePath()))
        {
            Log.Warning("MSBuild may need a new terminal session to appear on PATH.");
        }
    }

    // Single vswhere probe for every "where is Visual Studio" question in this file.
    // The setup gates used to answer that question by looking on PATH only, which is
    // true just inside a Developer Command Prompt -- so a perfectly good install read
    // as "missing" and setup tried to winget a second one.
    string ResolveVisualStudioInstallPath()
    {
        var vswhere = ResolveVsWherePath();
        if (string.IsNullOrWhiteSpace(vswhere) || !File.Exists(vswhere))
        {
            return string.Empty;
        }

        var probe = RunProcess(
            vswhere,
            "-latest -products * -requires Microsoft.Component.MSBuild -property installationPath",
            "Resolve Visual Studio installation path",
            showSpinner: false,
            silent: true);

        if (probe.ExitCode != 0)
        {
            return string.Empty;
        }

        var installPath = probe.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)
            ? string.Empty
            : installPath;
    }

    string ResolveMsbuildExePath()
    {
        var installPath = ResolveVisualStudioInstallPath();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return string.Empty;
        }

        var msbuildExe = Path.Combine(installPath, "MSBuild", "Current", "Bin", "MSBuild.exe");
        return File.Exists(msbuildExe) ? msbuildExe : string.Empty;
    }

    void EnsureVcpkgInstalledAndIntegrated()
    {
        var vcpkgExe = FindVcpkgExecutable();
        if (string.IsNullOrWhiteSpace(vcpkgExe))
        {
            PromptInstallOrFail(
                title: "vcpkg not found.",
                detail: "A local vcpkg clone will be bootstrapped under .workspace/vcpkg and integrated for this user.",
                adminRequired: false);

            if (!DryRun)
            {
                var vcpkgRoot = WorkspaceDir / "vcpkg";
                EnsureDir(WorkspaceDir);

                if (!Directory.Exists(vcpkgRoot))
                {
                    RunChecked("git", $"clone https://github.com/microsoft/vcpkg {Quote(vcpkgRoot)}", "Clone vcpkg", showSpinner: true, silent: true);
                }

                var bootstrap = vcpkgRoot / "bootstrap-vcpkg.bat";
                if (!File.Exists(bootstrap))
                {
                    Fail($"Missing bootstrap script: {bootstrap}");
                }

                RunChecked("cmd", $"/c \"\"{bootstrap}\" -disableMetrics\"", "Bootstrap vcpkg", workingDirectory: vcpkgRoot, showSpinner: true, silent: true);
            }

            vcpkgExe = FindVcpkgExecutable();
        }

        if (string.IsNullOrWhiteSpace(vcpkgExe))
        {
            Fail("vcpkg could not be located after bootstrap.");
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] {vcpkgExe} integrate install");
            return;
        }

        RunChecked(vcpkgExe, "integrate install", "Integrate vcpkg", showSpinner: true, silent: true);
        Log.Information("vcpkg integration complete.");
    }

    void PromptInstallOrFail(string title, string detail, bool adminRequired)
    {
        Log.Warning(title);
        Log.Information(detail);
        if (adminRequired)
        {
            Log.Warning("This install may require administrator privileges and can trigger a UAC prompt.");
        }

        if (!InteractiveSession)
        {
            Fail("Missing prerequisite in non-interactive mode. Install prerequisites first or run interactively.");
        }

        if (!AskYesNo("Install now?", defaultYes: false))
        {
            Fail("Installation declined. Aborting.");
        }
    }

    void InstallWingetPackage(string packageId, string label, string? overrideArgs)
    {
        var args = new StringBuilder();
        args.Append($"install --id {Quote(packageId)} -e --source winget --accept-source-agreements --accept-package-agreements --silent");
        if (!string.IsNullOrWhiteSpace(overrideArgs))
        {
            args.Append($" --override {Quote(overrideArgs)}");
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] winget {args}");
            return;
        }

        RunChecked("winget", args.ToString(), $"Install {label}", showSpinner: true, silent: true);
    }

    string ResolveGameDir(bool promptIfMissing, bool persist)
    {
        var cfg = LoadWorkspaceConfig();

        var fromArg = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(GameDir) && !IsValidGameDir(fromArg))
        {
            Fail($"Invalid --game-dir value '{GameDir}' (FFX.exe not found).");
        }

        if (IsValidGameDir(fromArg))
        {
            if (persist)
            {
                cfg.GameDir = fromArg;
                SaveWorkspaceConfig(cfg);
            }

            return fromArg;
        }

        if (!RefreshGameDir)
        {
            var fromConfig = NormalizePathOrEmpty(cfg.GameDir);
            if (IsValidGameDir(fromConfig))
            {
                return fromConfig;
            }
        }

        var detected = DetectGameDir();
        if (IsValidGameDir(detected))
        {
            Log.Information($"Auto-detected game directory: {detected}");
            if (persist)
            {
                cfg.GameDir = detected;
                SaveWorkspaceConfig(cfg);
            }

            return detected;
        }

        if (promptIfMissing && InteractiveSession)
        {
            while (true)
            {
                Console.Write("Enter game install directory (must contain FFX.exe): ");
                var input = NormalizePathOrEmpty(Console.ReadLine());
                if (IsValidGameDir(input))
                {
                    if (persist)
                    {
                        cfg.GameDir = input;
                        SaveWorkspaceConfig(cfg);
                    }

                    return input;
                }

                Log.Warning($"Invalid path: {input}");
            }
        }

        Fail("Could not resolve GameDir. Pass --game-dir or run build.cmd auto-deploy.");
        return string.Empty;
    }

    void CleanupReleaseDirAfterDeploy()
    {
        var releaseDir = ResolvePath(".release");
        if (!Directory.Exists(releaseDir))
        {
            return;
        }

        try
        {
            Directory.Delete(releaseDir, recursive: true);
            Log.Information($"Cleaned up release directory: {releaseDir}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not clean release directory '{releaseDir}': {ex.Message}");
        }
    }

    string DetectGameDir()
    {
        foreach (var candidate in GameDirCandidates())
        {
            if (IsValidGameDir(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    IEnumerable<string> GameDirCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in DiscoverGameDirCandidates())
        {
            var normalized = NormalizePathOrEmpty(candidate);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    IEnumerable<string> DiscoverGameDirCandidates()
    {
        foreach (var candidate in DiscoverGameDirCandidatesFromSteamAppManifest())
        {
            yield return candidate;
        }

        foreach (var candidate in DiscoverGameDirCandidatesFromGamesFallbackByPattern())
        {
            yield return candidate;
        }
    }

    IEnumerable<string> DiscoverGameDirCandidatesFromSteamAppManifest()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var libraryRoot in DiscoverSteamLibraryRoots())
        {
            var steamApps = Path.Combine(libraryRoot, "steamapps");
            var appManifestPath = Path.Combine(steamApps, $"appmanifest_{SteamAppIdFfx}.acf");
            if (!File.Exists(appManifestPath))
            {
                continue;
            }

            var installDir = TryReadInstallDirFromAppManifest(appManifestPath);
            if (string.IsNullOrWhiteSpace(installDir))
            {
                continue;
            }

            var candidate = NormalizePathOrEmpty(Path.Combine(steamApps, "common", installDir));
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    IEnumerable<string> DiscoverGameDirCandidatesFromGamesFallbackByPattern()
    {
        var gamesRoot = @"X:\Games";
        if (!Directory.Exists(gamesRoot))
        {
            yield break;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(gamesRoot, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!InstallPathNamePattern.IsMatch(name)) continue;
            yield return dir;
        }
    }

    IEnumerable<string> DiscoverSteamLibraryRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var installRoot in DiscoverSteamInstallRoots())
        {
            if (seen.Add(installRoot))
            {
                yield return installRoot;
            }

            foreach (var libraryRoot in DiscoverSteamLibraryRootsFromLibraryFolders(installRoot))
            {
                if (seen.Add(libraryRoot))
                {
                    yield return libraryRoot;
                }
            }
        }
    }

    IEnumerable<string> DiscoverSteamInstallRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in DiscoverSteamInstallRootsFromRegistry())
        {
            var normalized = NormalizePathOrEmpty(root);
            if (!string.IsNullOrWhiteSpace(normalized) && Directory.Exists(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var fallback in new[]
                 {
                     string.IsNullOrWhiteSpace(pf86) ? string.Empty : Path.Combine(pf86, "Steam"),
                     string.IsNullOrWhiteSpace(pf) ? string.Empty : Path.Combine(pf, "Steam")
                 })
        {
            var normalized = NormalizePathOrEmpty(fallback);
            if (!string.IsNullOrWhiteSpace(normalized) && Directory.Exists(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    IEnumerable<string> DiscoverSteamInstallRootsFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var keyPaths = new[]
        {
            @"SOFTWARE\Valve\Steam",
            @"SOFTWARE\WOW6432Node\Valve\Steam"
        };
        var valueNames = new[] { "InstallPath", "SteamPath" };

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                }
                catch
                {
                    // Ignore unavailable registry views/hives.
                }

                if (baseKey is null)
                {
                    continue;
                }

                using (baseKey)
                {
                    foreach (var keyPath in keyPaths)
                    {
                        using var steamKey = baseKey.OpenSubKey(keyPath);
                        if (steamKey is null) continue;

                        foreach (var valueName in valueNames)
                        {
                            var raw = steamKey.GetValue(valueName) as string;
                            var normalized = NormalizePathOrEmpty(raw);
                            if (!string.IsNullOrWhiteSpace(normalized))
                            {
                                yield return normalized;
                            }
                        }
                    }
                }
            }
        }
    }

    IEnumerable<string> DiscoverSteamLibraryRootsFromLibraryFolders(string steamRoot)
    {
        var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            yield break;
        }

        string text;
        try
        {
            text = File.ReadAllText(libraryFoldersPath);
        }
        catch
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SteamLibraryPathRegex.Matches(text))
        {
            var rawPath = match.Groups["path"].Value;
            var normalizedLibraryRoot = NormalizeSteamLibraryPath(rawPath);
            if (string.IsNullOrWhiteSpace(normalizedLibraryRoot) || !Directory.Exists(normalizedLibraryRoot)) continue;
            if (seen.Add(normalizedLibraryRoot))
            {
                yield return normalizedLibraryRoot;
            }
        }

        foreach (Match match in SteamLibraryLegacyPathRegex.Matches(text))
        {
            var rawPath = match.Groups["path"].Value;
            var normalizedLibraryRoot = NormalizeSteamLibraryPath(rawPath);
            if (string.IsNullOrWhiteSpace(normalizedLibraryRoot) || !Directory.Exists(normalizedLibraryRoot)) continue;
            if (seen.Add(normalizedLibraryRoot))
            {
                yield return normalizedLibraryRoot;
            }
        }
    }

    string TryReadInstallDirFromAppManifest(string appManifestPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(appManifestPath);
        }
        catch
        {
            return string.Empty;
        }

        var match = SteamAppManifestInstallDirRegex.Match(text);
        if (!match.Success)
        {
            return string.Empty;
        }

        var installDir = match.Groups["dir"].Value
            .Trim()
            .Replace(@"\\", @"\")
            .Replace('/', '\\')
            .Trim('\\');

        return string.IsNullOrWhiteSpace(installDir) ? string.Empty : installDir;
    }

    static string NormalizeSteamLibraryPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var unescaped = rawPath
            .Trim()
            .Replace(@"\\", @"\")
            .Replace('/', '\\');

        return NormalizePathOrEmpty(unescaped);
    }

    WorkspaceConfig LoadWorkspaceConfig()
    {
        EnsureDir(Path.GetDirectoryName(WorkspaceConfigPath) ?? string.Empty);

        if (!File.Exists(WorkspaceConfigPath))
        {
            var defaults = CreateDefaultWorkspaceConfig();
            SaveWorkspaceConfig(defaults);
            Log.Information($"Created default workspace config: {WorkspaceConfigPath}");
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(WorkspaceConfigPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            ValidateWorkspaceConfigSchema(root);

            var cfg = ParseWorkspaceConfig(root);
            cfg = NormalizeWorkspaceConfig(cfg);
            SaveWorkspaceConfig(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            Fail($"Invalid workspace config '{WorkspaceConfigPath}': {ex.Message}");
            return CreateDefaultWorkspaceConfig();
        }
    }

    void SaveWorkspaceConfig(WorkspaceConfig cfg)
    {
        EnsureDir(Path.GetDirectoryName(WorkspaceConfigPath) ?? string.Empty);
        var normalized = NormalizeWorkspaceConfig(cfg);
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(WorkspaceConfigPath, json + Environment.NewLine);
    }

    WorkspaceConfig CreateDefaultWorkspaceConfig()
    {
        return new WorkspaceConfig
        {
            Configuration = "Release",
            GameDir = string.Empty,
            AutoDeploy = null,
            DeployPreservePaths = CreateDefaultPreservePaths(),
        };
    }

    static List<string> CreateDefaultPreservePaths() => DefaultPreservePaths.ToList();

    WorkspaceConfig ParseWorkspaceConfig(JsonElement root)
    {
        var defaults = CreateDefaultWorkspaceConfig();
        var deployPreservePaths = ReadStringArrayWithLegacy(
            root,
            canonicalKey: "DeployPreservePaths",
            legacyKeys: ["PreservePaths"],
            fallback: defaults.DeployPreservePaths ?? CreateDefaultPreservePaths());

        return new WorkspaceConfig
        {
            Configuration = ReadStringWithLegacy(root, "Configuration", ["BuildTarget"], defaults.Configuration),
            GameDir = ReadStringWithLegacy(root, "GameDir", ["InstallPath"], defaults.GameDir),
            AutoDeploy = ReadNullableBool(root, "AutoDeploy", defaults.AutoDeploy),
            DeployPreservePaths = deployPreservePaths,
            RetiredToolingSettings = root.EnumerateObject().Where(p => RetiredToolingKeys.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.Clone()),
        };
    }

    void ValidateWorkspaceConfigSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' must be a JSON object.");
        }

        var allowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Configuration",
            "BuildTarget",
            "GameDir",
            "InstallPath",
            "AutoDeploy",
            "DeployPreservePaths",
            "PreservePaths",
        };

        foreach (var property in root.EnumerateObject())
        {
            if (!allowedKeys.Contains(property.Name) && !RetiredToolingKeys.Contains(property.Name))
            {
                Fail($"Unsupported workspace config key '{property.Name}' in '{WorkspaceConfigPath}'.");
            }
        }

        ValidateStringPropertyIfPresent(root, "Configuration");
        ValidateStringPropertyIfPresent(root, "BuildTarget");
        ValidateStringPropertyIfPresent(root, "GameDir");
        ValidateStringPropertyIfPresent(root, "InstallPath");

        if (root.TryGetProperty("AutoDeploy", out var autoDeployElement)
            && (autoDeployElement.ValueKind is not JsonValueKind.True
                && autoDeployElement.ValueKind is not JsonValueKind.False
                && autoDeployElement.ValueKind is not JsonValueKind.Null))
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' must contain 'AutoDeploy' with value true, false, or null.");
        }

        ValidateStringArrayPropertyIfPresent(root, "DeployPreservePaths");
        ValidateStringArrayPropertyIfPresent(root, "PreservePaths");
    }

    WorkspaceConfig NormalizeWorkspaceConfig(WorkspaceConfig cfg)
    {
        var normalized = cfg ?? CreateDefaultWorkspaceConfig();

        normalized.Configuration = NormalizeConfiguration(normalized.Configuration);
        normalized.GameDir = NormalizePathOrEmpty(normalized.GameDir);
        normalized.DeployPreservePaths ??= CreateDefaultPreservePaths();
        normalized.DeployPreservePaths = normalized.DeployPreservePaths
            .Select(NormalizePreservePathEntry)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.DeployPreservePaths.Count == 0)
        {
            normalized.DeployPreservePaths = CreateDefaultPreservePaths();
        }

        return normalized;
    }

    static string NormalizeConfiguration(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        if (trimmed.Equals("Release", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        Fail($"Invalid Configuration '{value}'. Use Debug or Release.");
        return string.Empty;
    }

    static bool TryGetPropertyWithLegacy(JsonElement root, string canonicalKey, IReadOnlyList<string> legacyKeys, out JsonElement value)
    {
        if (root.TryGetProperty(canonicalKey, out value))
        {
            return true;
        }

        for (var i = 0; i < legacyKeys.Count; i++)
        {
            if (root.TryGetProperty(legacyKeys[i], out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    static string ReadStringWithLegacy(JsonElement root, string canonicalKey, IReadOnlyList<string> legacyKeys, string fallback)
    {
        if (!TryGetPropertyWithLegacy(root, canonicalKey, legacyKeys, out var element))
        {
            return fallback;
        }

        return element.ValueKind == JsonValueKind.String ? (element.GetString() ?? string.Empty) : fallback;
    }

    static bool? ReadNullableBool(JsonElement root, string key, bool? fallback)
    {
        if (!root.TryGetProperty(key, out var element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => fallback
        };
    }

    static List<string> ReadStringArrayWithLegacy(JsonElement root, string canonicalKey, IReadOnlyList<string> legacyKeys, IReadOnlyList<string> fallback)
    {
        if (!TryGetPropertyWithLegacy(root, canonicalKey, legacyKeys, out var element))
        {
            return fallback.ToList();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return fallback.ToList();
        }

        return element.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .ToList();
    }

    void ValidateStringPropertyIfPresent(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var element) && element.ValueKind != JsonValueKind.String)
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' property '{key}' must be a string.");
        }
    }

    void ValidateStringArrayPropertyIfPresent(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var element))
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Array || element.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
        {
            Fail($"Workspace config '{WorkspaceConfigPath}' property '{key}' must be an array of strings.");
        }
    }

    string ResolveConfigEnvValue(string value, string propertyName)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        const string envPrefix = "$env:";
        if (!trimmed.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var envName = trimmed[envPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(envName))
        {
            Log.Warning($"Config property '{propertyName}' references an empty environment variable name.");
            return string.Empty;
        }

        var resolved = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            Log.Warning($"Config property '{propertyName}' references environment variable '{envName}', but it is not set.");
            return string.Empty;
        }

        return resolved.Trim();
    }

    string NormalizePreservePathEntry(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return NormalizePathOrEmpty(trimmed);
        }

        return trimmed.Replace('\\', '/').Trim('/');
    }

    void LogConfiguredPreservePathEntries(string preserveRoot, IReadOnlyList<string> normalizedPreservePaths)
    {
        foreach (var entry in normalizedPreservePaths)
        {
            var display = GetPreservePathDisplay(preserveRoot, entry);
            var kind = ClassifyPreservePathEntry(preserveRoot, entry);
            if (kind == "directory")
            {
                Log.Information($"Deploy preserve-paths: preserving directory {display}");
            }
            else
            {
                Log.Information($"Deploy preserve-paths: preserving file {display}");
            }
        }
    }

    string GetPreservePathDisplay(string preserveRoot, string entry)
    {
        if (!Path.IsPathRooted(entry))
        {
            return entry;
        }

        var normalizedRoot = NormalizePathOrEmpty(preserveRoot).Replace('\\', '/').TrimEnd('/');
        var normalizedEntry = NormalizePathOrEmpty(entry).Replace('\\', '/').TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(normalizedRoot)
            && normalizedEntry.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedEntry[(normalizedRoot.Length + 1)..];
            return string.IsNullOrWhiteSpace(relative) ? "." : relative;
        }

        return normalizedEntry;
    }

    string ClassifyPreservePathEntry(string preserveRoot, string entry)
    {
        if (IsGlobPattern(entry))
        {
            return "file";
        }

        var endsWithSeparator = entry.EndsWith("/", StringComparison.Ordinal) || entry.EndsWith("\\", StringComparison.Ordinal);
        var resolved = Path.IsPathRooted(entry)
            ? NormalizePathOrEmpty(entry)
            : NormalizePathOrEmpty(Path.Combine(preserveRoot, entry.Replace('/', Path.DirectorySeparatorChar)));

        if (Directory.Exists(resolved))
        {
            return "directory";
        }

        if (File.Exists(resolved))
        {
            return "file";
        }

        if (endsWithSeparator)
        {
            return "directory";
        }

        return Path.HasExtension(entry) ? "file" : "directory";
    }

    static T FailWithReturn<T>(string message)
    {
        Fail(message);
        return default!;
    }

    string FindVcpkgExecutable()
    {
        if (CommandExists("vcpkg"))
        {
            return "vcpkg";
        }

        // GitHub's windows runners ship vcpkg and point this at it; the release
        // workflow already bootstraps from the same variable.
        foreach (var variable in new[] { "VCPKG_INSTALLATION_ROOT", "VCPKG_ROOT" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var fromEnv = Path.Combine(root, "vcpkg.exe");
            if (File.Exists(fromEnv))
            {
                return fromEnv;
            }
        }

        // Visual Studio bundles vcpkg with the C++ workload. It is not on PATH
        // outside a Developer Command Prompt, so without this the setup gate wants
        // to clone and bootstrap a second copy next to a working one.
        var installPath = ResolveVisualStudioInstallPath();
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            var bundled = Path.Combine(installPath, "VC", "vcpkg", "vcpkg.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }

        var local = WorkspaceDir / "vcpkg" / "vcpkg.exe";
        return File.Exists(local) ? local : string.Empty;
    }

    bool DotNetSdkMajorInstalled(int major)
    {
        var result = RunProcess("dotnet", "--list-sdks", "Check SDKs", showSpinner: false, silent: true);
        if (result.ExitCode != 0) return false;

        return result.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith(major + ".", StringComparison.OrdinalIgnoreCase));
    }

    bool CommandExists(string command)
    {
        var result = RunProcess("where", Quote(command), "Probe command", showSpinner: false, silent: true);
        return result.ExitCode == 0;
    }

    static bool IsValidGameDir(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "FFX.exe"));

    static string NormalizePathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path.Trim()); }
        catch { return string.Empty; }
    }

    static bool AskYesNo(string question, bool defaultYes)
    {
        Console.Write($"{question} {(defaultYes ? "[Y/n]" : "[y/N]")}: ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return defaultYes;
        return input.Equals("y", StringComparison.OrdinalIgnoreCase) || input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    static readonly HashSet<string> RetiredToolingKeys = new(StringComparer.Ordinal)
    {
        "VisionApiUrl", "VisionApiKey", "VisionModel", "OpenApiUrl", "OpenApiKey", "OpenApiModel", "FetchRetries", "FetchRetryCount", "DiscordToken"
    };

    sealed class WorkspaceConfig
    {
        public string Configuration { get; set; } = "Release";
        public string GameDir { get; set; } = string.Empty;
        public bool? AutoDeploy { get; set; }
        public List<string>? DeployPreservePaths { get; set; }
        // Preserve retired tooling values when saving an existing configuration.
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? RetiredToolingSettings { get; set; }
    }

    void RunCleanCore()
    {
        if (Purge && !Yes) Fail("clean --purge is destructive and requires --yes.");
        var result = new CleanupAccountingResult();
        string[] directories =
        {
            ResolvePath(".workspace/local-build"),
            Path.Combine(RootDirectory, "bin"),
            Path.Combine(RootDirectory, "obj"),
            Path.Combine(RootDirectory, "build", "bin"),
            Path.Combine(RootDirectory, "build", "obj"),
            Path.Combine(RootDirectory, "tests", "Parry.Tests", "bin"),
            Path.Combine(RootDirectory, "tests", "Parry.Tests", "obj"),
            Path.Combine(ResolvePath(FahrenheitDir), "artifacts"),
            ResolvePath(".release/stage"),
            ResolvePath(".release/preflight")
        };
        foreach (var path in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            DeleteDirectoryMaybeWithAccounting(path, result);
        if (Purge) DeleteDirectoryMaybeWithAccounting(ResolvePath(".release"), result);
        Log.Information($"Clean complete. Removed files={result.FilesRemoved}, directories={result.DirectoriesRemoved}, reclaimed={FormatBytes(result.BytesReclaimed)}.");
    }
    void EnsureLocalBuildPrerequisitesForSetup()
    {
        EnsureGitInstalled();
        EnsureDotNetSdk10Installed();
        EnsureMsbuildInstalled();
        EnsureVcpkgInstalledAndIntegrated();
    }

    bool EnsureProjectWorkspaceSetup(string resolvedConfiguration)
    {
        if (!ShouldRunProjectWorkspaceSetup())
        {
            Log.Information("Project workspace already initialized. Skipping build/Build.proj Setup.");
            return false;
        }

        RunBuildProjTarget(
            "Setup",
            resolvedConfiguration,
            includeNativeMsbuild: false,
            fahrenheitRef: ResolveFahrenheitRef(useReleaseRef: false));
        return true;
    }

    bool ShouldRunProjectWorkspaceSetup()
    {
        var fahrenheitRoot = ResolvePath(FahrenheitDir);
        var gitDir = Path.Combine(fahrenheitRoot, ".git");
        if (!Directory.Exists(gitDir))
        {
            return true;
        }

        var managedCoreProject = Path.Combine(fahrenheitRoot, "src", "core", "Fahrenheit.csproj");
        var managedRuntimeProject = Path.Combine(fahrenheitRoot, "src", "runtime", "Fahrenheit.Runtime.csproj");
        if (!File.Exists(managedCoreProject) || !File.Exists(managedRuntimeProject))
        {
            return true;
        }

        return false;
    }

    void EnsureGameDirConfiguredForSetup()
    {
        var cfg = LoadWorkspaceConfig();
        var resolvedGameDir = ResolveGameDirForAutoDeploySetup(cfg, requiredForSetup: true);
        if (!IsValidGameDir(resolvedGameDir))
        {
            Fail("Could not resolve a valid GameDir for setup. Pass --game-dir <path> (must contain FFX.exe).");
            return;
        }

        var normalized = NormalizePathOrEmpty(resolvedGameDir);
        if (!normalized.Equals(NormalizePathOrEmpty(cfg.GameDir), StringComparison.OrdinalIgnoreCase))
        {
            cfg.GameDir = normalized;
            SaveWorkspaceConfig(cfg);
            Log.Information($"Saved GameDir in workspace config: {cfg.GameDir}");
        }
    }

    void SetupAutoDeployCore()
    {
        var cfg = LoadWorkspaceConfig();
        var hasPathOverride = !string.IsNullOrWhiteSpace(GameDir);
        var hasValidPathOverride = false;

        if (hasPathOverride)
        {
            var normalizedPath = NormalizePathOrEmpty(GameDir);
            if (!IsValidGameDir(normalizedPath))
            {
                Log.Warning($"Provided --game-dir is invalid: {GameDir}");
            }
            else
            {
                cfg.GameDir = normalizedPath;
                hasValidPathOverride = true;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            cfg.GameDir = string.Empty;
        }

        var alreadyConfigured = cfg.AutoDeploy == true && IsValidGameDir(cfg.GameDir);
        if (alreadyConfigured && !hasPathOverride && !RefreshGameDir)
        {
            SaveWorkspaceConfig(cfg);
            Log.Information($"Auto deploy already configured: GameDir={cfg.GameDir}");
            return;
        }

        if (InteractiveSession && !alreadyConfigured && !hasValidPathOverride)
        {
            if (!AskYesNo("Would you like to setup automatic build deployment into the game installation path?", defaultYes: true))
            {
                cfg.AutoDeploy = false;
                SaveWorkspaceConfig(cfg);
                Log.Warning("Automatic deployment setup skipped for now.");
                Log.Information("You can configure it later with: build.cmd auto-deploy");
                return;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            var resolvedGameDir = ResolveGameDirForAutoDeploySetup(cfg, requiredForSetup: false);
            if (IsValidGameDir(resolvedGameDir))
            {
                cfg.GameDir = resolvedGameDir;
            }
        }

        if (!IsValidGameDir(cfg.GameDir))
        {
            cfg.GameDir = string.Empty;
            cfg.AutoDeploy = false;
            SaveWorkspaceConfig(cfg);
            Log.Warning("No valid game installation path was configured.");
            Log.Information("Automatic deployment setup was skipped. You can configure it later with: build.cmd auto-deploy");
            return;
        }

        cfg.AutoDeploy = true;
        SaveWorkspaceConfig(cfg);
        Log.Information($"Configured automatic deployment: GameDir={cfg.GameDir}");
    }

    string ResolveGameDirForAutoDeploySetup(WorkspaceConfig cfg, bool requiredForSetup)
    {
        var fromArg = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(fromArg))
        {
            if (IsValidGameDir(fromArg))
            {
                return fromArg;
            }

            if (!InteractiveSession)
            {
                Fail($"Invalid --game-dir value '{GameDir}' (FFX.exe not found).");
            }

            if (!InteractiveSession || requiredForSetup)
            {
                Log.Warning($"Provided --game-dir is invalid: {fromArg}");
            }
        }

        if (!RefreshGameDir)
        {
            var fromConfig = NormalizePathOrEmpty(cfg.GameDir);
            if (IsValidGameDir(fromConfig))
            {
                return fromConfig;
            }
        }

        var detected = DetectGameDir();
        if (IsValidGameDir(detected))
        {
            if (!InteractiveSession)
            {
                return detected;
            }

            if (AskYesNo($"Detected game path '{detected}'. Use this path?", defaultYes: true))
            {
                return detected;
            }
        }

        if (!InteractiveSession)
        {
            if (requiredForSetup)
            {
                Fail(
                    "Could not resolve GameDir in non-interactive setup mode. " +
                    "Pass --game-dir <path> (must contain FFX.exe).");
            }

            return string.Empty;
        }

        while (true)
        {
            Console.Write("Enter game installation directory (must contain FFX.exe): ");
            var manual = NormalizePathOrEmpty(Console.ReadLine());
            if (IsValidGameDir(manual))
            {
                return manual;
            }

            Log.Warning($"Invalid game directory: {manual}");
            if (!requiredForSetup && !AskYesNo("Try entering GameDir again?", defaultYes: true))
            {
                return string.Empty;
            }
        }
    }
}
