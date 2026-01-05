namespace VinylTransfer.Core;

public sealed record AudioBuffer
{
    public AudioBuffer(float[] samples, int sampleRate, int channels)
    {
        if (samples is null)
        {
            throw new ArgumentNullException(nameof(samples));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be greater than zero.");
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be greater than zero.");
        }

        if (samples.Length % channels != 0)
        {
            throw new ArgumentException("Sample count must be evenly divisible by the channel count.", nameof(samples));
        }

        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public float[] Samples { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public int FrameCount => Samples.Length / Channels;
}
