global using System.Text;
global using System.Reflection;
global using FhFfx = Fahrenheit.FFX;
global using System.Globalization;
global using System.Text.Json;
global using System.Text.RegularExpressions;

using Xunit;

namespace Fahrenheit.Mods.Parry;

public sealed class FfxDataMappingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ffx-parry-mappings-tests", Guid.NewGuid().ToString("N"));

    public FfxDataMappingsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void RemovedDefaultAliasDoesNotSupplyRuntimeData()
    {
        Write("ffx-mappings.json", "Legacy alias", 9);
        var mappings = new FfxDataMappings();
        mappings.LoadFromDirectories([_directory], "en-US");
        Assert.False(mappings.HasAny);
    }

    [Fact]
    public void PreferredLocaleWinsOverEnglishFallback()
    {
        Write("ffx-mappings.de.json", "Angriff", 3);
        Write("ffx-mappings.us.json", "Attack", 3);
        var mappings = new FfxDataMappings();
        mappings.LoadFromDirectories([_directory], "de-DE");
        Assert.True(mappings.TryResolveCommandDisplay((ushort)0x3000, out var name, out _));
        Assert.Equal("Angriff", name);
        Assert.True(mappings.TryGetCommandHitCount(0x3000, out var hits));
        Assert.Equal(3, hits);
    }

    [Fact]
    public void EnglishFallbackWorksWithoutDefaultAlias()
    {
        Write("ffx-mappings.us.json", "Attack", 6);
        var mappings = new FfxDataMappings();
        mappings.LoadFromDirectories([_directory], "fr-FR");
        Assert.True(mappings.TryResolveCommandDisplay((ushort)0x3000, out var name, out _));
        Assert.Equal("Attack", name);
        Assert.True(mappings.TryGetCommandMechanics(0x3000, out var mechanics));
        Assert.Equal(6, mechanics.GetProperty("hit_count").GetInt32());
    }

    private void Write(string name, string commandName, int hitCount)
    {
        var content = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Domains = new
            {
                Commands = new Dictionary<string, object>
                {
                    ["0x3000"] = new { Name = commandName, Mechanics = new { hit_count = hitCount } }
                }
            }
        });
        File.WriteAllText(Path.Combine(_directory, name), content);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
