using NAudio.Wave;
using VinylTransfer.Core;

namespace VinylTransfer.Infrastructure;

public sealed class WavFileService
{
    public AudioBuffer Read(string path)
    {
        using var reader = new AudioFileReader(path);
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;
        var samples = new List<float>();
        var buffer = new float[reader.WaveFormat.SampleRate * channels];
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return new AudioBuffer(samples.ToArray(), sampleRate, channels);
    }

    public void Write(string path, AudioBuffer buffer)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, buffer.Channels);
        using var writer = new WaveFileWriter(path, format);
        writer.WriteSamples(buffer.Samples, 0, buffer.Samples.Length);
    }
}
