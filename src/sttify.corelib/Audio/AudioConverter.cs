using System.Buffers;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Sttify.Corelib.Audio;

public static class AudioConverter
{
    private const int TargetSampleRate = 16000;
    private const int TargetBitsPerSample = 16;
    private const int TargetChannels = 1;

    public static byte[] ConvertToVoskFormat(ReadOnlySpan<byte> audioData, WaveFormat sourceFormat)
    {
        if (audioData.IsEmpty)
            return Array.Empty<byte>();

        // If already in correct format, return as-is
        if (sourceFormat.SampleRate == TargetSampleRate &&
            sourceFormat.BitsPerSample == TargetBitsPerSample &&
            sourceFormat.Channels == TargetChannels)
        {
            return audioData.ToArray();
        }

        try
        {
            // Use array pool for temporary buffer to avoid allocation
            var pool = ArrayPool<byte>.Shared;
            var tempBuffer = pool.Rent(audioData.Length);
            try
            {
                audioData.CopyTo(tempBuffer);

                using var sourceStream = new MemoryStream(tempBuffer, 0, audioData.Length);
                using var sourceProvider = new RawSourceWaveStream(sourceStream, sourceFormat);

                // Convert to ISampleProvider for processing
                ISampleProvider sampleProvider = sourceProvider.ToSampleProvider();

                // Convert to mono if necessary
                if (sourceFormat.Channels > 1)
                {
                    sampleProvider = sampleProvider.ToMono();
                }

                // Resample if necessary
                if (sourceFormat.SampleRate != TargetSampleRate)
                {
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
                }

                // Convert back to wave format (16-bit PCM)
                var waveProvider = new SampleToWaveProvider16(sampleProvider);

                using var outputStream = new MemoryStream();
                var readBuffer = pool.Rent(4096);
                try
                {
                    int bytesRead;
                    while ((bytesRead = waveProvider.Read(readBuffer, 0, readBuffer.Length)) > 0)
                    {
                        outputStream.Write(readBuffer, 0, bytesRead);
                    }
                }
                finally
                {
                    pool.Return(readBuffer);
                }

                return outputStream.ToArray();
            }
            finally
            {
                pool.Return(tempBuffer);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Audio conversion failed: {ex.Message}");
            // Return original data if conversion fails
            return audioData.ToArray();
        }
    }

    public static byte[] ConvertToVoskFormat(byte[] audioData, WaveFormat sourceFormat)
    {
        return ConvertToVoskFormat(audioData.AsSpan(), sourceFormat);
    }

    public static WaveFormat GetVoskTargetFormat()
    {
        return new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);
    }

    public static bool IsVoskCompatible(WaveFormat format)
    {
        return format.SampleRate == TargetSampleRate &&
               format.BitsPerSample == TargetBitsPerSample &&
               format.Channels == TargetChannels;
    }

    public static double CalculateAudioLevel(ReadOnlySpan<byte> audioData, WaveFormat format)
    {
        if (audioData.IsEmpty)
            return 0.0;

        try
        {
            if (format.BitsPerSample == 16)
            {
                return Calculate16BitLevel(audioData);
            }
            else if (format.BitsPerSample == 32)
            {
                return Calculate32BitLevel(audioData);
            }
            else
            {
                return Calculate8BitLevel(audioData);
            }
        }
        catch
        {
            return 0.0;
        }
    }

    public static double CalculateAudioLevel(byte[] audioData, WaveFormat format)
    {
        return CalculateAudioLevel(audioData.AsSpan(), format);
    }

    private static double Calculate16BitLevel(ReadOnlySpan<byte> audioData)
    {
        double sum = 0;
        int sampleCount = 0;

        for (int i = 0; i < audioData.Length - 1; i += 2)
        {
            short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
            sum += Math.Abs(sample);
            sampleCount++;
        }

        if (sampleCount == 0)
            return 0.0;

        double average = sum / sampleCount;
        return Math.Min(1.0, average / 32768.0);
    }

    private static double Calculate32BitLevel(ReadOnlySpan<byte> audioData)
    {
        double sum = 0;
        int sampleCount = 0;

        for (int i = 0; i < audioData.Length - 3; i += 4)
        {
            float sample = BitConverter.ToSingle(audioData.Slice(i, 4));
            sum += Math.Abs(sample);
            sampleCount++;
        }

        if (sampleCount == 0)
            return 0.0;

        return Math.Min(1.0, sum / sampleCount);
    }

    private static double Calculate8BitLevel(ReadOnlySpan<byte> audioData)
    {
        double sum = 0;
        foreach (byte sample in audioData)
        {
            sum += Math.Abs(sample - 128); // 8-bit audio is unsigned, centered at 128
        }

        double average = sum / audioData.Length;
        return Math.Min(1.0, average / 128.0);
    }

    public static byte[] ApplyVolumeGain(byte[] audioData, WaveFormat format, float gainFactor)
    {
        if (gainFactor == 1.0f || audioData.Length == 0)
            return audioData;

        var result = new byte[audioData.Length];
        Array.Copy(audioData, result, audioData.Length);

        try
        {
            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i < result.Length - 1; i += 2)
                {
                    short sample = (short)(result[i] | (result[i + 1] << 8));
                    int amplified = (int)(sample * gainFactor);
                    amplified = Math.Max(short.MinValue, Math.Min(short.MaxValue, amplified));

                    result[i] = (byte)(amplified & 0xFF);
                    result[i + 1] = (byte)((amplified >> 8) & 0xFF);
                }
            }
            else if (format.BitsPerSample == 32)
            {
                for (int i = 0; i < result.Length - 3; i += 4)
                {
                    float sample = BitConverter.ToSingle(result, i);
                    float amplified = Math.Max(-1.0f, Math.Min(1.0f, sample * gainFactor));

                    var bytes = BitConverter.GetBytes(amplified);
                    Array.Copy(bytes, 0, result, i, 4);
                }
            }
        }
        catch
        {
            // Return original data if gain application fails
            return audioData;
        }

        return result;
    }
}
