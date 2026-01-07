using System;
using System.Runtime.InteropServices;
using VinylTransfer.Core;

namespace VinylTransfer.Infrastructure.Audio;

public sealed class MacAudioQueuePlayer : IAudioPlayer
{
    private const int BufferCount = 3;
    private const int FramesPerBuffer = 4096;
    private const uint AudioFormatLinearPcm = 0x6C70636D;
    private const uint AudioFormatFlagIsFloat = 0x1;
    private const uint AudioFormatFlagIsPacked = 0x8;

    private readonly object _sync = new();
    private IntPtr _audioQueue;
    private IntPtr[]? _buffers;
    private float[]? _samples;
    private long _sampleOffset;
    private long _startFrame;
    private int _channels;
    private int _sampleRate;
    private AudioQueueOutputCallback? _callback;
    private GCHandle _selfHandle;

    public bool IsSupported => true;

    public event EventHandler? PlaybackStopped;

    public void Play(AudioBuffer buffer, long startFrame)
    {
        if (buffer is null)
        {
            return;
        }

        lock (_sync)
        {
            Stop();

            _samples = buffer.Samples;
            _channels = buffer.Channels;
            _sampleRate = buffer.SampleRate;
            _startFrame = Math.Clamp(startFrame, 0, buffer.FrameCount);
            _sampleOffset = _startFrame * _channels;

            var format = new AudioStreamBasicDescription
            {
                SampleRate = _sampleRate,
                FormatID = AudioFormatLinearPcm,
                FormatFlags = AudioFormatFlagIsFloat | AudioFormatFlagIsPacked,
                BytesPerPacket = (uint)(sizeof(float) * _channels),
                FramesPerPacket = 1,
                BytesPerFrame = (uint)(sizeof(float) * _channels),
                ChannelsPerFrame = (uint)_channels,
                BitsPerChannel = sizeof(float) * 8,
                Reserved = 0
            };

            _callback = HandleOutputBuffer;
            _selfHandle = GCHandle.Alloc(this);

            var status = AudioQueueNewOutput(
                ref format,
                _callback,
                GCHandle.ToIntPtr(_selfHandle),
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                out _audioQueue);

            if (status != 0 || _audioQueue == IntPtr.Zero)
            {
                CleanupResources();
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
                return;
            }

            var bufferByteSize = (uint)(FramesPerBuffer * sizeof(float) * _channels);
            _buffers = new IntPtr[BufferCount];
            var enqueuedAny = false;
            for (var i = 0; i < BufferCount; i++)
            {
                status = AudioQueueAllocateBuffer(_audioQueue, bufferByteSize, out var bufferRef);
                if (status != 0 || bufferRef == IntPtr.Zero)
                {
                    break;
                }

                _buffers[i] = bufferRef;
                if (FillAndEnqueue(bufferRef))
                {
                    enqueuedAny = true;
                }
            }

            if (!enqueuedAny)
            {
                Stop();
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
                return;
            }

            AudioQueueStart(_audioQueue, IntPtr.Zero);
        }
    }

    public void Stop()
    {
        StopInternal(raisePlaybackStopped: false);
    }

    public long GetPositionFrames()
    {
        lock (_sync)
        {
            if (_audioQueue == IntPtr.Zero)
            {
                return 0;
            }

            var status = AudioQueueGetCurrentTime(
                _audioQueue,
                IntPtr.Zero,
                out var timeStamp,
                out _);

            if (status != 0)
            {
                return _startFrame;
            }

            var played = (long)Math.Max(0, timeStamp.SampleTime);
            return _startFrame + played;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private static void HandleOutputBuffer(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is not MacAudioQueuePlayer player)
        {
            return;
        }

        player.HandleOutputBuffer(queue, buffer);
    }

    private void HandleOutputBuffer(IntPtr queue, IntPtr buffer)
    {
        var shouldStop = false;
        lock (_sync)
        {
            if (_audioQueue == IntPtr.Zero || _audioQueue != queue)
            {
                return;
            }

            if (!FillAndEnqueue(buffer))
            {
                shouldStop = true;
            }
        }

        if (shouldStop)
        {
            StopInternal(raisePlaybackStopped: true);
        }
    }

    private bool FillAndEnqueue(IntPtr bufferRef)
    {
        if (_samples is null || _audioQueue == IntPtr.Zero)
        {
            return false;
        }

        var buffer = Marshal.PtrToStructure<AudioQueueBuffer>(bufferRef);
        var maxSamples = (int)(buffer.AudioDataBytesCapacity / sizeof(float));
        if (maxSamples <= 0)
        {
            return false;
        }

        var remainingSamples = _samples.LongLength - _sampleOffset;
        var samplesToCopy = (int)Math.Clamp(remainingSamples, 0, maxSamples);
        if (samplesToCopy <= 0)
        {
            buffer.AudioDataByteSize = 0;
            Marshal.StructureToPtr(buffer, bufferRef, true);
            return false;
        }

        Marshal.Copy(_samples, (int)_sampleOffset, buffer.AudioData, samplesToCopy);
        buffer.AudioDataByteSize = (uint)(samplesToCopy * sizeof(float));
        Marshal.StructureToPtr(buffer, bufferRef, true);
        _sampleOffset += samplesToCopy;

        var status = AudioQueueEnqueueBuffer(_audioQueue, bufferRef, 0, IntPtr.Zero);
        return status == 0;
    }

    private void StopInternal(bool raisePlaybackStopped)
    {
        IntPtr queue;
        lock (_sync)
        {
            queue = _audioQueue;
            if (queue == IntPtr.Zero)
            {
                return;
            }

            _audioQueue = IntPtr.Zero;
        }

        AudioQueueStop(queue, true);
        AudioQueueDispose(queue, true);
        CleanupResources();

        if (raisePlaybackStopped)
        {
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CleanupResources()
    {
        _buffers = null;
        _samples = null;
        _sampleOffset = 0;
        _startFrame = 0;

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _callback = null;
    }

    private delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMPTETime
    {
        public short Subframes;          // SInt16 mSubframes
        public short SubframeDivisor;    // SInt16 mSubframeDivisor
        public uint Counter;             // UInt32 mCounter
        public uint Type;                // UInt32 mType
        public uint Flags;               // UInt32 mFlags
        public short Hours;              // SInt16 mHours
        public short Minutes;            // SInt16 mMinutes
        public short Seconds;            // SInt16 mSeconds
        public short Frames;             // SInt16 mFrames
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioTimeStamp
    {
        public double SampleTime;
        public ulong HostTime;
        public double RateScalar;
        public ulong WordClockTime;
        public SMPTETime SMPTETime;
        public uint Flags;
        public uint Reserved;
    }

    private const string AudioToolboxLibrary = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        AudioQueueOutputCallback callback,
        IntPtr userData,
        IntPtr runLoop,
        IntPtr runLoopMode,
        uint flags,
        out IntPtr audioQueue);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueAllocateBuffer(
        IntPtr audioQueue,
        uint bufferByteSize,
        out IntPtr buffer);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueEnqueueBuffer(
        IntPtr audioQueue,
        IntPtr buffer,
        uint numPacketDescs,
        IntPtr packetDescs);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueStart(
        IntPtr audioQueue,
        IntPtr startTime);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueStop(
        IntPtr audioQueue,
        bool immediate);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueDispose(
        IntPtr audioQueue,
        bool immediate);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueGetCurrentTime(
        IntPtr audioQueue,
        IntPtr timeline,
        out AudioTimeStamp timeStamp,
        out uint discontinuity);
}
