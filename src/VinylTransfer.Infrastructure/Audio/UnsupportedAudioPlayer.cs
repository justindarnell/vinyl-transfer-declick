using System;
using VinylTransfer.Core;

namespace VinylTransfer.Infrastructure.Audio;

public sealed class UnsupportedAudioPlayer : IAudioPlayer
{
    public bool IsSupported => false;

    public event EventHandler? PlaybackStopped;

    public void Play(AudioBuffer buffer, long startFrame)
    {
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
    }

    public long GetPositionFrames()
    {
        return 0;
    }

    public void Dispose()
    {
    }
}
