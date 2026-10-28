using System.Runtime.InteropServices;

namespace KlingKlang.Tests;

[TestClass]
public sealed class AudioQueueInteropTests
{
    [TestMethod]
    public void AudioStreamDescriptionMatchesCoreAudioLayout()
    {
        Assert.AreEqual(40, Marshal.SizeOf<AudioStreamDescription>());
        Assert.AreEqual(0, Marshal.OffsetOf<AudioStreamDescription>(nameof(AudioStreamDescription.SampleRate)).ToInt32());
        Assert.AreEqual(8, Marshal.OffsetOf<AudioStreamDescription>(nameof(AudioStreamDescription.FormatId)).ToInt32());
        Assert.AreEqual(20, Marshal.OffsetOf<AudioStreamDescription>(nameof(AudioStreamDescription.FramesPerPacket)).ToInt32());
        Assert.AreEqual(36, Marshal.OffsetOf<AudioStreamDescription>(nameof(AudioStreamDescription.Reserved)).ToInt32());
    }

    [TestMethod]
    public void AudioQueueBufferUsesNativePointerAlignment()
    {
        int dataOffset = IntPtr.Size == 8 ? 8 : 4;
        int sizeOffset = IntPtr.Size == 8 ? 16 : 8;
        Assert.AreEqual(dataOffset, Marshal.OffsetOf<AudioQueueBuffer>(nameof(AudioQueueBuffer.AudioData)).ToInt32());
        Assert.AreEqual(sizeOffset, Marshal.OffsetOf<AudioQueueBuffer>(nameof(AudioQueueBuffer.AudioDataByteSize)).ToInt32());
        Assert.AreEqual(IntPtr.Size == 8 ? 56 : 28, Marshal.SizeOf<AudioQueueBuffer>());
    }
}
