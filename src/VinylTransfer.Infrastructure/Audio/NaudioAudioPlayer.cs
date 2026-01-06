using System;
using NAudio.Wave;
using VinylTransfer.Core;

namespace VinylTransfer.Infrastructure.Audio;

public sealed class NaudioAudioPlayer : IAudioPlayer
{
    private WaveOutEvent? _outputDevice;
    private BufferedWaveProvider? _bufferedProvider;

    public bool IsSupported => true;

    public event EventHandler? PlaybackStopped;

    public void Play(AudioBuffer buffer, long startFrame)
    {
        if (buffer is null)
        {
            return;
        }

        Stop();

        var format = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, buffer.Channels);
        _bufferedProvider = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true
        };

        const int chunkSizeInSamples = 4096;
        long totalFrames = buffer.FrameCount;
        int sampleSizeInBytes = sizeof(float);
        var chunkBytes = new byte[chunkSizeInSamples * sampleSizeInBytes];

        var clampedStart = Math.Clamp(startFrame, 0, totalFrames);
        long sampleOffset = clampedStart * buffer.Channels;
        long totalSamples = buffer.Samples.Length;

        while (sampleOffset < totalSamples)
        {
            int samplesThisChunk = (int)Math.Min(chunkSizeInSamples, totalSamples - sampleOffset);
            int bytesThisChunk = samplesThisChunk * sampleSizeInBytes;

            Buffer.BlockCopy(
                buffer.Samples,
                (int)(sampleOffset * sampleSizeInBytes),
                chunkBytes,
                0,
                bytesThisChunk);

            _bufferedProvider.AddSamples(chunkBytes, 0, bytesThisChunk);
            sampleOffset += samplesThisChunk;
        }

        _outputDevice = new WaveOutEvent();
        _outputDevice.PlaybackStopped += HandlePlaybackStopped;
        _outputDevice.Init(_bufferedProvider);
        _outputDevice.Play();
    }

    public void Stop()
    {
        if (_outputDevice is null)
        {
            return;
        }

        try
        {
            if (_outputDevice.PlaybackState != PlaybackState.Stopped)
            {
                _outputDevice.Stop();
            }
        }
        catch (ObjectDisposedException)
        {
            // Device already disposed, ignore
        }

        _outputDevice.PlaybackStopped -= HandlePlaybackStopped;
        _outputDevice.Dispose();
        _outputDevice = null;
        _bufferedProvider = null;
    }

    public long GetPositionFrames()
    {
        if (_outputDevice is null)
        {
            return 0;
        }

        var outputFormat = _outputDevice.OutputWaveFormat;
        if (outputFormat is null)
        {
            return 0;
        }

        var bytesPlayed = _outputDevice.GetPosition();
        var bytesPerSample = sizeof(float) * outputFormat.Channels;
        var samplesPlayed = bytesPlayed / bytesPerSample;
        return samplesPlayed / outputFormat.Channels;
    }

    public void Dispose()
    {
        Stop();
    }

    private void HandlePlaybackStopped(object? sender, StoppedEventArgs args)
    {
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }
}
