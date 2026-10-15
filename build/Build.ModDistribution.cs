using Nuke.Common.IO;

internal sealed partial class BuildScript
{
    [Nuke.Common.Parameter(Name = "sdk")] readonly string DistributionSdk = "local";

    void RunModDistribution(string command, string? tag = null, string? output = null, bool? full = null, string? configuration = null)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RootDirectory,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "run", "--project", (RootDirectory / "build/distribution/ModBuild.csproj").ToString(), "--", RootDirectory.ToString(), command,
            "--sdk", DistributionSdk, "--configuration", configuration ?? (string.IsNullOrWhiteSpace(ConfigurationOverride) ? "Release" : ConfigurationOverride),
            "--out-dir", output ?? OutDir, "--full", (full ?? Full).ToString() })
            start.ArgumentList.Add(argument);
        var selectedTag = tag ?? Tag;
        if (!string.IsNullOrWhiteSpace(selectedTag))
        {
            start.ArgumentList.Add("--tag");
            start.ArgumentList.Add(selectedTag);
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Mod distribution failed with exit code {process.ExitCode}.");
    }
}
