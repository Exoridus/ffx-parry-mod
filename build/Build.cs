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

internal sealed partial class BuildScript : NukeBuild
{
    [Parameter(Name = "config-path")] readonly string ConfigPath = string.Empty;
    [Parameter(Name = "configuration")] readonly string ConfigurationOverride = string.Empty;
    [Parameter(Name = "fahrenheit-repo")] readonly string FahrenheitRepo = "https://github.com/fahrenheit-crew/fahrenheit.git";
    [Parameter(Name = "fahrenheit-dir")] readonly string FahrenheitDir = ".workspace/fahrenheit";
    [Parameter(Name = "fahrenheit-ref")] readonly string FahrenheitRef = string.Empty;
    [Parameter(Name = "native-msbuild-exe")] readonly string NativeMSBuildExe = string.Empty;
    [Parameter(Name = "toolset")] readonly string Toolset = string.Empty;
    [Parameter(Name = "mod-id")] readonly string ModId = "fhparry";

    [Parameter(Name = "game-dir")] readonly string GameDir = string.Empty;
    [Parameter(Name = "repo")] readonly string Repo = string.Empty;
    [Parameter(Name = "bump")] readonly string Bump = "patch";
    [Parameter(Name = "workflow")] readonly string Workflow = string.Empty;

    [Parameter(Name = "full")] readonly bool Full;
    [Parameter(Name = "log-verbosity")] readonly string LogVerbosity = string.Empty;
    [Parameter(Name = "purge")] readonly bool Purge;
    [Parameter(Name = "yes")] readonly bool Yes;
    [Parameter(Name = "dry-run")] readonly bool DryRun;
    [Parameter(Name = "non-interactive")] readonly bool NonInteractive;
    [Parameter(Name = "elevated")] readonly bool Elevated;
    [Parameter(Name = "auto-deploy")] readonly bool? AutoDeployOverride;
    [Parameter(Name = "refresh-game-dir")] readonly bool RefreshGameDir;

    [Parameter(Name = "type")] readonly string Type = "chore";
    [Parameter(Name = "scope")] readonly string Scope = string.Empty;
    [Parameter(Name = "subject")] readonly string Subject = string.Empty;
    [Parameter(Name = "breaking")] readonly bool Breaking;

    [Parameter(Name = "range")] readonly string Range = string.Empty;
    [Parameter(Name = "commit-file")] readonly string CommitFile = string.Empty;
    [Parameter(Name = "message")] readonly string Message = string.Empty;

    [Parameter(Name = "tag")] readonly string Tag = string.Empty;
    [Parameter(Name = "out")] readonly string Out = ".release/release-notes.txt";
    [Parameter(Name = "out-dir")] readonly string OutDir = ".release";

    public static int Main() => Execute<BuildScript>(x => x.Help);
    bool _isCapturingWorkflowHelp;
    WorkflowHelpBlock? _capturedWorkflowHelp;

    new Target Help => _ => _
        .Executes(() =>
        {
            ValidateVerbosityFlags();
            if (string.IsNullOrWhiteSpace(Workflow))
            {
                ShowHelpSummary();
            }
            else
            {
                ShowHelpWorkflow(Workflow);
            }
        });

    Target Cli => _ => _
        .Executes(RunCliWorkflow);

    Target AutoDeploy => _ => _
        .Executes(SetupAutoDeployCore);

    Target DocsSync => _ => _
        .Executes(SyncAutomationDocsCore);

    Target Setup => _ => _.Executes(ExecuteSetupWorkflow);

    Target Clean => _ => _.Executes(ExecuteCleanWorkflow);

    Target Verify => _ => _.Executes(ExecuteVerifyWorkflow);

    Target Build => _ => _.Executes(ExecuteBuildWorkflow);

    Target Deploy => _ => _.Executes(ExecuteDeployWorkflow);
    Target Start => _ => _.Executes(ExecuteStartWorkflow);

    Target ReleaseNotes => _ => _.Executes(() =>
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        var repoSlug = ResolveRepositorySlug(Repo);
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            Fail("Missing --repo owner/repo.");
        }

        GenerateReleaseNotesCore(
            tag: Tag,
            repositorySlug: repoSlug,
            outputPath: ResolvePath(Out));
    });

    Target ReleasePack => _ => _.Executes(() =>
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        PackageReleaseCore(Tag, ResolvePath(OutDir));
    });

    Target ReleaseReady => _ => _.Executes(ReleaseReadyCore);

    Target ReleaseBump => _ => _.Executes(ReleaseVersionCore);

    Target Commit => _ => _.Executes(() =>
    {
        var requestedMessage = Subject;
        var requestedType = Type;
        var requestedScope = Scope;
        var requestedBreaking = Breaking;

        if (string.IsNullOrWhiteSpace(requestedMessage))
        {
            if (!InteractiveSession)
            {
                Fail("Missing --subject. In interactive mode, you can run build.cmd commit without arguments.");
            }

            var wizard = RunCommitWizard();
            if (!wizard.Confirmed)
            {
                Fail("Commit canceled.");
            }

            requestedType = wizard.Type;
            requestedScope = wizard.Scope;
            requestedMessage = wizard.Message;
            requestedBreaking = wizard.Breaking;
        }

        var subject = BuildCommitSubject(requestedType, requestedScope, requestedMessage, requestedBreaking);
        if (!IsValidConventionalCommit(subject))
        {
            Fail($"Invalid Conventional Commit subject: {subject}");
        }

        RunChecked("git", $"commit -m {Quote(subject)}", "Create commit");
    });

    Target CommitCheck => _ => _.Executes(ExecuteCommitCheckWorkflow);

    void RunCliWorkflow()
    {
        ValidateVerbosityFlags();

        var workflow = (Workflow ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(workflow))
        {
            ShowHelpSummary();
            return;
        }

        if (workflow.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ShowHelpSummary();
            return;
        }

        if (workflow.Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Workflow 'install' was removed. Redirecting to 'build.cmd setup'.");
            ExecuteSetupWorkflow();
            return;
        }

        var definition = TryGetBuildWorkflow(workflow);
        if (definition is not null)
        {
            definition.Execute();
            return;
        }

        Fail($"Unknown workflow '{workflow}'. Use: build.cmd help");
    }
    void ShowHelpSummary()
    {
        WriteHelpLine("Usage: build.cmd <workflow> [options]");
        WriteHelpLine("Detailed help: build.cmd -h <workflow>");
        WriteHelpLine("Bool options: --flag (true), --no-flag (false)");
        WriteHelpLine("Global verbosity: --verbosity|-v quiet|minimal|normal|detailed|diagnostic (default: normal)");
        WriteHelpLine("Recommended escalation: quiet -> normal -> detailed -> diagnostic");
        WriteHelpLine("Global config path: --config-path (shorthand: -c)");
        WriteHelpLine("Common shorthand: -c <config-path>, -n (--dry-run), -h (help)");
        WriteHelpLine("Agent guidance: use --verbosity quiet for routine verify/build/deploy runs.");

        foreach (var section in BuildWorkflowSections)
        {
            WriteHelpLine(string.Empty);
            WriteHelpLine($"{section.Heading}:");
            foreach (var workflow in section.Workflows)
            {
                WriteHelpLine($"  {workflow.Name,-14} {workflow.Summary}");
            }
        }

        WriteHelpLine(string.Empty);
        WriteHelpLine("  Use: build.cmd -h <workflow> for detailed parameters and examples.");
    }
    void ShowHelpWorkflow(string workflowRaw)
    {
        var workflow = (workflowRaw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(workflow))
        {
            ShowHelpSummary();
            return;
        }

        if (TryGetBuildWorkflow(workflow) is { } definition)
        {
            PrintHelpBlock(
                definition.Name,
                definition.Purpose,
                definition.Parameters,
                definition.Examples);
            return;
        }

        if (workflow.Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Workflow 'install' was removed. Use: build.cmd setup");
            if (TryGetBuildWorkflow("setup") is { } setupDefinition)
            {
                PrintHelpBlock(
                    setupDefinition.Name,
                    setupDefinition.Purpose,
                    setupDefinition.Parameters,
                    setupDefinition.Examples);
                return;
            }
        }

        Log.Warning($"Unknown workflow: {workflowRaw}");
        ShowHelpSummary();
    }
    WorkflowHelpBlock CaptureWorkflowHelpBlock(string workflowRaw)
    {
        var normalizedWorkflow = (workflowRaw ?? string.Empty).Trim().ToLowerInvariant();
        _isCapturingWorkflowHelp = true;
        _capturedWorkflowHelp = null;
        try
        {
            ShowHelpWorkflow(normalizedWorkflow);
            return _capturedWorkflowHelp ?? new WorkflowHelpBlock(
                normalizedWorkflow,
                "Unknown workflow.",
                [],
                []);
        }
        finally
        {
            _isCapturingWorkflowHelp = false;
            _capturedWorkflowHelp = null;
        }
    }

    void PrintHelpBlock(string name, string purpose, IEnumerable<string> parameters, IEnumerable<string> examples)
    {
        var parameterList = parameters?.ToList() ?? [];
        var exampleList = examples?.ToList() ?? [];

        _capturedWorkflowHelp = new WorkflowHelpBlock(
            Workflow: name,
            Purpose: purpose,
            Parameters: parameterList,
            Examples: exampleList);

        if (_isCapturingWorkflowHelp)
        {
            return;
        }

        WriteHelpLine($"Workflow: {name}");
        WriteHelpLine($"Purpose: {purpose}");
        WriteHelpLine("Global options:");
        WriteHelpLine("  - --verbosity|-v quiet|minimal|normal|detailed|diagnostic (default: normal)");
        WriteHelpLine("  - --config-path <path> (shorthand: -c)");
        WriteHelpLine("  - -n (--dry-run), -h (help)");
        WriteHelpLine("Parameters:");
        foreach (var line in parameterList)
        {
            WriteHelpLine($"  - {line}");
        }

        WriteHelpLine("Examples:");
        foreach (var line in exampleList)
        {
            WriteHelpLine($"  {line}");
        }
    }
    sealed record WorkflowDefinition(
        string Name,
        string Section,
        string Summary,
        string Purpose,
        IReadOnlyList<string> Parameters,
        IReadOnlyList<string> Examples,
        Action Execute);

    static readonly string[] BuildSectionOrder = ["Core", "Distribution", "Release", "Commit", "Utility"];

    IReadOnlyList<WorkflowDefinition>? _buildWorkflowDefinitions;
    IReadOnlyDictionary<string, WorkflowDefinition>? _buildWorkflowLookup;

    IReadOnlyList<WorkflowDefinition> BuildWorkflowDefinitions =>
        _buildWorkflowDefinitions ??= CreateBuildWorkflowDefinitions();

    IReadOnlyDictionary<string, WorkflowDefinition> BuildWorkflowLookup =>
        _buildWorkflowLookup ??= BuildWorkflowDefinitions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    IReadOnlyList<(string Heading, IReadOnlyList<WorkflowDefinition> Workflows)> BuildWorkflowSections =>
        BuildSectionOrder
            .Select(section => (Heading: section, Workflows: (IReadOnlyList<WorkflowDefinition>)BuildWorkflowDefinitions.Where(x => x.Section.Equals(section, StringComparison.Ordinal)).ToList()))
            .Where(x => x.Workflows.Count > 0)
            .ToList();

    bool IsBuildWorkflow(string workflow) => BuildWorkflowLookup.ContainsKey(workflow);
    WorkflowDefinition? TryGetBuildWorkflow(string workflow) => BuildWorkflowLookup.TryGetValue(workflow, out var definition) ? definition : null;

    IReadOnlyList<WorkflowDefinition> CreateBuildWorkflowDefinitions()
    {
        return
        [
            new("setup", "Core", "Primary local onboarding/setup", "Primary local onboarding: prerequisites, config/GameDir repair, hooks, workspace setup, and auto-deploy setup.", [], ["build.cmd setup"], ExecuteSetupWorkflow),
            new("clean", "Core", "Remove local caches/artifacts (safe default)", "Remove generated mod and host build artifacts. Add --purge --yes to also remove local release output.", ["--purge (optional, requires --yes).", "--yes (required with --purge).", "--dry-run (optional)."], ["build.cmd clean", "build.cmd clean --dry-run", "build.cmd clean --purge --yes"], ExecuteCleanWorkflow),
            new("auto-deploy", "Core", "Configure automatic post-build deploy", "Configure automatic post-build deployment.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional)."], ["build.cmd auto-deploy", "build.cmd auto-deploy --game-dir \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\FINAL FANTASY FFX&FFX-2 HD Remaster\""], SetupAutoDeployCore),
            new("doctor", "Core", "Diagnose local toolchain/environment state", "Diagnose local toolchain and environment state.", ["--full (optional)."], ["build.cmd doctor", "build.cmd doctor --full"], RunDoctorCore),
            new("format", "Core", "Auto-fix code formatting/style", "Apply code formatting/style fixes using dotnet format.", [], ["build.cmd format"], RunFormatFixCore),
            new("lint", "Core", "Run fast lint/compile checks", "Run fast lint/compile checks for build, mod, and tests projects.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c)."], ["build.cmd lint"], () => RunLintCore(RequestedConfiguration)),
            new("smoke", "Core", "Run quick end-to-end sanity checks", "Run quick sanity checks against a full build.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c)."], ["build.cmd smoke"], () => RunSmokeCore(RequestedConfiguration)),
            new("verify", "Core", "Build full payload (config Configuration by default) + run tests", "Run local validation without deploy side effects.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c).", "--repo owner/repo (optional)."], ["build.cmd verify"], ExecuteVerifyWorkflow),
            new("build", "Core", "Build full payload", "Build full Fahrenheit payload.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c).", "--auto-deploy or --no-auto-deploy (optional).", "--dry-run (optional)."], ["build.cmd build", "build.cmd build --configuration Release"], ExecuteBuildWorkflow),
            new("deploy", "Core", "Deploy artifacts to game directory", "Deploy full build artifacts into GameDir.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional).", "--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c).", "--dry-run (optional)."], ["build.cmd deploy"], ExecuteDeployWorkflow),
            new("start", "Core", "Launch fhstage0.exe", "Launch the game via deployed Fahrenheit stage0 loader.", ["--game-dir <path> (optional).", "--refresh-game-dir (optional).", "--elevated (optional)."], ["build.cmd start"], ExecuteStartWorkflow),
            new("mod-build", "Distribution", "Build and validate the mod payload", "Build only the mod with isolated SDK feeds; never deploy.", ["--sdk local|official", "--configuration Debug|Release"], ["build.cmd mod-build"], () => RunModDistribution("mod-build")),
            new("mod-verify", "Distribution", "Build the mod and run tests", "Validate the SDK payload and run runtime/package tests.", ["--sdk local|official", "--configuration Debug|Release"], ["build.cmd mod-verify"], () => RunModDistribution("mod-verify")),
            new("mod-pack", "Distribution", "Build, test and package installable mods", "Create a mod ZIP rooted at the mod ID, SHA256 and provenance; --full also builds/packages the matching host.", ["--sdk local|official", "--tag vX.Y.Z[-suffix]", "--out-dir path", "--full"], ["build.cmd mod-pack --full"], () => RunModDistribution("mod-pack")),
            new("release-bump", "Release", "Bump version + changelog (no tag)", "Bump version and create the release commit. The tag is created by the Release workflow after a green build.", ["--bump patch|minor|major (optional)."], ["build.cmd release-bump"], ReleaseVersionCore),
            new("release-ready", "Release", "Preflight checks/build/package/notes", "Run release preflight.", ["--configuration Debug|Release (optional).", "--config-path <path-to-config.local.json> (optional, shorthand: -c).", "--repo owner/repo (optional)."], ["build.cmd release-ready"], ReleaseReadyCore),
            new("release-pack", "Release", "Create release ZIP assets", "Build, test and package Release mod/full archives using the shared distribution workflow.", ["--tag vX.Y.Z (required).", "--sdk local|official (optional).", "--out-dir <path> (optional)."], ["build.cmd release-pack --tag v0.0.1"], ExecuteReleasePackWorkflow),
            new("release-check-assets", "Release", "Validate release archives and checksum presence", "Check the packaged ZIPs against MAX_FULL_ZIP_MB (default 200) and MAX_MOD_ZIP_MB (default 64).", ["--tag vX.Y.Z (required).", "--out-dir <path> (optional).", "--mod-id <id> (optional)."], ["build.cmd release-check-assets --tag v0.0.1"], AssertReleaseAssetsCore),
            new("release-notes", "Release", "Generate release notes markdown", "Generate release-notes markdown/text for a tag.", ["--tag vX.Y.Z (required).", "--repo owner/repo (required).", "--out <path> (optional)."], ["build.cmd release-notes --tag v0.0.1 --repo owner/repo"], ExecuteReleaseNotesWorkflow),
            new("commit", "Commit", "Interactive/non-interactive Conventional Commit", "Create a Conventional Commit.", ["--type feat|fix|... (optional).", "--scope <scope> (optional).", "--subject \"message\" (required in non-interactive mode).", "--breaking (optional)."], ["build.cmd commit"], ExecuteCommitWorkflow),
            new("commit-check", "Commit", "Validate message/file/range commit input", "Validate a commit message string, a commit message file, or a git commit range.", ["--message \"...\" (optional).", "--commit-file <path> (optional).", "--range <BASE..HEAD> (optional)."], ["build.cmd commit-check --message \"feat: x\"", "build.cmd commit-check --range origin/main..HEAD"], ExecuteCommitCheckWorkflow),
            new("docs-sync", "Utility", "Regenerate build automation docs", "Regenerate docs/automation.md from workflow metadata.", [], ["build.cmd docs-sync"], SyncAutomationDocsCore)
        ];
    }

    void ExecuteSetupWorkflow()
    {
        EnsureLocalBuildPrerequisitesForSetup();
        EnsureGameDirConfiguredForSetup();
        var resolvedConfiguration = ResolveBuildConfiguration(RequestedConfiguration);
        SetupHooksCore();
        bool ranWorkspaceSetup = EnsureProjectWorkspaceSetup(resolvedConfiguration);
        SetupAutoDeployCore();
        if (ranWorkspaceSetup && InteractiveSession && AskYesNo("Run first full build now? (Recommended)", defaultYes: true))
        {
            RunBuildProjTarget("Build", resolvedConfiguration, includeNativeMsbuild: true, fahrenheitRef: ResolveFahrenheitRef(useReleaseRef: false));
        }
    }

    void ExecuteCleanWorkflow() => RunCleanCore();

    void ExecuteBuildWorkflow() => BuildCore("full", RequestedConfiguration, useReleaseRef: false);

    void ExecuteVerifyWorkflow()
    {
        if (!IsValidConventionalCommit("feat: selftest commit format") || IsValidConventionalCommit("invalid message"))
        {
            Fail("Commit validator selftest failed.");
        }

        RunVerifyCore(RequestedConfiguration);
    }

    void ExecuteDeployWorkflow() => DeployCore("full", RequestedConfiguration);

    void ExecuteStartWorkflow() => StartCore();

    void ExecuteReleasePackWorkflow()
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        PackageReleaseCore(Tag, ResolvePath(OutDir));
    }

    void ExecuteReleaseNotesWorkflow()
    {
        if (string.IsNullOrWhiteSpace(Tag))
        {
            Fail("Missing --tag.");
        }

        var repoSlug = ResolveRepositorySlug(Repo);
        if (string.IsNullOrWhiteSpace(repoSlug))
        {
            Fail("Missing --repo owner/repo.");
        }

        GenerateReleaseNotesCore(
            tag: Tag,
            repositorySlug: repoSlug,
            outputPath: ResolvePath(Out));
    }

    void ExecuteCommitWorkflow()
    {
        var requestedMessage = Subject;
        var requestedType = Type;
        var requestedScope = Scope;
        var requestedBreaking = Breaking;
        if (string.IsNullOrWhiteSpace(requestedMessage))
        {
            if (!InteractiveSession)
            {
                Fail("Missing --subject. In interactive mode, you can run build.cmd commit without arguments.");
            }
            var wizard = RunCommitWizard();
            if (!wizard.Confirmed)
            {
                Fail("Commit canceled.");
            }
            requestedType = wizard.Type;
            requestedScope = wizard.Scope;
            requestedMessage = wizard.Message;
            requestedBreaking = wizard.Breaking;
        }
        var subject = BuildCommitSubject(requestedType, requestedScope, requestedMessage, requestedBreaking);
        if (!IsValidConventionalCommit(subject))
        {
            Fail($"Invalid Conventional Commit subject: {subject}");
        }
        RunChecked("git", $"commit -m {Quote(subject)}", "Create commit");
    }

    void ExecuteCommitCheckWorkflow()
    {
        if (!string.IsNullOrWhiteSpace(Range))
        {
            ValidateCommitRangeCore(Range);
            return;
        }

        if (!string.IsNullOrWhiteSpace(CommitFile))
        {
            ValidateCommitMessageFromFile(ResolvePath(CommitFile));
            return;
        }
        if (string.IsNullOrWhiteSpace(Message))
        {
            Fail("Missing --message, --commit-file, or --range.");
        }
        ValidateCommitMessageString(Message);
    }
}

