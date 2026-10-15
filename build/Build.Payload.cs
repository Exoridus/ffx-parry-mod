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

    void BuildCore(string target, string configuration, bool useReleaseRef, bool allowAutoDeploy = true)
    {
        var resolvedConfiguration = ResolveBuildConfiguration(configuration);
        var effectiveFahrenheitRef = ResolveFahrenheitRef(useReleaseRef);
        var t = target.Trim().ToLowerInvariant();
        if (t == "mod")
        {
            RunBuildProjTarget("BuildModOnly", resolvedConfiguration, includeNativeMsbuild: false, fahrenheitRef: effectiveFahrenheitRef);
        }
        else if (t == "full")
        {
            RunBuildProjTarget("Build", resolvedConfiguration, includeNativeMsbuild: true, fahrenheitRef: effectiveFahrenheitRef);
        }
        else
        {
            Fail($"Invalid build target '{target}'. Use mod or full.");
        }

        if (allowAutoDeploy)
        {
            TryAutoDeployAfterBuild(t, resolvedConfiguration, useReleaseRef);
        }
    }

    void StartCore()
    {
        EnsureStageRuntimePrerequisites();

        var gameDir = ResolveGameDir(promptIfMissing: true, persist: false);
        var binDir = Path.Combine(gameDir, "fahrenheit", "bin");
        var stage0Exe = Path.Combine(binDir, "fhstage0.exe");
        if (!File.Exists(stage0Exe))
        {
            Fail($"Missing stage0 loader: {stage0Exe}{Environment.NewLine}Run build+deploy first.");
        }

        Log.Information($"Launching Fahrenheit loader from: {binDir}{(Elevated ? " (elevated)" : string.Empty)}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = stage0Exe,
                Arguments = "..\\..\\FFX.exe",
                WorkingDirectory = binDir,
                UseShellExecute = true,
                Verb = Elevated ? "runas" : "open"
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                Fail("Failed to start fhstage0.exe.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Fail("Start canceled by user (UAC prompt declined).");
        }
        catch (Exception ex)
        {
            Fail($"Failed to start fhstage0.exe: {ex.Message}");
        }
    }

    void EnsureStageRuntimePrerequisites()
    {
        if (HasDotNetRuntime10HostFxr())
        {
            return;
        }

        Fail(
            "Missing Microsoft .NET Runtime 10, required by Fahrenheit stage1 loader."
            + Environment.NewLine
            + "Install with:"
            + Environment.NewLine
            + "  winget install --id Microsoft.DotNet.Runtime.10 --exact --accept-package-agreements --accept-source-agreements");
    }

    static bool HasDotNetRuntime10HostFxr()
    {
        return EnumerateHostFxrCandidates(major: 10).Any();
    }

    static IEnumerable<string> EnumerateHostFxrCandidates(int major)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var fxrRoot = Path.Combine(root, "dotnet", "host", "fxr");
            if (!Directory.Exists(fxrRoot))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(fxrRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(major + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dll = Path.Combine(dir, "hostfxr.dll");
                if (File.Exists(dll))
                {
                    yield return dll;
                }
            }
        }
    }

    void RunBuildProjTarget(string target, string configuration, bool includeNativeMsbuild, string fahrenheitRef)
    {
        var resolvedConfiguration = ResolveBuildConfiguration(configuration);
        var args = new StringBuilder();
        args.Append("msbuild ");
        args.Append(Quote(RootDirectory / "build/Build.proj"));
        args.Append($" -nologo -verbosity:{ResolveMsBuildVerbosity()}");
        args.Append($" -t:{target}");
        args.Append($" -p:Configuration={Quote(resolvedConfiguration)}");
        args.Append($" -p:FahrenheitRepo={Quote(FahrenheitRepo)}");
        args.Append($" -p:FahrenheitDir={Quote(ResolvePath(FahrenheitDir))}");
        args.Append($" -p:FahrenheitRef={Quote(fahrenheitRef)}");

        if (includeNativeMsbuild && !string.IsNullOrWhiteSpace(NativeMSBuildExe))
        {
            args.Append($" -p:NativeMSBuildExe={Quote(NativeMSBuildExe)}");
        }

        if (includeNativeMsbuild)
        {
            var resolvedToolset = ResolveNativePlatformToolset();
            if (!string.IsNullOrWhiteSpace(resolvedToolset))
            {
                args.Append($" -p:NativePlatformToolset={Quote(resolvedToolset)}");
            }
        }

        RunChecked("dotnet", args.ToString(), $"MSBuild target {target}");
    }

    string ResolveNativePlatformToolset()
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(Toolset))
        {
            return Toolset.Trim();
        }

        var installPath = ResolveVisualStudioInstallPath();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return string.Empty;
        }

        var vcRoot = Path.Combine(installPath, "MSBuild", "Microsoft", "VC");
        if (!Directory.Exists(vcRoot))
        {
            return string.Empty;
        }

        var candidates = new[] { "v145", "v144", "v143", "v142" };
        var vcVersions = Directory.EnumerateDirectories(vcRoot, "v*")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var vcVersion in vcVersions)
        {
            var platformToolsetsRoot = Path.Combine(vcVersion, "Platforms", "Win32", "PlatformToolsets");
            if (!Directory.Exists(platformToolsetsRoot))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var propsPath = Path.Combine(platformToolsetsRoot, candidate, "Toolset.props");
                if (File.Exists(propsPath))
                {
                    Log.Information($"Using native platform toolset override: {candidate}");
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    static string ResolveVsWherePath()
    {
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrWhiteSpace(programFilesX86))
        {
            return string.Empty;
        }

        return Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
    }

    void TryAutoDeployAfterBuild(string buildTarget, string configuration, bool useReleaseRef)
    {
        if (useReleaseRef || IsServerBuild)
        {
            return;
        }

        var cfg = LoadWorkspaceConfig();
        if (AutoDeployOverride.HasValue && !AutoDeployOverride.Value)
        {
            Log.Information("Automatic deployment was disabled by --no-auto-deploy.");
            return;
        }

        bool autoDeployEnabled;
        if (AutoDeployOverride.HasValue)
        {
            autoDeployEnabled = AutoDeployOverride.Value;
        }
        else if (cfg.AutoDeploy.HasValue)
        {
            autoDeployEnabled = cfg.AutoDeploy.Value;
        }
        else if (InteractiveSession)
        {
            autoDeployEnabled = AskYesNo("Enable automatic deployment after build?", defaultYes: true);
            cfg.AutoDeploy = autoDeployEnabled;
            SaveWorkspaceConfig(cfg);
        }
        else
        {
            Log.Information("AutoDeploy is not configured (null) in non-interactive mode. Skipping automatic deploy.");
            return;
        }

        if (!autoDeployEnabled)
        {
            return;
        }

        var gameDir = NormalizePathOrEmpty(GameDir);
        if (!string.IsNullOrWhiteSpace(GameDir) && !IsValidGameDir(gameDir))
        {
            Log.Warning($"Invalid --game-dir value '{GameDir}' (FFX.exe not found). Skipping automatic deploy.");
            return;
        }

        if (!IsValidGameDir(gameDir) && !RefreshGameDir)
        {
            gameDir = NormalizePathOrEmpty(cfg.GameDir);
        }

        if (!IsValidGameDir(gameDir))
        {
            gameDir = DetectGameDir();
        }

        if (!IsValidGameDir(gameDir) && InteractiveSession)
        {
            Console.Write("Enter game installation directory for auto-deploy (must contain FFX.exe): ");
            var manual = NormalizePathOrEmpty(Console.ReadLine());
            if (IsValidGameDir(manual))
            {
                gameDir = manual;
            }
        }

        if (!IsValidGameDir(gameDir))
        {
            Log.Warning("Automatic deploy is enabled but no valid GameDir could be resolved. Skipping automatic deploy.");
            Log.Information("Run: build.cmd auto-deploy --game-dir <path>");
            return;
        }

        var normalizedGameDir = NormalizePathOrEmpty(gameDir);
        if (!normalizedGameDir.Equals(NormalizePathOrEmpty(cfg.GameDir), StringComparison.OrdinalIgnoreCase))
        {
            cfg.GameDir = normalizedGameDir;
            SaveWorkspaceConfig(cfg);
        }

        var normalizedBuildTarget = (buildTarget ?? string.Empty).Trim().ToLowerInvariant();
        var deployTarget = normalizedBuildTarget switch
        {
            "full" => "full",
            "mod" => "mod",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(deployTarget))
        {
            Log.Warning($"Unknown build target '{buildTarget}'. Skipping automatic deploy.");
            return;
        }

        var ok = DeployFromArtifacts(normalizedGameDir, configuration, deployTarget, failOnError: false, reason: "Automatic deploy");
        if (!ok)
        {
            Log.Warning("Automatic deploy failed and was skipped.");
        }
    }

    bool DeployFromArtifacts(string gameDir, string configuration, string target, bool failOnError, string reason)
    {
        var normalizedTarget = target.Trim().ToLowerInvariant();
        if (normalizedTarget != "mod" && normalizedTarget != "full")
        {
            var msg = $"Invalid deploy target '{target}'. Use mod or full.";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        var normalizedGameDir = NormalizePathOrEmpty(gameDir);
        if (!IsValidGameDir(normalizedGameDir))
        {
            var msg = $"Invalid GameDir '{gameDir}' (FFX.exe not found).";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        var resolvedConfiguration = ResolveBuildConfiguration(configuration);
        var deployCfg = resolvedConfiguration.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "rel" : "dbg";
        var sourceRoot = Path.Combine(ResolvePath(FahrenheitDir), "artifacts", "deploy", deployCfg);
        if (!Directory.Exists(sourceRoot))
        {
            var msg = $"Build output not found: {sourceRoot}";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        var targetRoot = Path.Combine(normalizedGameDir, "fahrenheit");
        var sourcePath = normalizedTarget == "full"
            ? sourceRoot
            : Path.Combine(sourceRoot, "mods", ModId);
        var destinationPath = normalizedTarget == "full"
            ? targetRoot
            : Path.Combine(targetRoot, "mods", ModId);

        if (!Directory.Exists(sourcePath))
        {
            var msg = $"Deploy source path does not exist: {sourcePath}";
            if (failOnError) Fail(msg);
            Log.Warning(msg);
            return false;
        }

        var cfg = LoadWorkspaceConfig();
        var preservePaths = (cfg.DeployPreservePaths ?? []).Append("config").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        try
        {
            if (!DryRun)
            {
                if (Parry.Build.SettingsMigration.Migrate(targetRoot))
                    Log.Information("Migrated legacy Parry settings.");
            }
            SyncDirectoryRecursiveWithPreservePaths(sourcePath, destinationPath, targetRoot, preservePaths);
            if (DryRun)
            {
                Log.Information($"{reason}: [DRY-RUN] simulated deploy {normalizedTarget} to {destinationPath}");
            }
            else
            {
                Log.Information($"{reason}: deployed {normalizedTarget} to {destinationPath}");
                CleanupReleaseDirAfterDeploy();
            }
            return true;
        }
        catch (Exception ex)
        {
            if (failOnError)
            {
                Fail($"{reason} failed: {ex.Message}");
            }

            Log.Warning($"{reason} failed: {ex.Message}");
            return false;
        }
    }

    void MirrorDirectoryRecursive(string source, string destination)
    {
        EnsureDirectoryMaybe(destination);
        var destinationExists = Directory.Exists(destination);

        if (destinationExists)
        {
            foreach (var file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(destination, file);
                var sourceFile = Path.Combine(source, relative);
                if (!File.Exists(sourceFile))
                {
                    DeleteFileMaybe(file);
                }
            }

            foreach (var dir in Directory.GetDirectories(destination, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                var relative = Path.GetRelativePath(destination, dir);
                var sourceDir = Path.Combine(source, relative);
                if (!Directory.Exists(sourceDir))
                {
                    DeleteDirectoryMaybe(dir);
                }
            }
        }

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            EnsureDirectoryMaybe(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent)) EnsureDirectoryMaybe(parent);
            CopyFileMaybe(file, target, overwrite: true);
        }
    }

    void SyncDirectoryRecursiveWithPreservePaths(string source, string destination, string preserveRoot, IReadOnlyList<string> preservePaths)
    {
        var normalizedPreservePaths = (preservePaths ?? [])
            .Select(NormalizePreservePathEntry)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPreservePaths.Count == 0)
        {
            MirrorDirectoryRecursive(source, destination);
            return;
        }

        LogConfiguredPreservePathEntries(preserveRoot, normalizedPreservePaths);

        EnsureDirectoryMaybe(destination);
        var destinationExists = Directory.Exists(destination);

        var relativeEntries = normalizedPreservePaths
            .Where(x => !Path.IsPathRooted(x))
            .Where(x => !IsGlobPattern(x))
            .Select(NormalizeRelativeForComparison)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var relativeGlobEntries = normalizedPreservePaths
            .Where(x => !Path.IsPathRooted(x))
            .Where(IsGlobPattern)
            .Select(NormalizeRelativeForComparison)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var absoluteEntries = normalizedPreservePaths
            .Where(Path.IsPathRooted)
            .Where(x => !IsGlobPattern(x))
            .Select(x => NormalizePathOrEmpty(x).Replace('\\', '/').TrimEnd('/'))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var absoluteGlobEntries = normalizedPreservePaths
            .Where(Path.IsPathRooted)
            .Where(IsGlobPattern)
            .Select(x => NormalizePathOrEmpty(x).Replace('\\', '/').TrimEnd('/'))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var relativeGlobPatterns = relativeGlobEntries
            .Select(CreateGlobRegex)
            .ToList();
        var absoluteGlobPatterns = absoluteGlobEntries
            .Select(CreateGlobRegex)
            .ToList();
        var relativeGlobPrefixes = relativeGlobEntries
            .Select(GetGlobLiteralPrefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var absoluteGlobPrefixes = absoluteGlobEntries
            .Select(GetGlobLiteralPrefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (destinationExists)
        {
            foreach (var file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(destination, file);
                var sourceFile = Path.Combine(source, relative);
                if (File.Exists(sourceFile))
                {
                    continue;
                }

                if (IsTargetPathPreserved(file, preserveRoot, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns, out _))
                {
                    continue;
                }

                DeleteFileMaybe(file);
            }

            foreach (var dir in Directory.GetDirectories(destination, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                var relative = Path.GetRelativePath(destination, dir);
                var sourceDir = Path.Combine(source, relative);
                if (Directory.Exists(sourceDir))
                {
                    continue;
                }

                if (IsTargetPathPreserved(dir, preserveRoot, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns, out _))
                {
                    continue;
                }

                var normalizedDirAbsolute = NormalizePathOrEmpty(dir).Replace('\\', '/').TrimEnd('/');
                var normalizedRelativeToRoot = NormalizeRelativeForComparison(Path.GetRelativePath(preserveRoot, dir));
                if (DirectoryContainsPreservedPath(normalizedRelativeToRoot, normalizedDirAbsolute, relativeEntries, absoluteEntries, relativeGlobPrefixes, absoluteGlobPrefixes))
                {
                    continue;
                }

                DeleteDirectoryMaybe(dir);
            }
        }

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            var target = Path.Combine(destination, relative);
            if (IsTargetPathPreserved(target, preserveRoot, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns, out _))
            {
                continue;
            }

            EnsureDirectoryMaybe(target);
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            if (IsTargetPathPreserved(target, preserveRoot, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns, out _))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent)) EnsureDirectoryMaybe(parent);
            CopyFileMaybe(file, target, overwrite: true);
        }
    }

    void EnsureDirectoryMaybe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            return;
        }

        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Create directory: {path}");
            }

            return;
        }

        Directory.CreateDirectory(path);
    }

    void DeleteFileMaybe(string path)
    {
        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Delete file: {path}");
            }

            return;
        }

        File.Delete(path);
    }

    void DeleteDirectoryMaybe(string path)
    {
        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Delete directory: {path}");
            }

            return;
        }

        Directory.Delete(path, recursive: true);
    }

    void CopyFileMaybe(string source, string target, bool overwrite)
    {
        if (DryRun)
        {
            if (IsLogVerbosityAtLeast(BuildLogVerbosity.Detailed))
            {
                Log.Information($"[DRY-RUN] Copy file: {source} -> {target}");
            }

            return;
        }

        File.Copy(source, target, overwrite: overwrite);
    }

    bool IsTargetPathPreserved(
        string targetPath,
        string preserveRoot,
        IReadOnlyList<string> relativeEntries,
        IReadOnlyList<string> absoluteEntries,
        IReadOnlyList<Regex> relativeGlobPatterns,
        IReadOnlyList<Regex> absoluteGlobPatterns,
        out string relativeToRootDisplay)
    {
        var normalizedTargetAbsolute = NormalizePathOrEmpty(targetPath).Replace('\\', '/').TrimEnd('/');
        var normalizedRelativeToRoot = NormalizeRelativeForComparison(Path.GetRelativePath(preserveRoot, targetPath));
        relativeToRootDisplay = string.IsNullOrWhiteSpace(normalizedRelativeToRoot) ? "." : normalizedRelativeToRoot;
        return IsPreservedPath(normalizedRelativeToRoot, normalizedTargetAbsolute, relativeEntries, absoluteEntries, relativeGlobPatterns, absoluteGlobPatterns);
    }

    static bool IsPreservedPath(
        string relativeToRoot,
        string targetAbsolute,
        IReadOnlyList<string> relativeEntries,
        IReadOnlyList<string> absoluteEntries,
        IReadOnlyList<Regex> relativeGlobPatterns,
        IReadOnlyList<Regex> absoluteGlobPatterns)
    {
        foreach (var absoluteEntry in absoluteEntries)
        {
            if (targetAbsolute.Equals(absoluteEntry, StringComparison.OrdinalIgnoreCase) ||
                targetAbsolute.StartsWith(absoluteEntry + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var absolutePattern in absoluteGlobPatterns)
        {
            if (absolutePattern.IsMatch(targetAbsolute))
            {
                return true;
            }
        }

        if (relativeToRoot.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var relativeEntry in relativeEntries)
        {
            if (relativeToRoot.Equals(relativeEntry, StringComparison.OrdinalIgnoreCase) ||
                relativeToRoot.StartsWith(relativeEntry + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var relativePattern in relativeGlobPatterns)
        {
            if (relativePattern.IsMatch(relativeToRoot))
            {
                return true;
            }
        }

        return false;
    }

    static bool DirectoryContainsPreservedPath(
        string directoryRelativeToRoot,
        string directoryAbsolute,
        IReadOnlyList<string> relativeEntries,
        IReadOnlyList<string> absoluteEntries,
        IReadOnlyList<string> relativeGlobPrefixes,
        IReadOnlyList<string> absoluteGlobPrefixes)
    {
        foreach (var absoluteEntry in absoluteEntries)
        {
            if (absoluteEntry.Equals(directoryAbsolute, StringComparison.OrdinalIgnoreCase) ||
                absoluteEntry.StartsWith(directoryAbsolute + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (directoryRelativeToRoot.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(directoryRelativeToRoot))
        {
            return relativeEntries.Count > 0 || relativeGlobPrefixes.Count > 0;
        }

        foreach (var relativeEntry in relativeEntries)
        {
            if (relativeEntry.Equals(directoryRelativeToRoot, StringComparison.OrdinalIgnoreCase) ||
                relativeEntry.StartsWith(directoryRelativeToRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var relativePrefix in relativeGlobPrefixes)
        {
            if (string.IsNullOrWhiteSpace(relativePrefix))
            {
                return true;
            }

            if (relativePrefix.Equals(directoryRelativeToRoot, StringComparison.OrdinalIgnoreCase) ||
                relativePrefix.StartsWith(directoryRelativeToRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var absolutePrefix in absoluteGlobPrefixes)
        {
            if (string.IsNullOrWhiteSpace(absolutePrefix))
            {
                return true;
            }

            if (absolutePrefix.Equals(directoryAbsolute, StringComparison.OrdinalIgnoreCase) ||
                absolutePrefix.StartsWith(directoryAbsolute + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsGlobPattern(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && (value.Contains('*') || value.Contains('?') || value.Contains('['));
    }

    static Regex CreateGlobRegex(string pattern)
    {
        var normalized = (pattern ?? string.Empty).Replace('\\', '/').Trim();
        var sb = new StringBuilder("^");

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '*')
            {
                var isDoubleStar = i + 1 < normalized.Length && normalized[i + 1] == '*';
                if (isDoubleStar)
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }

                continue;
            }

            if (c == '?')
            {
                sb.Append("[^/]");
                continue;
            }

            sb.Append(Regex.Escape(c.ToString()));
        }

        sb.Append("$");
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    static string GetGlobLiteralPrefix(string pattern)
    {
        var normalized = (pattern ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var wildcardIndex = normalized.IndexOfAny(['*', '?', '[']);
        if (wildcardIndex < 0)
        {
            return normalized;
        }

        if (wildcardIndex == 0)
        {
            return string.Empty;
        }

        return normalized[..wildcardIndex].TrimEnd('/');
    }

    static string NormalizeRelativeForComparison(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim();
        if (normalized == ".")
        {
            return string.Empty;
        }

        return normalized.Trim('/');
    }
    void RunVerifyCore(string configuration)
    {
        var resolvedConfiguration = ResolveBuildConfiguration(configuration);
        ValidateJsonConfigsCore();
        AssertAutomationDocsUpToDate();
        BuildCore("full", resolvedConfiguration, useReleaseRef: false, allowAutoDeploy: false);
        RunTestsIfAny(resolvedConfiguration);
    }

    void DeployCore(string target, string configuration)
    {
        var t = target.Trim().ToLowerInvariant();
        if (t != "mod" && t != "full")
        {
            Fail($"Invalid deploy target '{target}'. Use mod or full.");
        }

        var gameDir = ResolveGameDir(promptIfMissing: true, persist: false);
        DeployFromArtifacts(gameDir, configuration, t, failOnError: true, reason: "Manual deploy");
    }
}
