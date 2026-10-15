using System.Text.Json.Nodes;
using Parry.Build;
using Xunit;

namespace Fahrenheit.Mods.Parry;

public sealed class SettingsMigrationTests : IDisposable
{
    private const string Module = "Fahrenheit.Mods.Parry.ParryModule";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ffx-parry-settings-tests", Guid.NewGuid().ToString("N"));
    private string Config => Path.Combine(_root, "config", "global", "fhparry", Module + ".json");

    [Fact]
    public void MigrationPreservesValuesAndOriginalBackupAndIsIdempotent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        const string original = "{\"fhparry.enabled\":false,\"fhparry.list\":[1,null,\"für\"],\"unrelated\":7}";
        File.WriteAllText(Config, original);
        Assert.True(SettingsMigration.Migrate(_root));
        var values = JsonNode.Parse(File.ReadAllText(Config))!.AsObject();
        Assert.False(values[Module + ".enabled"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(values["fhparry.list"], values[Module + ".list"]));
        Assert.Equal(7, values["unrelated"]!.GetValue<int>());
        Assert.Equal(original, File.ReadAllText(Config + ".alpha11.bak"));
        var migrated = File.ReadAllText(Config);
        Assert.False(SettingsMigration.Migrate(_root));
        Assert.Equal(migrated, File.ReadAllText(Config));
        Assert.Equal(original, File.ReadAllText(Config + ".alpha11.bak"));
    }

    [Fact]
    public void ExistingNewSettingWinsAndPreviousBackupIsRetained()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        File.WriteAllText(Config, "{\"fhparry.enabled\":false,\"" + Module + ".enabled\":true,\"fhparry.extra\":null}");
        File.WriteAllText(Config + ".alpha11.bak", "existing backup");
        Assert.True(SettingsMigration.Migrate(_root));
        var values = JsonNode.Parse(File.ReadAllText(Config))!.AsObject();
        Assert.True(values[Module + ".enabled"]!.GetValue<bool>());
        Assert.True(values.ContainsKey(Module + ".extra"));
        Assert.Null(values[Module + ".extra"]);
        Assert.Equal("existing backup", File.ReadAllText(Config + ".alpha11.bak"));
    }

    [Fact]
    public void MissingSettingsAreNotCreated() => Assert.False(SettingsMigration.Migrate(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
