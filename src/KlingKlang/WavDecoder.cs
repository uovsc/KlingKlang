using System.Buffers.Binary;

namespace KlingKlang;

internal static class WavDecoder
{
    public static AudioClip Decode(Stream stream)
    {
        try
        {
            return DecodeCore(stream);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The WAV file is truncated.", exception);
        }
    }

    private static AudioClip DecodeCore(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("The file is not a RIFF/WAVE file.");
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (riffSize < 4)
        {
            throw new InvalidDataException("The RIFF size is too small for a WAVE header.");
        }

        ulong remaining = riffSize - 4u;
        int channels = 0;
        int sampleRate = 0;
        byte[]? data = null;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        while (remaining > 0)
        {
            if (remaining < 8)
            {
                throw new InvalidDataException("The RIFF chunk header is incomplete.");
            }

            stream.ReadExactly(chunkHeader);
            remaining -= 8;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            ulong paddedSize = (ulong)size + (size & 1u);
            if (paddedSize > remaining)
            {
                throw new InvalidDataException("A WAV chunk extends beyond the RIFF boundary.");
            }

            if (chunkHeader[..4].SequenceEqual("fmt "u8))
            {
                if (channels != 0 || size < 16)
                {
                    throw new InvalidDataException("The WAV format chunk is invalid or duplicated.");
                }

                stream.ReadExactly(format);
                (channels, sampleRate) = ValidateFormat(format);
                SkipExactly(stream, size - 16u);
            }
            else if (chunkHeader[..4].SequenceEqual("data"u8))
            {
                if (data is not null)
                {
                    throw new InvalidDataException("The WAV data chunk is duplicated.");
                }

                if (size > Array.MaxLength)
                {
                    throw new InvalidDataException("The WAV data chunk is too large.");
                }

                using var buffer = new MemoryStream();
                CopyExactly(stream, buffer, size);
                data = buffer.ToArray();
            }
            else
            {
                SkipExactly(stream, size);
            }

            if ((size & 1u) != 0)
            {
                SkipExactly(stream, 1);
            }

            remaining -= paddedSize;
        }

        if (channels == 0 || data is null || data.Length == 0 || data.Length % (channels * 2) != 0)
        {
            throw new InvalidDataException("The WAV must contain a format and complete PCM frames.");
        }

        return new AudioClip(Pcm16Resampler.Convert(data, channels, sampleRate), channels);
    }

    private static (int Channels, int SampleRate) ValidateFormat(ReadOnlySpan<byte> format)
    {
        ushort encoding = BinaryPrimitives.ReadUInt16LittleEndian(format);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
        ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);

        if (encoding != 1 || channels is not (1 or 2) || bitsPerSample != 16)
        {
            throw new NotSupportedException("Only uncompressed PCM16 mono or stereo WAV files are supported.");
        }

        if (sampleRate == 0)
        {
            throw new InvalidDataException("The WAV sample rate must be nonzero.");
        }

        if (sampleRate < 8_000 || sampleRate > AudioClip.MixSampleRate)
        {
            throw new NotSupportedException("Only WAV sample rates from 8,000 through 48,000 Hz are supported.");
        }

        if (blockAlign != channels * 2 || byteRate != sampleRate * blockAlign)
        {
            throw new InvalidDataException("The WAV byte rate or block alignment is invalid.");
        }

        return (channels, (int)sampleRate);
    }

    private static void SkipExactly(Stream stream, uint count) => CopyExactly(stream, null, count);

    private static void CopyExactly(Stream source, Stream? destination, uint count)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (count > 0)
        {
            int length = (int)Math.Min(count, (uint)buffer.Length);
            source.ReadExactly(buffer[..length]);
            destination?.Write(buffer[..length]);
            count -= (uint)length;
        }
    }
}
