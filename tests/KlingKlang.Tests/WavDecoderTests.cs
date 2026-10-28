using System.Buffers.Binary;

namespace KlingKlang.Tests;

[TestClass]
public sealed class WavDecoderTests
{
    [TestMethod]
    public void DecodesMonoAndStereoSampleValuesAndOrdering()
    {
        using var mono = WaveTestData.Clip([short.MinValue, 0, short.MaxValue]);
        using var stereo = WaveTestData.Clip([16384, -16384, short.MaxValue, short.MinValue], channels: 2);
        using var engine = AudioEngine.CreateOffline();
        Assert.AreEqual(1, mono.Channels);
        Assert.AreEqual(3, mono.FrameCount);
        Assert.AreEqual(48_000, mono.SampleRate);
        Assert.AreEqual(2, stereo.Channels);
        Assert.AreEqual(2, stereo.FrameCount);

        engine.Play(mono);
        float[] output = new float[6];
        engine.Render(output);
        CollectionAssert.AreEqual(new[] { -1f, -1f, 0f, 0f, 32767f / 32768f, 32767f / 32768f }, output);

        engine.Play(stereo);
        engine.Render(output);
        CollectionAssert.AreEqual(new[] { .5f, -.5f, 32767f / 32768f, -1f, 0f, 0f }, output);
    }

    [TestMethod]
    public void AcceptsUnknownOddChunksAndDataBeforeFormatOnNonSeekableStream()
    {
        byte[] wav = WaveTestData.Wave(("JUNK", [1, 2, 3]), WaveTestData.Data(16384),
            ("LIST", [4]), WaveTestData.Format(), ("tail", [5, 6]));
        using var stream = new NonSeekableStream(wav);
        using AudioClip clip = AudioClip.LoadWav(stream);
        using var engine = AudioEngine.CreateOffline();
        engine.Play(clip);
        float[] output = new float[2];
        engine.Render(output);
        CollectionAssert.AreEqual(new[] { .5f, .5f }, output);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    public void RejectsEveryTruncatedPrefix()
    {
        byte[] wav = WaveTestData.Wave(("JUNK", [1, 2, 3]), WaveTestData.Format(), WaveTestData.Data(1, 2));
        for (int length = 0; length < wav.Length; length++)
        {
            using var stream = new MemoryStream(wav, 0, length);
            Assert.ThrowsExactly<InvalidDataException>(() => AudioClip.LoadWav(stream), $"Prefix length {length}");
        }
    }

    [TestMethod]
    public void RejectsInvalidRiffHeadersAndChunkBoundaries()
    {
        byte[] valid = WaveTestData.Pcm([1]);
        byte[] badMagic = (byte[])valid.Clone();
        badMagic[0] = (byte)'X';
        AssertInvalid(badMagic);
        byte[] tooSmall = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooSmall.AsSpan(4, 4), 3);
        AssertInvalid(tooSmall);
        byte[] partialHeader = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(partialHeader.AsSpan(4, 4), 7);
        AssertInvalid(partialHeader);
        byte[] chunkOutsideRiff = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(chunkOutsideRiff.AsSpan(16, 4), uint.MaxValue);
        AssertInvalid(chunkOutsideRiff);
        byte[] missingPad = WaveTestData.Wave(WaveTestData.Format(), WaveTestData.Data(1), ("JUNK", [1]));
        BinaryPrimitives.WriteUInt32LittleEndian(missingPad.AsSpan(4, 4), (uint)missingPad.Length - 9);
        AssertInvalid(missingPad);
    }

    [TestMethod]
    public void RejectsMissingDuplicateOrMalformedChunksAndFrames()
    {
        AssertInvalid(WaveTestData.Wave(WaveTestData.Data(1)));
        AssertInvalid(WaveTestData.Wave(WaveTestData.Format()));
        AssertInvalid(WaveTestData.Wave(WaveTestData.Format(), WaveTestData.Data()));
        AssertInvalid(WaveTestData.Wave(WaveTestData.Format(), WaveTestData.Format(), WaveTestData.Data(1)));
        AssertInvalid(WaveTestData.Wave(WaveTestData.Format(), WaveTestData.Data(1), WaveTestData.Data(2)));
        AssertInvalid(WaveTestData.Wave(("fmt ", new byte[15]), WaveTestData.Data(1)));
        AssertInvalid(WaveTestData.Wave(WaveTestData.Format(channels: 2), WaveTestData.Data(1)));
        AssertInvalid(WaveTestData.Wave(WaveTestData.Format(), ("data", new byte[3])));
        AssertInvalid(WaveTestData.Wave(WaveTestData.Format(blockAlign: 4), WaveTestData.Data(1)));
        AssertInvalid(WaveTestData.Wave(WaveTestData.Format(byteRate: 1), WaveTestData.Data(1)));
    }

    [TestMethod]
    public void RejectsUnsupportedFormatsAndSampleRates()
    {
        AssertUnsupported(WaveTestData.Format(encoding: 3));
        AssertUnsupported(WaveTestData.Format(channels: 3));
        AssertUnsupported(WaveTestData.Format(bits: 24));
        AssertUnsupported(WaveTestData.Format(sampleRate: 44_100));
    }

    [TestMethod]
    public void RejectsInvalidStreamAndDisposedClip()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => AudioClip.LoadWav((Stream)null!));
        var closed = new MemoryStream();
        closed.Dispose();
        Assert.ThrowsExactly<ArgumentException>(() => AudioClip.LoadWav(closed));
        AudioClip clip = WaveTestData.Clip([1]);
        clip.Dispose();
        clip.Dispose();
        using var engine = AudioEngine.CreateOffline();
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.Play(clip));
    }

    private static void AssertInvalid(byte[] bytes) =>
        Assert.ThrowsExactly<InvalidDataException>(() => AudioClip.LoadWav(new MemoryStream(bytes)));

    private static void AssertUnsupported((string Id, byte[] Payload) format) =>
        Assert.ThrowsExactly<NotSupportedException>(() =>
            AudioClip.LoadWav(new MemoryStream(WaveTestData.Wave(format, WaveTestData.Data(1)))));

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 2));
        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, 2)]);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
