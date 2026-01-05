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
            throw new ArgumentException("Path must be provided.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("WAV file not found.", path);
        }

        using var reader = new AudioFileReader(path);
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;
        var totalSampleCount = (int)(reader.Length / sizeof(float));
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
            throw new ArgumentException("Path must be provided.", nameof(path));
        }

        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (buffer.Samples is null || buffer.Samples.Length == 0)
        {
            throw new ArgumentException("Audio buffer samples must be provided.", nameof(buffer));
        }

        if (buffer.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(buffer), "Sample rate must be greater than zero.");
        }

        if (buffer.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(buffer), "Channel count must be greater than zero.");
        }

        var format = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, buffer.Channels);
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(buffer.Samples, 0, buffer.Samples.Length);
    }
}
