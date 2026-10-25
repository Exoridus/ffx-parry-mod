using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Fahrenheit.Mods.Parry;

internal static class NativeBuild
{
    internal const long ImageLength = 10687744;
    internal const string ImageSha256 = "0537b2a1047f3266e73495cd4e35f63f0777f4231d417699f979954686da686d";

    internal static bool IsSupportedIdentity(long length, string sha256)
        => length == ImageLength && string.Equals(sha256, ImageSha256, System.StringComparison.OrdinalIgnoreCase);

    internal static bool TryValidate(out string error)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            string? path = process.MainModule?.FileName;
            if (path is null)
            {
                error = "Cannot resolve the running executable.";
                return false;
            }
            using var stream = File.OpenRead(path);
            string hash = System.Convert.ToHexStringLower(SHA256.HashData(stream));
            if (IsSupportedIdentity(stream.Length, hash))
            {
                error = string.Empty;
                return true;
            }
            error = $"Unsupported executable ({stream.Length} bytes, SHA-256 {hash}); expected Steam build 25501027 ({ImageSha256}).";
        }
        catch (System.Exception exception)
        {
            error = $"Cannot verify the running executable: {exception.Message}";
        }
        return false;
    }
}
