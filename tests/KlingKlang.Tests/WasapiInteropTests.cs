using System.Reflection;
using System.Runtime.InteropServices;

namespace KlingKlang.Tests;

[TestClass]
public sealed class WasapiInteropTests
{
    [TestMethod]
    public void WaveFormatMatchesWindowsWaveFormatExLayout()
    {
        Type type = GetNested("WaveFormat");
        Assert.AreEqual(18, Marshal.SizeOf(type));
        Assert.AreEqual(0, Marshal.OffsetOf(type, "FormatTag").ToInt32());
        Assert.AreEqual(2, Marshal.OffsetOf(type, "Channels").ToInt32());
        Assert.AreEqual(4, Marshal.OffsetOf(type, "SampleRate").ToInt32());
        Assert.AreEqual(8, Marshal.OffsetOf(type, "AverageBytesPerSecond").ToInt32());
        Assert.AreEqual(12, Marshal.OffsetOf(type, "BlockAlign").ToInt32());
        Assert.AreEqual(14, Marshal.OffsetOf(type, "BitsPerSample").ToInt32());
        Assert.AreEqual(16, Marshal.OffsetOf(type, "ExtraSize").ToInt32());
    }

    [TestMethod]
    public void ComIdentifiersAndVtableOrderMatchWindowsInterfaces()
    {
        Assert.AreEqual(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), GetNested("MMDeviceEnumerator").GUID);
        Assert.AreEqual(new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), GetNested("IMMDeviceEnumerator").GUID);
        Assert.AreEqual(new Guid("D666063F-1587-4E43-81F1-B948E807363F"), GetNested("IMMDevice").GUID);
        Assert.AreEqual(new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), GetNested("IAudioClient").GUID);
        Assert.AreEqual(new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), GetNested("IAudioRenderClient").GUID);
        CollectionAssert.AreEqual(
            new[] { "Initialize", "GetBufferSize", "GetStreamLatency", "GetCurrentPadding", "IsFormatSupported",
                "GetMixFormat", "GetDevicePeriod", "Start", "Stop", "Reset", "SetEventHandle", "GetService" },
            GetNested("IAudioClient").GetMethods().OrderBy(method => method.MetadataToken).Select(method => method.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "GetBuffer", "ReleaseBuffer" },
            GetNested("IAudioRenderClient").GetMethods().OrderBy(method => method.MetadataToken).Select(method => method.Name).ToArray());
    }

    [TestMethod]
    public void ClassifiesInvalidatedAndMissingEndpointsButNotUnrelatedErrors()
    {
        Type type = typeof(AudioEngine).Assembly.GetType("KlingKlang.WasapiOutputStream", throwOnError: true)!;
        MethodInfo createError = type.GetMethod("CreateError", BindingFlags.Static | BindingFlags.NonPublic)!;
        object invalidated = createError.Invoke(null, [unchecked((int)0x88890004), "test"])!;
        object resources = createError.Invoke(null, [unchecked((int)0x88890026), "test"])!;
        object missing = createError.Invoke(null, [unchecked((int)0x80070490), "test"])!;
        object unrelated = createError.Invoke(null, [unchecked((int)0x80070057), "test"])!;
        Assert.IsInstanceOfType<AudioDeviceLostException>(invalidated);
        Assert.IsInstanceOfType<AudioDeviceLostException>(resources);
        Assert.IsInstanceOfType<AudioDeviceLostException>(missing);
        Assert.AreEqual(typeof(InvalidOperationException), unrelated.GetType());
    }

    private static Type GetNested(string name) =>
        typeof(AudioEngine).Assembly.GetType("KlingKlang.WasapiOutputStream", throwOnError: true)!
            .GetNestedType(name, BindingFlags.NonPublic)!;
}
