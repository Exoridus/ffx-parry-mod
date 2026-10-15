using System.Text.Json;
using System.Text.Json.Nodes;

namespace Parry.Build;

internal static class SettingsMigration
{
    private const string Module = "Fahrenheit.Mods.Parry.ParryModule";

    internal static bool Migrate(string fahrenheitDirectory)
    {
        var path = Path.Combine(fahrenheitDirectory, "config", "global", "fhparry", Module + ".json");
        if (!File.Exists(path)) return false;
        var values = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var changed = false;
        foreach (var entry in values.ToArray())
        {
            if (!entry.Key.StartsWith("fhparry.", StringComparison.Ordinal)) continue;
            var key = Module + "." + entry.Key["fhparry.".Length..];
            if (values.ContainsKey(key)) continue;
            values[key] = entry.Value?.DeepClone();
            changed = true;
        }
        if (!changed) return false;
        var backup = path + ".alpha11.bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
        var temporary = path + ".alpha12.tmp";
        File.WriteAllText(temporary, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
        return true;
    }
}
