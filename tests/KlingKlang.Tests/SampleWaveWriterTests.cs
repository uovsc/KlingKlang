namespace KlingKlang.Tests;

[TestClass]
public sealed class SampleWaveWriterTests
{
    [TestMethod]
    public void ClampsToPcm16AtOutputBoundaryAndProducesDecodableWav()
    {
        using var stream = new MemoryStream();
        WaveWriter.Write(stream, [-1.5f, -1f, -.5f, 0f, .5f, 1f, 1.5f], channels: 1);
        stream.Position = 44;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        short[] actual = new short[7];
        for (int i = 0; i < actual.Length; i++)
        {
            actual[i] = reader.ReadInt16();
        }

        CollectionAssert.AreEqual(new short[] { -32768, -32768, -16384, 0, 16384, 32767, 32767 }, actual);
        stream.Position = 0;
        using AudioClip clip = AudioClip.LoadWav(stream);
        Assert.AreEqual(7, clip.FrameCount);
        Assert.AreEqual(1, clip.Channels);
    }

    [TestMethod]
    public void RejectsIncompleteFramesAndNonFiniteSamples()
    {
        using var stream = new MemoryStream();
        Assert.ThrowsExactly<ArgumentException>(() => WaveWriter.Write(stream, [0f], 2));
        Assert.ThrowsExactly<ArgumentException>(() => WaveWriter.Write(stream, [float.NaN], 1));
        Assert.AreEqual(0L, stream.Length);
    }
}
