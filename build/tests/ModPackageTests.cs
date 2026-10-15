using System.IO.Compression;
using Xunit;

public sealed class ModPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FahrenheitModPackage-" + Guid.NewGuid().ToString("N"));
    private string Payload => Path.Combine(_root, "payload");

    public ModPackageTests()
    {
        Directory.CreateDirectory(Payload);
        foreach (var suffix in new[] { ".dll", ".pdb", ".deps.json", ".runtimeconfig.json" })
            File.WriteAllText(Path.Combine(Payload, "example" + suffix), "fixture");
        File.WriteAllText(Path.Combine(Payload, "example.manifest.json"), "{\"Id\":\"example\",\"Version\":\"1.2.3\"}");
    }

    [Fact]
    public void ExtractsDirectlyIntoModsAndIsByteDeterministic()
    {
        var first = ModPackage.Create(Payload, "example", "1.2.3", Path.Combine(_root, "first"), "v1.2.3");
        var second = ModPackage.Create(Payload, "example", "1.2.3", Path.Combine(_root, "second"), "v1.2.3");
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        using var zip = ZipFile.OpenRead(first);
        Assert.Contains(zip.Entries, e => e.FullName == "example/example.dll");
        Assert.All(zip.Entries, e => Assert.StartsWith("example/", e.FullName));
        Assert.True(File.Exists(first + ".sha256"));
    }

    [Theory]
    [InlineData(".dll")]
    [InlineData(".pdb")]
    [InlineData(".deps.json")]
    [InlineData(".runtimeconfig.json")]
    [InlineData(".manifest.json")]
    public void RejectsIncompleteSdkPayload(string suffix)
    {
        File.Delete(Path.Combine(Payload, "example" + suffix));
        Assert.Throws<InvalidDataException>(() => ModPackage.Validate(Payload, "example", "1.2.3"));
    }

    [Theory]
    [InlineData("fh.dll")]
    [InlineData("loadorder")]
    [InlineData(".workspace/config.json")]
    [InlineData("src/Module.cs")]
    public void RejectsHostFilesAndDevelopmentContent(string relative)
    {
        var path = Path.Combine(Payload, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        Assert.Throws<InvalidDataException>(() => ModPackage.Validate(Payload, "example", "1.2.3"));
    }

    [Fact]
    public void RejectsMismatchedManifest()
    {
        Assert.Throws<InvalidDataException>(() => ModPackage.Validate(Payload, "example", "9.9.9"));
    }

    [Fact]
    public void RejectsInvalidGeneratedManifestJson()
    {
        File.WriteAllText(Path.Combine(Payload, "example.manifest.json"), "{\"Id\":\"example\",\"Version\":\"1.2.3\",\"Desc\":\"line\nline\"}");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ModPackage.Validate(Payload, "example", "1.2.3"));
    }

    [Fact]
    public void RejectsFullPackageWithoutMatchingHost()
    {
        var destination = Path.Combine(_root, "full", "fahrenheit", "mods", "example");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(Payload)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        Assert.Throws<InvalidDataException>(() => ModPackage.CreateFull(Path.Combine(_root, "full"), "example", "1.2.3", Path.Combine(_root, "out"), "v1.2.3"));
    }

    [Fact]
    public void RejectsUnrelatedEditorsInFullPackage()
    {
        var stage = Path.Combine(_root, "full");
        var destination = Path.Combine(stage, "fahrenheit", "mods", "example");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(Payload)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        var bin = Path.Combine(stage, "fahrenheit", "bin");
        Directory.CreateDirectory(bin);
        foreach (var name in new[] { "fhstage0.exe", "fhstage1.dll", "fh.dll", "fhr.dll", "fhr.manifest.json", "fhr.runtimeconfig.json", "fhr.deps.json", "nethost.dll", "fhatelier.exe" })
            File.WriteAllText(Path.Combine(bin, name), "fixture");
        File.WriteAllText(Path.Combine(stage, "start-ffx-with-mods.cmd"), "fixture");
        File.WriteAllText(Path.Combine(stage, "fahrenheit", "mods", "loadorder"), "example\n");
        Assert.Throws<InvalidDataException>(() => ModPackage.CreateFull(stage, "example", "1.2.3", Path.Combine(_root, "out"), "v1.2.3"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
