using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Fahrenheit.Mods.Parry;

public sealed class Stage1NativeSignatureTests
{
    [Theory]
    [InlineData("MsCalcCommandProbeDelegate", true)]
    [InlineData("MsCheckStatusBeforeActionProbeDelegate", false)]
    public void CommandProbesPreserveBothNativeStackArguments(string name, bool cueFirst)
    {
        var type = typeof(ParryModule).GetNestedType(name, BindingFlags.NonPublic)!;
        var invoke = type.GetMethod("Invoke")!;
        Assert.Equal(CallingConvention.Cdecl, type.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()!.CallingConvention);
        Assert.Equal(cueFirst ? typeof(void) : typeof(int), invoke.ReturnType);
        Assert.Equal(cueFirst ? new[] { typeof(nint), typeof(int) } : new[] { typeof(int), typeof(nint) },
            invoke.GetParameters().Select(p => p.ParameterType).ToArray());
    }
}
