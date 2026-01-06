using System;
using VinylTransfer.Core;

namespace VinylTransfer.Infrastructure.Audio;

public interface IAudioPlayer : IDisposable
{
    bool IsSupported { get; }

    event EventHandler? PlaybackStopped;

    void Play(AudioBuffer buffer, long startFrame);

    void Stop();

    long GetPositionFrames();
}
