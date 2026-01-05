using NAudio.Wave;
using VinylTransfer.Core;
using System.IO;

namespace VinylTransfer.Infrastructure;

public sealed class WavFileService
{
    public AudioBuffer Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("WAV file path must be provided.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("WAV file not found.", path);
        }

        using var reader = new AudioFileReader(path);
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;
        var totalSampleCount = (int)(reader.Length / sizeof(float));
        
        // Validate reasonable file size (e.g., max 2GB of samples = 500M floats)
        const int maxSamples = 500_000_000;
        if (totalSampleCount > maxSamples)
        {
            throw new InvalidOperationException($"WAV file is too large. Maximum supported size is {maxSamples} samples.");
        }
        
        var samples = new float[Math.Max(totalSampleCount, 0)];
        var totalRead = 0;

        while (totalRead < samples.Length)
        {
            var read = reader.Read(samples, totalRead, samples.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead != samples.Length)
        {
            Array.Resize(ref samples, totalRead);
        }

        return new AudioBuffer(samples, sampleRate, channels);
    }

    public void Write(string path, AudioBuffer buffer)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("WAV file path must be provided.", nameof(path));
        }

        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        var format = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, buffer.Channels);
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(buffer.Samples, 0, buffer.Samples.Length);
    }
}
