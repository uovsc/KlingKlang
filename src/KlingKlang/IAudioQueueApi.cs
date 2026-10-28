using System.Runtime.InteropServices;

namespace KlingKlang;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

[StructLayout(LayoutKind.Sequential)]
internal struct AudioStreamDescription
{
    public double SampleRate;
    public uint FormatId;
    public uint FormatFlags;
    public uint BytesPerPacket;
    public uint FramesPerPacket;
    public uint BytesPerFrame;
    public uint ChannelsPerFrame;
    public uint BitsPerChannel;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioQueueBuffer
{
    public uint AudioDataBytesCapacity;
    public IntPtr AudioData;
    public uint AudioDataByteSize;
    public IntPtr UserData;
    public uint PacketDescriptionCapacity;
    public IntPtr PacketDescriptions;
    public uint PacketDescriptionCount;
}

internal interface IAudioQueueApi
{
    int Create(ref AudioStreamDescription format, AudioQueueOutputCallback callback, IntPtr userData, out IntPtr queue);
    int Allocate(IntPtr queue, uint bytes, out IntPtr buffer);
    int Enqueue(IntPtr queue, IntPtr buffer);
    int Start(IntPtr queue);
    int Stop(IntPtr queue);
    int Dispose(IntPtr queue);
}
