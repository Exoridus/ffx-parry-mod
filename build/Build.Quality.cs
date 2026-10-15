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

    void RunTestsIfAny(string configuration)
    {
        var projects = new List<string>();
        var testRoot = Path.Combine(RootDirectory, "tests");

        if (Directory.Exists(testRoot))
        {
            projects.AddRange(Directory.EnumerateFiles(testRoot, "*.csproj", SearchOption.AllDirectories));
        }

        projects.AddRange(Directory.EnumerateFiles(RootDirectory, "*.csproj", SearchOption.TopDirectoryOnly));

        projects = projects
            .Where(path => Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projects.Count == 0)
        {
            Log.Information("No test projects found outside .workspace. Skipping tests.");
            return;
        }

        foreach (var project in projects)
        {
            RunChecked(
                "dotnet",
                $"test {Quote(project)} --configuration {Quote(configuration)} --nologo --verbosity {ResolveDotNetCliVerbosity()}",
                $"Run tests for {Path.GetFileName(project)}");
        }
    }
    void SyncAutomationDocsCore()
    {
        var buildDocsPath = ResolvePath("docs/automation.md");

        File.WriteAllText(buildDocsPath, NormalizeLineEndings(GenerateBuildAutomationDocs()));

        Log.Information($"Updated automation docs: {buildDocsPath}");
    }

    void AssertAutomationDocsUpToDate()
    {
        AssertDocsMatchGenerated("docs/automation.md", GenerateBuildAutomationDocs());
    }

    void AssertDocsMatchGenerated(string relativePath, string expectedContent)
    {
        var fullPath = ResolvePath(relativePath);
        if (!File.Exists(fullPath))
        {
            Fail($"Missing generated docs file '{relativePath}'. Run: .\\build.cmd docs-sync");
        }

        var currentContent = NormalizeLineEndings(File.ReadAllText(fullPath));
        var normalizedExpected = NormalizeLineEndings(expectedContent);
        if (!string.Equals(currentContent, normalizedExpected, StringComparison.Ordinal))
        {
            Fail($"Generated docs '{relativePath}' are stale. Run: .\\build.cmd docs-sync");
        }
    }

    static string NormalizeLineEndings(string content) => content.Replace("\r\n", "\n");

    string GenerateBuildAutomationDocs()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Automation Overview");
        sb.AppendLine();
        sb.AppendLine("`build.cmd` is the project lifecycle entrypoint.");
        sb.AppendLine();
        sb.AppendLine("_Auto-generated from build workflow metadata. Do not edit manually; run `.\\build.cmd docs-sync`._");
        sb.AppendLine();
        sb.AppendLine("Quick command discovery:");
        sb.AppendLine("- `.\\build.cmd help`");
        sb.AppendLine("- `.\\build.cmd -h <workflow>`");
        sb.AppendLine("- Bool parameters support both `--flag` and `--no-flag`.");
        sb.AppendLine("- Global verbosity: `--verbosity|-v quiet|minimal|normal|detailed|diagnostic` (default: `normal`).");
        sb.AppendLine("- Recommended escalation: `quiet` -> `normal` -> `detailed` -> `diagnostic`.");
        sb.AppendLine("- Global config path: `--config-path` (shorthand: `-c`).");
        sb.AppendLine("- Common shorthand: `-c <config-path>`, `-n` (`--dry-run`).");
        sb.AppendLine("- Agent guidance: use `--verbosity quiet` for routine `.\\build.cmd verify`, `.\\build.cmd build`, and `.\\build.cmd deploy`.");
        sb.AppendLine();

        foreach (var section in BuildWorkflowSections)
        {
            sb.AppendLine($"## {section.Heading} Workflows");
            sb.AppendLine();

            foreach (var workflow in section.Workflows)
            {
                var help = CaptureWorkflowHelpBlock(workflow.Name);
                AppendWorkflowHelpBlock(sb, ".\\build.cmd", help, "build.cmd");
            }
        }

        return sb.ToString();
    }

    static void AppendWorkflowHelpBlock(StringBuilder sb, string commandPrefix, WorkflowHelpBlock help, string replaceToken)
    {
        sb.AppendLine($"- `{commandPrefix} {help.Workflow}`");
        sb.AppendLine($"  - {help.Purpose}");

        if (help.Parameters.Count > 0)
        {
            sb.AppendLine("  - Parameters:");
            foreach (var parameter in help.Parameters)
            {
                sb.AppendLine($"  - {parameter}");
            }
        }

        if (help.Examples.Count > 0)
        {
            sb.AppendLine("  - Examples:");
            foreach (var example in help.Examples)
            {
                var normalizedExample = example.Replace(replaceToken, commandPrefix, StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"  - `{normalizedExample}`");
            }
        }

        sb.AppendLine();
    }
    Target Doctor => _ => _.Executes(RunDoctorCore);

    Target Format => _ => _.Executes(RunFormatFixCore);

    Target Lint => _ => _.Executes(() => RunLintCore(RequestedConfiguration));

    Target Smoke => _ => _.Executes(() => RunSmokeCore(RequestedConfiguration));

    void RunDoctorCore()
    {
        var requiredFailures = new List<string>();
        var optionalWarnings = new List<string>();

        Log.Information("Doctor report");
        Log.Information($"  OS: {RuntimeInformation.OSDescription}");
        Log.Information($"  Architecture: {RuntimeInformation.OSArchitecture}");
        Log.Information($"  Runtime: {RuntimeInformation.FrameworkDescription}");

        var hasGit = CommandExists("git");
        LogDoctorCheck("Git", hasGit, required: true, "Required for clone/update, changelog, and tag workflows.");
        if (!hasGit) requiredFailures.Add("Git");

        var hasDotNet = CommandExists("dotnet");
        LogDoctorCheck(".NET SDK CLI", hasDotNet, required: true, "Required for NUKE and managed builds.");
        if (!hasDotNet) requiredFailures.Add(".NET SDK CLI");

        var hasSdk10 = hasDotNet && DotNetSdkMajorInstalled(10);
        LogDoctorCheck(".NET SDK 10.x", hasSdk10, required: true, "Pinned by global.json.");
        if (!hasSdk10) requiredFailures.Add(".NET SDK 10.x");

        var hasWinget = CommandExists("winget");
        LogDoctorCheck("winget", hasWinget, required: false, "Used by build.cmd install for automated prerequisite setup.");
        if (!hasWinget) optionalWarnings.Add("winget");

        var hasJava = CommandExists("java");
        LogDoctorCheck("Java", hasJava, required: false, "Required only for FFXDataParser workflows.");
        if (!hasJava) optionalWarnings.Add("java");

        var hasMaven = CommandExists("mvn");
        LogDoctorCheck("Maven", hasMaven, required: false, "Required only for FFXDataParser workflows.");
        if (!hasMaven) optionalWarnings.Add("mvn");

        if (Full)
        {
            var hasMsbuild = CommandExists("msbuild");
            LogDoctorCheck("MSBuild", hasMsbuild, required: true, "Required for full native Fahrenheit builds.");
            if (!hasMsbuild) requiredFailures.Add("MSBuild");

            var hasVcpkg = !string.IsNullOrWhiteSpace(FindVcpkgExecutable());
            LogDoctorCheck("vcpkg", hasVcpkg, required: true, "Required for native dependency resolution in full builds.");
            if (!hasVcpkg) requiredFailures.Add("vcpkg");
        }
        else
        {
            var hasMsbuild = CommandExists("msbuild");
            LogDoctorCheck("MSBuild", hasMsbuild, required: false, "Needed for full Fahrenheit native builds.");
            if (!hasMsbuild) optionalWarnings.Add("msbuild");

            var hasVcpkg = !string.IsNullOrWhiteSpace(FindVcpkgExecutable());
            LogDoctorCheck("vcpkg", hasVcpkg, required: false, "Needed for full Fahrenheit native builds.");
            if (!hasVcpkg) optionalWarnings.Add("vcpkg");
        }

        if (requiredFailures.Count > 0)
        {
            Fail("Doctor failed. Missing required prerequisites: " + string.Join(", ", requiredFailures));
        }

        if (optionalWarnings.Count > 0)
        {
            Log.Warning("Doctor warnings (optional tools missing): " + string.Join(", ", optionalWarnings));
        }

        Log.Information("Doctor completed: required prerequisites are available.");
    }

    void RunLintCore(string configuration)
    {
        var normalizedConfiguration = ResolveBuildConfiguration(configuration);
        ValidateJsonConfigsCore();

        var buildProject = Path.Combine(RootDirectory, "build", "Build.csproj");
        if (!File.Exists(buildProject))
        {
            Fail($"Missing build project: {buildProject}");
        }

        RunChecked(
            "dotnet",
            $"build {Quote(buildProject)} --configuration {Quote(normalizedConfiguration)} --nologo --verbosity {ResolveDotNetCliVerbosity()} -warnaserror",
            "Lint compile check (build orchestration)");

        var modProject = Path.Combine(RootDirectory, "Fahrenheit.Mods.Parry.csproj");
        RunChecked(
            "dotnet",
            $"build {Quote(modProject)} --configuration {Quote(normalizedConfiguration)} --nologo --verbosity {ResolveDotNetCliVerbosity()}",
            "Lint compile check (mod project)");

        var testsProject = Path.Combine(RootDirectory, "tests", "Parry.Tests", "Parry.Tests.csproj");
        if (File.Exists(testsProject))
        {
            RunChecked(
                "dotnet",
                $"build {Quote(testsProject)} --configuration {Quote(normalizedConfiguration)} --nologo --verbosity {ResolveDotNetCliVerbosity()}",
                "Lint compile check (tests)");
        }

        ValidateCommitMessageString("feat: lint selftest");
        Log.Information("Lint checks passed.");
    }

    void RunFormatFixCore()
    {
        var modProject = Path.Combine(RootDirectory, "Fahrenheit.Mods.Parry.csproj");
        if (!File.Exists(modProject))
        {
            Fail($"Missing mod project: {modProject}");
        }

        RunChecked(
            "dotnet",
            $"format {Quote(modProject)} --no-restore --verbosity {ResolveDotNetCliVerbosity()}",
            "Code style auto-fix (dotnet format)");
    }

    void ValidateJsonConfigsCore()
    {
        ValidateModProjectMetadata();
        ValidateLanguageJson();
        Log.Information("JSON configuration checks passed.");
    }

    void ValidateGeneratedManifest(string path)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var (field, expected) in ReadModManifestMetadata())
            if (!manifest.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() != expected)
                Fail($"Generated manifest field '{field}' does not match the mod project.");
    }

    void ValidateModProjectMetadata()
    {
        var metadata = ReadModManifestMetadata();
        if (!Regex.IsMatch(metadata["Version"], @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z\.-]+)?$"))
            Fail($"Project Version '{metadata["Version"]}' is not SemVer-like.");
    }

    Dictionary<string, string> ReadModManifestMetadata()
    {
        var project = System.Xml.Linq.XDocument.Load(RootDirectory / "Fahrenheit.Mods.Parry.csproj");
        var properties = new Dictionary<string, string>
        {
            ["Id"] = "AssemblyName", ["Name"] = "AssemblyTitle", ["Desc"] = "Description",
            ["Authors"] = "Authors", ["Version"] = "Version", ["Link"] = "RepositoryUrl", ["Flags"] = "FhManifestFlags"
        };
        var result = new Dictionary<string, string>();
        foreach (var (field, property) in properties)
        {
            var value = project.Descendants(property).SingleOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(value)) Fail($"Mod project property '{property}' must not be empty.");
            result[field] = value!;
        }
        return result;
    }

    // Fahrenheit loads localization from lang/{module type name}/{lang id}.json, one
    // directory per module type, so the validator walks every directory under lang/ and
    // treats each one as its own set with en-US.json as the baseline.
    void ValidateLanguageJson()
    {
        var langDir = Path.Combine(RootDirectory, "lang");
        if (!Directory.Exists(langDir))
        {
            Fail($"Missing lang directory: {langDir}");
        }

        var moduleDirs = Directory.GetDirectories(langDir);
        if (moduleDirs.Length == 0)
        {
            Fail($"Language validation failed: no module directories under {langDir}.");
        }

        var requiredFiles = new[]
        {
            "en-US.json",
            "de-DE.json"
        };

        foreach (var moduleDir in moduleDirs)
        {
            var moduleName = Path.GetFileName(moduleDir);
            foreach (var file in requiredFiles)
            {
                var path = Path.Combine(moduleDir, file);
                if (!File.Exists(path))
                {
                    Fail($"Missing required language file: {path}");
                }
            }

            var baselinePath = Path.Combine(moduleDir, "en-US.json");
            var baseline = ReadStringMap(baselinePath, allowEmptyValues: false);
            if (baseline.Count == 0)
            {
                Fail($"Language validation failed: lang/{moduleName}/en-US.json must contain at least one entry.");
            }

            var langFiles = Directory.GetFiles(moduleDir, "*.json", SearchOption.TopDirectoryOnly);
            foreach (var file in langFiles)
            {
                var map = ReadStringMap(file, allowEmptyValues: false);
                var missingKeys = baseline.Keys.Where(k => !map.ContainsKey(k)).ToList();
                if (missingKeys.Count > 0)
                {
                    Fail($"Language validation failed: {moduleName}/{Path.GetFileName(file)} is missing keys: {string.Join(", ", missingKeys.Take(10))}{(missingKeys.Count > 10 ? " ..." : string.Empty)}");
                }

                ValidateLanguageGlyphRange(file, map);
            }
        }
    }

    // Fahrenheit builds its ImGui font with io.Fonts.GetGlyphRangesDefault(), which
    // covers Basic Latin plus Latin-1 Supplement and nothing else. Umlauts and the
    // sharp s are inside that range and render; an em dash, a curly quote or an
    // ellipsis character is outside it and draws as a missing glyph in the settings
    // panel, where nobody sees it until a screenshot arrives. The fix is always to
    // pick the ASCII punctuation, never to strip a letter of its diacritic.
    static void ValidateLanguageGlyphRange(string path, Dictionary<string, string> map)
    {
        foreach (var (key, value) in map)
        {
            foreach (var c in value)
            {
                if (c <= 'ÿ') continue;

                Fail($"Language validation failed: key '{key}' in {Path.GetFileName(path)} contains "
                   + $"U+{(int)c:X4} ('{c}'), which the ImGui font cannot render. Only U+0000-U+00FF "
                   + "is available; use ASCII punctuation instead.");
            }
        }
    }

    static Dictionary<string, string> ReadStringMap(string path, bool allowEmptyValues)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            Fail($"Language validation failed: {Path.GetFileName(path)} must contain a JSON object.");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                Fail($"Language validation failed: key '{prop.Name}' in {Path.GetFileName(path)} must map to a string.");
            }

            var value = prop.Value.GetString() ?? string.Empty;
            if (!allowEmptyValues && string.IsNullOrWhiteSpace(value))
            {
                Fail($"Language validation failed: key '{prop.Name}' in {Path.GetFileName(path)} must not be empty.");
            }

            map[prop.Name] = value;
        }

        return map;
    }

    void RunSmokeCore(string configuration)
    {
        var normalizedConfig = ResolveBuildConfiguration(configuration);
        var deployConfig = normalizedConfig.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "rel" : "dbg";
        var effectiveFahrenheitRef = ResolveFahrenheitRef(useReleaseRef: false);

        RunBuildProjTarget("Build", normalizedConfig, includeNativeMsbuild: true, fahrenheitRef: effectiveFahrenheitRef);

        var localOutput = Path.Combine(RootDirectory, "bin", normalizedConfig, "net10.0", "win-x86");
        AssertFilesExist(
            localOutput,
            "fhparry.dll",
            Path.Combine("resources", "mappings", "ffx-mappings.us.json"));

        var deployModOutput = Path.Combine(ResolvePath(FahrenheitDir), "artifacts", "deploy", deployConfig, "mods", ModId);
        AssertFilesExist(
            deployModOutput,
            "fhparry.dll",
            "fhparry.manifest.json",
            Path.Combine("resources", "mappings", "ffx-mappings.us.json"));

        foreach (var root in new[] { localOutput, deployModOutput })
        {
            foreach (var locale in new[] { "us", "de", "fr", "it", "sp", "jp", "ch", "kr" })
                AssertFilesExist(root, Path.Combine("resources", "mappings", $"ffx-mappings.{locale}.json"));
            if (Directory.Exists(Path.Combine(root, "mappings", "runtime")) ||
                File.Exists(Path.Combine(root, "resources", "mappings", "ffx-mappings.json")))
                Fail("Smoke check failed: obsolete mapping payload remains. Run build.cmd clean and rebuild.");
        }

        ValidateGeneratedManifest(Path.Combine(deployModOutput, "fhparry.manifest.json"));

        var stage0 = Path.Combine(ResolvePath(FahrenheitDir), "artifacts", "deploy", deployConfig, "bin", "fhstage0.exe");
        if (!File.Exists(stage0))
        {
            Fail($"Smoke check failed. Missing stage0 loader: {stage0}");
        }

        RunBuildCliSmokeCore();
        Log.Information($"Smoke checks passed for full payload, config={normalizedConfig}.");
    }

    void RunBuildCliSmokeCore()
    {
        var checks = new[]
        {
            new BuildCliSmokeCheck(
                Command: @".\build.cmd --help",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Help"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd -h deploy",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Help --workflow deploy"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd deploy -h",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Help --workflow deploy"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd build --no-auto-deploy --configuration Debug",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Cli --workflow build --no-auto-deploy --configuration Debug"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd --target Help --workflow build --dry-run",
                MustContain: ["[NUKE] dotnet run --project build\\Build.csproj -- --target Help --workflow build --dry-run"],
                MustNotContain: []),
            new BuildCliSmokeCheck(
                Command: @".\build.cmd --target Help --workflow deploy --game-dir ""C:\Program Files\Square Enix\Final Fantasy X-X2 - HD Remaster""",
                MustContain: ["--target Help --workflow deploy --game-dir"],
                MustNotContain: [])
        };

        foreach (var check in checks)
        {
            Log.Information($"[CLI-SMOKE] {check.Command}");
            var cmdArgs = $"/c set BUILD_CMD_SMOKE_ONLY=1&& set BUILD_CMD_ALLOW_NESTED=1&& {check.Command}";
            var result = RunProcess(
                "cmd",
                cmdArgs,
                $"CLI smoke: {check.Command}",
                showSpinner: false,
                silent: true);
            var output = result.StdOut + result.StdErr;

            if (result.ExitCode != 0)
            {
                Fail($"CLI smoke command failed ({result.ExitCode}): {check.Command}{Environment.NewLine}{output}");
            }

            foreach (var needle in check.MustContain)
            {
                if (!output.Contains(needle, StringComparison.Ordinal))
                {
                    Fail($"CLI smoke expected output to contain '{needle}' for command: {check.Command}{Environment.NewLine}{output}");
                }
            }

            foreach (var needle in check.MustNotContain)
            {
                if (output.Contains(needle, StringComparison.Ordinal))
                {
                    Fail($"CLI smoke expected output to not contain '{needle}' for command: {check.Command}{Environment.NewLine}{output}");
                }
            }
        }

        Log.Information("[CLI-SMOKE] All CLI checks passed.");
    }

    readonly record struct BuildCliSmokeCheck(string Command, string[] MustContain, string[] MustNotContain);

    static void AssertFilesExist(string rootPath, params string[] relativeFiles)
    {
        if (!Directory.Exists(rootPath))
        {
            Fail($"Missing expected directory: {rootPath}");
        }

        foreach (var relative in relativeFiles)
        {
            var path = Path.Combine(rootPath, relative);
            if (!File.Exists(path))
            {
                Fail($"Missing expected file: {path}");
            }
        }
    }

    static void LogDoctorCheck(string name, bool ok, bool required, string detail)
    {
        var requiredLabel = required ? "required" : "optional";
        if (ok)
        {
            Log.Information($"[OK] {name} ({requiredLabel}) - {detail}");
        }
        else
        {
            Log.Warning($"[MISSING] {name} ({requiredLabel}) - {detail}");
        }
    }
}
