using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KlingKlang;

[SupportedOSPlatform("macos")]
internal sealed class AudioQueueNative : IAudioQueueApi
{
    private const string Library = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    public int Create(ref AudioStreamDescription format, AudioQueueOutputCallback callback, IntPtr userData, out IntPtr queue) =>
        AudioQueueNewOutput(ref format, callback, userData, IntPtr.Zero, IntPtr.Zero, 0, out queue);
    public int Allocate(IntPtr queue, uint bytes, out IntPtr buffer) => AudioQueueAllocateBuffer(queue, bytes, out buffer);
    public int Enqueue(IntPtr queue, IntPtr buffer) => AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
    public int Start(IntPtr queue) => AudioQueueStart(queue, IntPtr.Zero);
    public int Stop(IntPtr queue) => AudioQueueStop(queue, 1);
    public int Dispose(IntPtr queue) => AudioQueueDispose(queue, 1);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueNewOutput(ref AudioStreamDescription format, AudioQueueOutputCallback callback,
        IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bytes, out IntPtr buffer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetCount, IntPtr packetDescriptions);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueStop(IntPtr queue, byte immediate);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int AudioQueueDispose(IntPtr queue, byte immediate);
}
