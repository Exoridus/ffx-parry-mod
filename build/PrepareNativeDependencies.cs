using System.Diagnostics;
using System.Text.Json;

if (args.Length != 2 || !(args[1].Equals("Debug", StringComparison.OrdinalIgnoreCase) || args[1].Equals("Release", StringComparison.OrdinalIgnoreCase)))
    throw new ArgumentException("Expected Fahrenheit directory and Debug or Release configuration.");

var root = Path.GetFullPath(args[0]);
var configuration = args[1].Equals("Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
var project = Path.Combine(root, "src", "Fahrenheit.NativeDeps.csproj");
string[] properties = [$"-p:Configuration={configuration}", $"-p:SolutionDir={root}/"];
Run(["restore", project, ..properties, "--verbosity", "quiet"]);
using var metadata = JsonDocument.Parse(Run(["msbuild", project, ..properties,
    "-getProperty:NuGetPackageRoot", "-getProperty:BundledNETCoreAppPackageVersion",
    "-getProperty:NetCoreTargetingPackRoot", "-getProperty:_FhOutDirBin"]));
var values = metadata.RootElement.GetProperty("Properties");
string Property(string name) => values.GetProperty(name).GetString()!;
var relativeHost = Path.Combine("microsoft.netcore.app.host.win-x86", Property("BundledNETCoreAppPackageVersion"), "runtimes", "win-x86", "native");
var host = Path.Combine(Property("NetCoreTargetingPackRoot"), relativeHost);
// An x64 SDK can restore x86 hosting assets into NuGet instead of dotnet/packs.
if (!File.Exists(Path.Combine(host, "nethost.h")))
    host = Path.Combine(Property("NuGetPackageRoot"), relativeHost);
string[] headers = ["nethost.h", "coreclr_delegates.h", "hostfxr.h", "nethost.lib"];
foreach (var file in headers.Append("nethost.dll"))
    if (!File.Exists(Path.Combine(host, file)))
        throw new FileNotFoundException("Missing x86 hosting asset.", Path.Combine(host, file));

Run(["msbuild", project, ..properties, "-target:__FhCopyDbgshim", "-verbosity:quiet"]);
var includes = Path.Combine(root, "src", "stage1", "inc", "nethost");
var binaries = Property("_FhOutDirBin");
Directory.CreateDirectory(includes);
Directory.CreateDirectory(binaries);
foreach (var file in headers)
    File.Copy(Path.Combine(host, file), Path.Combine(includes, file), overwrite: true);
File.Copy(Path.Combine(host, "nethost.dll"), Path.Combine(binaries, "nethost.dll"), overwrite: true);

static string Run(params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Task.WaitAll(output, errors);
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"dotnet {arguments[0]} failed ({process.ExitCode}).\n{output.Result}\n{errors.Result}");
    if (errors.Result.Length > 0) Console.Error.Write(errors.Result);
    return output.Result;
}
