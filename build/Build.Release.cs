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
    bool IsValidConventionalCommit(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;

        var trimmed = subject.Trim();
        if (trimmed.StartsWith("Merge ", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("Revert ", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("fixup!", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("squash!", StringComparison.OrdinalIgnoreCase)) return true;

        var idx = trimmed.IndexOf(':');
        if (idx <= 0 || idx + 1 >= trimmed.Length) return false;

        var head = trimmed[..idx];
        var body = trimmed[(idx + 1)..].Trim();
        if (body.Length == 0) return false;

        var typeEnd = head.IndexOf('(');
        var type = typeEnd >= 0 ? head[..typeEnd] : head.TrimEnd('!');
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "build", "chore", "ci", "docs", "feat", "fix", "perf", "refactor", "revert", "style", "test"
        };

        return allowed.Contains(type.Trim('!', ' '));
    }

    string BuildCommitSubject(string type, string scope, string message, bool breaking)
    {
        var sb = new StringBuilder(type.Trim());
        if (!string.IsNullOrWhiteSpace(scope)) sb.Append('(').Append(scope.Trim()).Append(')');
        if (breaking) sb.Append('!');
        sb.Append(": ").Append(message.Trim());
        return sb.ToString();
    }

    CommitWizardResult RunCommitWizard()
    {
        var types = new[]
        {
            "feat", "fix", "docs", "chore", "ci", "build", "refactor", "perf", "test", "style", "revert"
        };

        Log.Information("Interactive Conventional Commit wizard");
        Log.Information("Select commit type:");
        for (var i = 0; i < types.Length; i++)
        {
            var suffix = i == 0 ? " (default)" : string.Empty;
            Log.Information($"  {i + 1}) {types[i]}{suffix}");
        }

        Console.Write("Type [1-11] (default 1): ");
        var typeInput = (Console.ReadLine() ?? string.Empty).Trim();
        var typeIndex = int.TryParse(typeInput, out var idx) ? idx - 1 : 0;
        if (typeIndex < 0 || typeIndex >= types.Length)
        {
            typeIndex = 0;
        }

        var selectedType = types[typeIndex];

        Console.Write("Scope (optional, empty for none): ");
        var scope = (Console.ReadLine() ?? string.Empty).Trim();

        string message;
        while (true)
        {
            Console.Write("Message (required): ");
            message = (Console.ReadLine() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(message))
            {
                break;
            }

            Log.Warning("Message cannot be empty.");
        }

        var breaking = AskYesNo("Breaking change?", defaultYes: false);
        var subject = BuildCommitSubject(selectedType, scope, message, breaking);
        if (!IsValidConventionalCommit(subject))
        {
            Fail($"Invalid commit subject generated: {subject}");
        }

        Log.Information($"Preview: {subject}");
        var confirmed = AskYesNo("Create this commit?", defaultYes: true);

        return new CommitWizardResult(selectedType, scope, message, breaking, confirmed);
    }

    string ResolveFahrenheitRef(bool useReleaseRef)
    {
        if (!string.IsNullOrWhiteSpace(FahrenheitRef))
        {
            return FahrenheitRef.Trim();
        }

        if (useReleaseRef)
        {
            if (!File.Exists(ReleaseFahrenheitRefPath))
            {
                Fail($"Missing release ref file: {ReleaseFahrenheitRefPath}. Run build.cmd release-bump first.");
            }

            var pinned = File.ReadAllText(ReleaseFahrenheitRefPath).Trim();
            if (string.IsNullOrWhiteSpace(pinned))
            {
                Fail($"Release ref file is empty: {ReleaseFahrenheitRefPath}");
            }

            return pinned;
        }

        // Default builds pin to the committed ref (fahrenheit.release.ref) so a plain
        // `build.cmd build` is deterministic and does NOT silently drift to origin/main
        // (currently the in-flight alpha11 line). Explicit --fahrenheit-ref still overrides;
        // origin/main remains the fallback only if the pin file is missing/empty.
        if (File.Exists(ReleaseFahrenheitRefPath))
        {
            var committed = File.ReadAllText(ReleaseFahrenheitRefPath).Trim();
            if (!string.IsNullOrWhiteSpace(committed))
            {
                return committed;
            }
        }

        return "origin/main";
    }

    void ReleaseVersionCore()
    {
        RequireGitRepository();
        EnsureCleanWorkingTree();

        var bump = Bump.Trim().ToLowerInvariant();
        if (bump != "major" && bump != "minor" && bump != "patch")
        {
            Fail("Invalid bump level. Use patch, minor, or major.");
        }

        var latestTag = TryGetLatestSemverTag();
        var currentVersion = latestTag is null ? new SemVersion(0, 0, 0) : ParseSemVersion(latestTag);
        var nextVersion = bump switch
        {
            "major" => new SemVersion(currentVersion.Major + 1, 0, 0),
            "minor" => new SemVersion(currentVersion.Major, currentVersion.Minor + 1, 0),
            _ => new SemVersion(currentVersion.Major, currentVersion.Minor, currentVersion.Patch + 1)
        };

        var newTag = $"v{nextVersion.Major}.{nextVersion.Minor}.{nextVersion.Patch}";
        if (GitRefExists(newTag))
        {
            Fail($"Tag already exists: {newTag}");
        }

        var repoSlug = ResolveRepositorySlug(Repo);
        GenerateChangelogCore(tagOverride: newTag, outputPath: RootDirectory / "CHANGELOG.md", repositorySlug: repoSlug);
        UpdateProjectVersion(nextVersion, repoSlug);
        PinReleaseFahrenheitRef();

        var filesToStage = new[]
        {
            Quote(RootDirectory / "CHANGELOG.md"),
            Quote(RootDirectory / "Fahrenheit.Mods.Parry.csproj"),
            Quote(ReleaseFahrenheitRefPath)
        };
        RunChecked("git", $"add {string.Join(" ", filesToStage)}", "Stage release files");

        var commitMessage = $"chore(release): {newTag}";
        var commitResult = RunProcess("git", $"-c core.hooksPath=NUL commit -m {Quote(commitMessage)}", "Create release commit", silent: true);
        if (commitResult.ExitCode != 0)
        {
            RunChecked("git", $"-c core.hooksPath=NUL commit --allow-empty -m {Quote(commitMessage)}", "Create empty release commit");
        }

        // Deliberately no `git tag` here. The tag is created by the Release workflow
        // after a green release build, so a failed build never burns a version number.
        Log.Information($"Created release commit for {newTag}.");
        Log.Information("Next steps:");
        Log.Information("  1. git push origin main");
        Log.Information("  2. wait for CI (its release preflight packages the same payload)");
        Log.Information($"  3. gh workflow run Release -f version={newTag}");
    }

    void PinReleaseFahrenheitRef()
    {
        // Honor a deliberately committed pin: if the release ref file already holds a
        // concrete 40-char commit SHA, keep it. Only auto-resolve Fahrenheit main HEAD
        // when the pin is missing, empty, or symbolic (e.g. a branch name). Without this,
        // release-bump silently drags every release onto Fahrenheit main HEAD.
        if (File.Exists(ReleaseFahrenheitRefPath))
        {
            var existing = File.ReadAllText(ReleaseFahrenheitRefPath).Trim();
            if (existing.Length == 40 && existing.All(Uri.IsHexDigit))
            {
                Log.Information($"Keeping committed release Fahrenheit ref: {existing}");
                return;
            }
        }

        var lsRemote = RunProcess(
            "git",
            $"ls-remote {Quote(FahrenheitRepo)} refs/heads/main",
            "Resolve Fahrenheit main ref",
            silent: true);

        if (lsRemote.ExitCode != 0)
        {
            Fail($"Failed to resolve Fahrenheit main ref.{Environment.NewLine}{lsRemote.StdErr}");
        }

        var firstLine = lsRemote.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            Fail("Could not parse Fahrenheit main ref from git ls-remote output.");
        }

        var hash = firstLine!.Split('\t', ' ').FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(hash))
        {
            Fail("Could not extract Fahrenheit commit hash from ls-remote output.");
        }

        File.WriteAllText(ReleaseFahrenheitRefPath, hash + Environment.NewLine);
        Log.Information($"Pinned release Fahrenheit ref: {hash}");
    }

    void GenerateChangelogCore(string? tagOverride, string outputPath, string repositorySlug)
    {
        RequireGitRepository();

        var currentLabel = ResolveCurrentChangelogLabel(tagOverride);
        var currentRef = currentLabel == "Initial Commit"
            ? "HEAD"
            : (GitRefExists(currentLabel) ? currentLabel : "HEAD");
        var currentCommit = GitSingleLineOrFallback($"rev-parse {Quote(currentRef)}", "rev-parse HEAD", "HEAD");
        var previousTag = currentLabel == "Initial Commit" ? string.Empty : ResolvePreviousSemverTag(currentLabel);
        var repoUrl = string.IsNullOrWhiteSpace(repositorySlug) ? string.Empty : $"https://github.com/{repositorySlug}";

        string range;
        if (currentLabel == "Initial Commit")
        {
            range = "HEAD";
        }
        else if (!string.IsNullOrWhiteSpace(previousTag))
        {
            range = $"{previousTag}..{currentRef}";
        }
        else
        {
            range = currentRef;
        }

        var releaseDate = GitSingleLineOrFallback($"log -1 --date=short --format=%ad {Quote(currentRef)}", "log -1 --date=short --format=%ad", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var commits = CollectCommitLines(range, repoUrl);
        if (commits.Count == 0)
        {
            commits.Add("- Initial commit.");
        }

        var content = new StringBuilder();
        content.AppendLine("# Changelog");
        content.AppendLine();

        if (currentLabel == "Initial Commit")
        {
            if (!string.IsNullOrWhiteSpace(repoUrl))
            {
                content.AppendLine($"## [Initial Commit]({repoUrl}/tree/{currentCommit}) ({releaseDate})");
                content.AppendLine($"[Commit History]({repoUrl}/commits/{currentCommit})");
            }
            else
            {
                content.AppendLine($"## Initial Commit ({releaseDate})");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(repoUrl))
            {
                content.AppendLine($"## [{currentLabel}]({repoUrl}/releases/tag/{currentLabel}) ({releaseDate})");
                if (!string.IsNullOrWhiteSpace(previousTag))
                {
                    content.AppendLine($"[Full Changelog]({repoUrl}/compare/{previousTag}...{currentLabel}) | [Previous Releases]({repoUrl}/releases)");
                }
                else
                {
                    content.AppendLine($"[Initial Release Commits]({repoUrl}/commits/{currentCommit}) | [All Releases]({repoUrl}/releases)");
                }
            }
            else
            {
                content.AppendLine($"## {currentLabel} ({releaseDate})");
            }
        }

        content.AppendLine();
        foreach (var commit in commits)
        {
            content.AppendLine(commit);
        }

        File.WriteAllText(outputPath, content.ToString().Replace("\r\n", "\n"));
        Log.Information($"Changelog generated: {outputPath}");
    }

    void GenerateReleaseNotesCore(string tag, string repositorySlug, string outputPath)
    {
        RequireGitRepository();

        var repoUrl = $"https://github.com/{repositorySlug}";
        var previousTag = ResolvePreviousAnyTag(tag);

        // The tag does not exist as a git ref while the release build runs ahead of
        // publishing, and never exists for the CI preflight tag. CollectCommitLines
        // swallows a failed `git log`, so an unresolvable range does not error out --
        // it silently yields notes that claim "Initial release". Anchor the range on
        // HEAD whenever the tag is not (yet) a real ref.
        var rangeEnd = GitRefExists(tag) ? tag : "HEAD";
        var releaseDate = GitSingleLineOrFallback($"log -1 --date=short --format=%ad {Quote(rangeEnd)}^{{commit}}", "log -1 --date=short --format=%ad", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var range = string.IsNullOrWhiteSpace(previousTag) ? rangeEnd : $"{previousTag}..{rangeEnd}";
        var commits = CollectCommitLines(range, repoUrl);
        if (commits.Count == 0)
        {
            commits.Add("- Initial release");
        }

        var content = new StringBuilder();
        var project = repositorySlug.Split('/').LastOrDefault() ?? repositorySlug;
        var fullPackage = $"{repoUrl}/releases/download/{tag}/fahrenheit-full-{tag}.zip";
        var modPackage = $"{repoUrl}/releases/download/{tag}/{ModId}-mod-{tag}.zip";
        content.AppendLine($"# {project} {tag} ({releaseDate})");
        content.AppendLine();
        content.AppendLine("This release provides pre-built ZIP packages for Windows (FFX/X-2 HD Remaster + Fahrenheit):");
        content.AppendLine();
        content.AppendLine($"- Full package: [fahrenheit-full-{tag}.zip]({fullPackage})");
        content.AppendLine($"  - SHA256: [fahrenheit-full-{tag}.zip.sha256]({fullPackage}.sha256)");
        content.AppendLine($"- Mod-only package: [{ModId}-mod-{tag}.zip]({modPackage})");
        content.AppendLine($"  - SHA256: [{ModId}-mod-{tag}.zip.sha256]({modPackage}.sha256)");
        content.AppendLine();
        content.AppendLine("## Installation");
        content.AppendLine();
        content.AppendLine("Prerequisite: Microsoft .NET Runtime 10 (x86 recommended).");
        content.AppendLine("If missing, `fahrenheit/start-fahrenheit.cmd` will prompt to install it via winget.");
        content.AppendLine();
        content.AppendLine("1. Download one of the ZIP packages above.");
        content.AppendLine("2. Extract into your game directory (folder containing `FFX.exe`).");
        content.AppendLine("3. For the full package, start via `fahrenheit/start-fahrenheit.cmd` (recommended).");
        content.AppendLine("4. Alternatively run `fahrenheit\\bin\\fhstage0.exe ..\\..\\FFX.exe` from `fahrenheit\\bin`.");
        content.AppendLine();
        content.AppendLine("## Changes in This Release");
        content.AppendLine();
        content.AppendLine($"[View this tag]({repoUrl}/releases/tag/{tag}) | [All Releases]({repoUrl}/releases)");
        content.AppendLine();
        foreach (var commit in commits)
        {
            content.AppendLine(commit);
        }
        content.AppendLine();
        content.AppendLine("---");
        content.AppendLine();

        if (!string.IsNullOrWhiteSpace(previousTag))
        {
            content.AppendLine($"Full Changelog: {repoUrl}/compare/{previousTag}...{tag} | [README]({repoUrl}/blob/main/README.md)");
        }
        else
        {
            content.AppendLine($"Full Changelog: {repoUrl}/commits/{tag} | [README]({repoUrl}/blob/main/README.md)");
        }

        File.WriteAllText(outputPath, content.ToString().Replace("\r\n", "\n"));
        Log.Information($"Release notes generated: {outputPath}");
    }

    string ResolveRepositorySlug(string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        var remote = GitSingleLineOrFallback("remote get-url origin", "remote get-url origin", string.Empty);
        if (string.IsNullOrWhiteSpace(remote))
        {
            return string.Empty;
        }

        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            return remote["git@github.com:".Length..].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        if (remote.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return remote["https://github.com/".Length..].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return string.Empty;
    }

    string ResolveCurrentChangelogLabel(string? tagOverride)
    {
        if (!string.IsNullOrWhiteSpace(tagOverride))
        {
            return tagOverride;
        }

        return TryGetLatestSemverTag() ?? "Initial Commit";
    }

    string ResolvePreviousSemverTag(string currentTag)
    {
        var tags = GetSemverTagsDescending();
        foreach (var tag in tags)
        {
            if (!tag.Equals(currentTag, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return string.Empty;
    }

    string ResolvePreviousAnyTag(string currentTag)
    {
        var result = RunProcess("git", "tag --sort=-v:refname", "List tags", silent: true);
        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        foreach (var tag in result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = tag.Trim();
            if (!trimmed.Equals(currentTag, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    bool GitRefExists(string reference)
    {
        var result = RunProcess("git", $"rev-parse {Quote(reference)}^{{commit}}", "Check git ref", silent: true);
        return result.ExitCode == 0;
    }

    List<string> CollectCommitLines(string range, string repoUrl)
    {
        var format = string.IsNullOrWhiteSpace(repoUrl)
            ? "- %s (%h)"
            : $"- %s ([%h]({repoUrl}/commit/%H))";

        var result = RunProcess("git", $"log --pretty=format:{Quote(format)} --no-merges {Quote(range)}", "Collect commits", silent: true);
        if (result.ExitCode != 0)
        {
            return new List<string>();
        }

        return result.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    string? TryGetLatestSemverTag()
    {
        var tags = GetSemverTagsDescending();
        return tags.Count == 0 ? null : tags[0];
    }

    List<string> GetSemverTagsDescending()
    {
        var result = RunProcess("git", "tag --list v* --sort=-v:refname", "List semver tags", silent: true);
        if (result.ExitCode != 0)
        {
            return new List<string>();
        }

        var tags = new List<string>();
        foreach (var line in result.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = line.Trim();
            if (TryParseSemVersion(tag, out _))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    SemVersion ParseSemVersion(string tag)
    {
        if (!TryParseSemVersion(tag, out var version))
        {
            Fail($"Invalid semantic version tag: {tag}");
        }

        return version;
    }

    static bool TryParseSemVersion(string tag, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith("v", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = tag[1..].Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch);
        return true;
    }

    string GitSingleLineOrFallback(string primaryArgs, string fallbackArgs, string fallbackValue)
    {
        var primary = RunProcess("git", primaryArgs, "Read git value", silent: true);
        if (primary.ExitCode == 0)
        {
            var line = primary.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        var fallback = RunProcess("git", fallbackArgs, "Read fallback git value", silent: true);
        if (fallback.ExitCode == 0)
        {
            var line = fallback.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return fallbackValue;
    }

    void EnsureCleanWorkingTree()
    {
        RunChecked("git", "update-index -q --refresh", "Refresh git index", silent: true);

        if (RunProcess("git", "diff --quiet --exit-code", "Check unstaged changes", silent: true).ExitCode != 0)
        {
            Fail("Working tree has unstaged changes.");
        }

        if (RunProcess("git", "diff --cached --quiet --exit-code", "Check staged changes", silent: true).ExitCode != 0)
        {
            Fail("Working tree has staged but uncommitted changes.");
        }

        var untracked = RunProcess("git", "ls-files --others --exclude-standard", "Check untracked files", silent: true);
        if (untracked.ExitCode != 0)
        {
            Fail($"Failed to inspect untracked files.{Environment.NewLine}{untracked.StdErr}");
        }

        if (untracked.StdOut.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Length > 0)
        {
            Fail("Working tree has untracked files.");
        }
    }

    void UpdateProjectVersion(SemVersion version, string repoSlug)
    {
        var projectPath = RootDirectory / "Fahrenheit.Mods.Parry.csproj";
        if (!File.Exists(projectPath))
        {
            Fail($"Mod project not found: {projectPath}");
        }

        var text = File.ReadAllText(projectPath);
        text = Regex.Replace(text, @"<Version>[^<]*</Version>", $"<Version>{version}</Version>");
        if (!string.IsNullOrWhiteSpace(repoSlug))
        {
            text = Regex.Replace(
                text,
                @"<RepositoryUrl>[^<]*</RepositoryUrl>",
                $"<RepositoryUrl>https://github.com/{repoSlug}</RepositoryUrl>");
        }

        File.WriteAllText(projectPath, text);
    }

    void ValidateCommitMessageString(string message)
    {
        if (!IsValidConventionalCommit(message))
        {
            Fail($"Invalid commit subject: {message}");
        }
    }

    void ValidateCommitMessageFromFile(string path)
    {
        if (!File.Exists(path))
        {
            Fail($"Commit message file not found: {path}");
        }

        var firstSubject = File.ReadLines(path)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(firstSubject))
        {
            Fail("No commit subject found in commit message file.");
        }

        ValidateCommitMessageString(firstSubject!);
    }

    void AssertReleaseAssetsCore()
    {
        if (string.IsNullOrWhiteSpace(Tag)) Fail("--tag is required.");
        var root = ResolvePath(OutDir);
        int Budget(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? value : string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)) ? fallback
                : throw new FormatException($"{name} must be an integer.");
        var assets = new[]
        {
            (Path: Path.Combine(root, $"{ModId}-full-{Tag}.zip"), Limit: Budget("MAX_FULL_ZIP_MB", 200)),
            (Path: Path.Combine(root, $"{ModId}-mod-{Tag}.zip"), Limit: Budget("MAX_MOD_ZIP_MB", 64))
        };
        foreach (var asset in assets)
        {
            if (!File.Exists(asset.Path)) Fail($"Missing release asset: {asset.Path}");
            var bytes = new FileInfo(asset.Path).Length;
            var size = Math.Round(bytes / (1024d * 1024d), 2);
            if (bytes > asset.Limit * 1024L * 1024L)
                Fail($"Asset too large: {asset.Path} is {size} MiB > {asset.Limit} MiB");
            Log.Information($"OK {Path.GetFileName(asset.Path)} = {size} MiB (limit {asset.Limit} MiB)");
        }
        foreach (var asset in assets)
            if (!File.Exists(asset.Path + ".sha256")) Fail($"Missing checksum file: {asset.Path}.sha256");
        Log.Information("Release asset checks passed.");
    }

    void PackageReleaseCore(string tag, string outRoot)
        => RunModDistribution("mod-pack", tag, outRoot, full: true, configuration: "Release");

    void ValidateCommitRangeCore(string range)
    {
        RequireGitRepository();

        var result = RunProcess("git", $"log --format=%s --no-merges {Quote(range)}", "Read commit range", showSpinner: false, silent: true);
        if (result.ExitCode != 0)
        {
            Fail($"Failed to read commit range '{range}'.{Environment.NewLine}{result.StdErr}");
        }

        var invalid = result.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !IsValidConventionalCommit(x))
            .ToList();

        if (invalid.Count > 0)
        {
            Fail("Invalid commit subject(s):" + Environment.NewLine + string.Join(Environment.NewLine, invalid.Select(x => "  - " + x)));
        }

        Log.Information($"Commit messages valid for range {range}.");
    }

    void RequireGitRepository()
    {
        var result = RunProcess("git", "rev-parse --git-dir", "Check git repo", showSpinner: false, silent: true);
        if (result.ExitCode != 0)
        {
            Fail("This target must run inside a git repository.");
        }
    }

    readonly record struct SemVersion(int Major, int Minor, int Patch)
    {
        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    readonly record struct CommitWizardResult(string Type, string Scope, string Message, bool Breaking, bool Confirmed);
    void ReleaseReadyCore()
    {
        RequireGitRepository();
        EnsureCleanWorkingTree();

        var commitRange = Range;
        if (string.IsNullOrWhiteSpace(commitRange))
        {
            var latestTag = TryGetLatestSemverTag();
            commitRange = string.IsNullOrWhiteSpace(latestTag) ? "HEAD" : $"{latestTag}..HEAD";
        }

        ValidateCommitRangeCore(commitRange);
        RunModDistribution("mod-verify", configuration: "Debug");

        var preflightTag = !string.IsNullOrWhiteSpace(Tag)
            ? Tag.Trim()
            : $"v0.0.0-preflight-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var repoSlug = ResolveRepositorySlug(Repo);
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            repoSlug = "owner/repo";
        }



        var preflightOut = ResolvePath(".release/preflight");
        RecreateDir(preflightOut);
        PackageReleaseCore(preflightTag, preflightOut);
        GenerateReleaseNotesCore(preflightTag, repoSlug, Path.Combine(preflightOut, "release-notes.txt"));

        Log.Information($"Release preflight completed. Artifacts: {preflightOut}");
    }
}

