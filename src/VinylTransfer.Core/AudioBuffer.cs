namespace VinylTransfer.Core;

public sealed record AudioBuffer(float[] Samples, int SampleRate, int Channels)
{
    public int FrameCount => Samples.Length / Channels;
}
