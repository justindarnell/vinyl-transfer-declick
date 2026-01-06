using System;
using System.Buffers;
using System.Runtime.InteropServices;
using ManagedBass;
using VinylTransfer.Core;

namespace VinylTransfer.Infrastructure.Audio;

public sealed class ManagedBassAudioPlayer : IAudioPlayer
{
    private int _stream;
    private int _sampleRate;
    private int _channels;
    private float[]? _samples;
    private long _sampleOffset;
    private bool _isInitialized;
    private readonly StreamProcedure _streamReader;
    private readonly SyncProcedure _syncHandler;

    public ManagedBassAudioPlayer()
    {
        _streamReader = StreamReadCallback;
        _syncHandler = HandleStreamEnded;
    }

    public bool IsSupported => true;

    public event EventHandler? PlaybackStopped;

    public void Play(AudioBuffer buffer, long startFrame)
    {
        if (buffer is null)
        {
            return;
        }

        Stop();

        _samples = buffer.Samples;
        _sampleRate = buffer.SampleRate;
        _channels = buffer.Channels;
        _sampleOffset = Math.Clamp(startFrame, 0, buffer.FrameCount) * _channels;

        InitializeBass(_sampleRate);
        if (!_isInitialized)
        {
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
            return;
        }

        _stream = Bass.CreateStream(
            _sampleRate,
            _channels,
            BassFlags.Float,
            _streamReader,
            IntPtr.Zero);

        if (_stream != 0)
        {
            Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _syncHandler);
            Bass.ChannelPlay(_stream, true);
            return;
        }

        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (_stream == 0)
        {
            return;
        }

        Bass.ChannelStop(_stream);
        Bass.StreamFree(_stream);
        _stream = 0;
        _sampleOffset = 0;
        _samples = null;
    }

    public long GetPositionFrames()
    {
        if (_stream == 0)
        {
            return 0;
        }

        var bytesPlayed = Bass.ChannelGetPosition(_stream);
        var bytesPerFrame = sizeof(float) * _channels;
        if (bytesPerFrame == 0)
        {
            return 0;
        }

        return bytesPlayed / bytesPerFrame;
    }

    public void Dispose()
    {
        Stop();
        if (_isInitialized)
        {
            Bass.Free();
            _isInitialized = false;
        }
    }

    private int StreamReadCallback(IntPtr buffer, int length, IntPtr user)
    {
        if (_samples is null || length <= 0)
        {
            return 0;
        }

        var floatCount = length / sizeof(float);
        var remaining = _samples.LongLength - _sampleOffset;
        var toCopy = (int)Math.Clamp(remaining, 0, floatCount);

        if (toCopy <= 0)
        {
            return 0;
        }

        var rented = ArrayPool<float>.Shared.Rent(toCopy);
        try
        {
            Array.Copy(_samples, _sampleOffset, rented, 0, toCopy);
            Marshal.Copy(rented, 0, buffer, toCopy);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }

        _sampleOffset += toCopy;
        return toCopy * sizeof(float);
    }

    private void InitializeBass(int sampleRate)
    {
        if (_isInitialized && _sampleRate == sampleRate)
        {
            return;
        }

        if (_isInitialized)
        {
            Bass.Free();
            _isInitialized = false;
        }

        _isInitialized = Bass.Init(-1, sampleRate);
    }

    private void HandleStreamEnded(int handle, int channel, int data, IntPtr user)
    {
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }
}
