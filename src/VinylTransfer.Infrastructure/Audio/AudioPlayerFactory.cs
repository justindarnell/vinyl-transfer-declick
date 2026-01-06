using System.Runtime.InteropServices;

namespace VinylTransfer.Infrastructure.Audio;

public static class AudioPlayerFactory
{
    public static IAudioPlayer Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new NaudioAudioPlayer();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new ManagedBassAudioPlayer();
        }

        return new UnsupportedAudioPlayer();
    }
}
