using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KlingKlang;

[SupportedOSPlatform("linux")]
internal sealed class AlsaNative : IAlsaApi
{
    private const string Library = "libasound.so.2";

    public int Open(out IntPtr handle) => snd_pcm_open(out handle, "default", 0, 1);
    public int SetParameters(IntPtr handle) => snd_pcm_set_params(handle, 2, 3, 2, 48_000, 1, 50_000);
    public int GetParameters(IntPtr handle, out nuint bufferFrames, out nuint periodFrames) =>
        snd_pcm_get_params(handle, out bufferFrames, out periodFrames);
    public nint Available(IntPtr handle) => snd_pcm_avail_update(handle);
    public nint Write(IntPtr handle, IntPtr samples, nuint frames) => snd_pcm_writei(handle, samples, frames);
    public int Prepare(IntPtr handle) => snd_pcm_prepare(handle);
    public int Resume(IntPtr handle) => snd_pcm_resume(handle);
    public int Drop(IntPtr handle) => snd_pcm_drop(handle);
    public int Close(IntPtr handle) => snd_pcm_close(handle);
    public string ErrorString(int error) => Marshal.PtrToStringUTF8(snd_strerror(error)) ?? $"error {error}";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_open(out IntPtr pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_get_params(IntPtr pcm, out nuint bufferFrames, out nuint periodFrames);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint snd_pcm_avail_update(IntPtr pcm);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint snd_pcm_writei(IntPtr pcm, IntPtr buffer, nuint frames);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_prepare(IntPtr pcm);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_resume(IntPtr pcm);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_drop(IntPtr pcm);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_close(IntPtr pcm);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr snd_strerror(int error);
}
