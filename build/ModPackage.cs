using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ModPackage
{
    public static void Validate(string payload, string id, string version)
    {
        foreach (var suffix in new[] { ".dll", ".pdb", ".deps.json", ".runtimeconfig.json", ".manifest.json" })
            if (!File.Exists(Path.Combine(payload, id + suffix)))
                throw new InvalidDataException($"Missing payload file: {id}{suffix}");

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, id + ".manifest.json")));
        if (manifest.RootElement.GetProperty("Id").GetString() != id ||
            manifest.RootElement.GetProperty("Version").GetString() != version)
            throw new InvalidDataException("Generated manifest ID/version does not match the project.");

        foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(payload, file).Replace('\\', '/');
            var first = relative.Split('/')[0];
            if (relative.Contains('/') && first is not ("lang" or "resources" or "efl"))
                throw new InvalidDataException($"Unexpected payload directory: {relative}");
            if (!relative.Contains('/') && relative != id + ".dll" && relative != id + ".pdb" &&
                relative != id + ".manifest.json" && relative != id + ".deps.json" &&
                relative != id + ".runtimeconfig.json")
                throw new InvalidDataException($"Unexpected payload file: {relative}. Declare and review additional dependencies before shipping.");
            if (relative.StartsWith("lang/", StringComparison.Ordinal))
            {
                if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || relative.Split('/').Length != 3)
                    throw new InvalidDataException($"Invalid localization path: {relative}");
                using var localization = JsonDocument.Parse(File.ReadAllText(file));
            }
        }
    }

    public static string Create(string payload, string id, string version, string output, string tag)
    {
        Validate(payload, id, version);
        return WriteArchive(payload, id + "/", output, $"{id}-mod-{tag}.zip");
    }

    public static string CreateFull(string stage, string id, string version, string output, string tag)
    {
        Validate(Path.Combine(stage, "fahrenheit", "mods", id), id, version);
        foreach (var relative in new[] { "fhstage0.exe", "fhstage1.dll", "fh.dll", "fhr.dll", "fhr.manifest.json", "fhr.runtimeconfig.json", "fhr.deps.json", "nethost.dll" })
            if (!File.Exists(Path.Combine(stage, "fahrenheit", "bin", relative)))
                throw new InvalidDataException($"Full package is missing host file: {relative}");
        var bin = Path.Combine(stage, "fahrenheit", "bin");
        if (Directory.GetFiles(bin, "*", SearchOption.AllDirectories).Any(f => ExcludeHostFile(Path.GetRelativePath(bin, f).Replace('\\', '/'))))
            throw new InvalidDataException("Full package contains editor tools, link artifacts or native assets for another platform.");
        foreach (var relative in new[] { "start-ffx-with-mods.cmd", "fahrenheit/mods/loadorder" })
            if (!File.Exists(Path.Combine(stage, relative))) throw new InvalidDataException($"Full package is missing {relative}");
        return WriteArchive(stage, "", output, $"{id}-full-{tag}.zip");
    }

    public static void CopyHost(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (ExcludeHostFile(relative)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static bool ExcludeHostFile(string relative)
    {
        var name = Path.GetFileName(relative);
        if (Path.GetExtension(name) is ".lib" or ".exp") return true;
        if (new[] { "fhatelier.", "fhdedit.", "fheedit.", "fhmodmgr." }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        var parts = relative.Split('/');
        return parts.Length > 1 && parts[0] == "runtimes" && parts[1] is not ("win-x86" or "win");
    }

    private static string WriteArchive(string payload, string prefix, string output, string name)
    {
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, name);
        var temporary = path + ".tmp";
        try
        {
            using (var archive = new ZipArchive(File.Create(temporary), ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(payload, file).Replace('\\', '/');
                    var entry = archive.CreateEntry(prefix + relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var source = File.OpenRead(file);
                    using var destination = entry.Open();
                    source.CopyTo(destination);
                }
            }
            using (var archive = ZipFile.OpenRead(temporary))
            {
                if (archive.Entries.Any(e => !e.FullName.StartsWith(prefix, StringComparison.Ordinal) || e.FullName.Contains("..")))
                    throw new InvalidDataException("Archive must extract directly into Fahrenheit's mods directory.");
            }
            var budgetName = prefix.Length == 0 ? "MAX_FULL_ZIP_MB" : "MAX_MOD_ZIP_MB";
            var configured = Environment.GetEnvironmentVariable(budgetName);
            var budget = configured is null ? (prefix.Length == 0 ? 200 : 64) : int.Parse(configured, System.Globalization.CultureInfo.InvariantCulture);
            if (budget <= 0 || new FileInfo(temporary).Length > budget * 1024L * 1024L)
                throw new InvalidDataException($"Archive exceeds {budgetName} ({budget} MiB).");
            File.Move(temporary, path, true);
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            File.WriteAllText(path + ".sha256", $"{hash}  {Path.GetFileName(path)}\n", new UTF8Encoding(false));
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
