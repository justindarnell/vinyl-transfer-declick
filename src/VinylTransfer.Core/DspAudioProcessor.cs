using System.Diagnostics;
using System.Threading;

namespace VinylTransfer.Core;

public sealed class DspAudioProcessor : IAudioProcessor
{
    public ProcessingResult Process(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Input is null)
        {
            throw new ArgumentException("Input buffer is required.", nameof(request));
        }

        if (request.Settings is null)
        {
            throw new ArgumentException("Processing settings are required.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        var input = request.Input;
        var settings = request.Settings;
        var samples = input.Samples;
        var processedSamples = new float[samples.Length];
        samples.CopyTo(processedSamples, 0);

        progress?.Report(new ProcessingProgress(0.0, "Initializing DSP pipeline"));
        ApplyProcessing(
            processedSamples,
            input,
            settings,
            progress,
            cancellationToken,
            out var clicksDetected,
            out var popsDetected,
            out var noiseFloor);

        var difference = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            difference[i] = samples[i] - processedSamples[i];
        }

        stopwatch.Stop();

        var processed = new AudioBuffer(processedSamples, input.SampleRate, input.Channels);
        var diffBuffer = new AudioBuffer(difference, input.SampleRate, input.Channels);
        var resultDiagnostics = new ProcessingDiagnostics(stopwatch.Elapsed, clicksDetected, popsDetected, noiseFloor);

        progress?.Report(new ProcessingProgress(1.0, "Processing complete"));
        return new ProcessingResult(processed, diffBuffer, resultDiagnostics);
    }

    private static float ApplyProcessing(
        float[] samples,
        AudioBuffer input,
        ProcessingSettings settings,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken,
        out int clicksDetected,
        out int popsDetected,
        out float noiseFloor)
    {
        cancellationToken.ThrowIfCancellationRequested();
        noiseFloor = settings.UseAutoMode
            ? EstimateNoiseFloor(samples)
            : Math.Max(settings.ManualMode.NoiseFloor, 0f);
        progress?.Report(new ProcessingProgress(0.1, settings.UseAutoMode ? "Estimated noise floor" : "Applied manual noise floor"));

        var clickThreshold = settings.UseAutoMode
            ? noiseFloor * (1f + settings.AutoMode.ClickSensitivity * 8f)
            : settings.ManualMode.ClickThreshold;
        var popThreshold = settings.UseAutoMode
            ? noiseFloor * (1f + settings.AutoMode.PopSensitivity * 12f)
            : settings.ManualMode.PopThreshold;

        var clickIntensity = settings.UseAutoMode ? 0.7f + 0.3f * settings.AutoMode.ClickSensitivity : settings.ManualMode.ClickIntensity;
        var popIntensity = settings.UseAutoMode ? 0.8f + 0.2f * settings.AutoMode.PopSensitivity : settings.ManualMode.PopIntensity;

        var reductionAmount = settings.UseAutoMode ? settings.AutoMode.NoiseReductionAmount : settings.ManualMode.NoiseReductionAmount;
        if (reductionAmount > 0f)
        {
            ApplyNoiseReduction(samples, reductionAmount, noiseFloor);
            progress?.Report(new ProcessingProgress(0.2, "Applied noise reduction"));
        }

        clicksDetected = 0;
        popsDetected = 0;

        var channels = input.Channels;
        var frameCount = input.FrameCount;
        var progressInterval = Math.Max(1, frameCount / 60);
        var cancellationCheckInterval = Math.Max(1, frameCount / 10); // Check cancellation 10 times during processing
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (frame % cancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                var sample = samples[index];
                var abs = MathF.Abs(sample);

                if (abs >= popThreshold)
                {
                    popsDetected++;
                    samples[index] = BlendWithNeighbors(samples, frame, channel, channels, input.FrameCount, 3, popIntensity);
                }
                else if (abs >= clickThreshold)
                {
                    clicksDetected++;
                    samples[index] = BlendWithNeighbors(samples, frame, channel, channels, input.FrameCount, 1, clickIntensity);
                }
            }

            if (frame % progressInterval == 0 || frame == frameCount - 1)
            {
                var fraction = frameCount <= 1 ? 1.0 : frame / (double)(frameCount - 1);
                var percent = 0.2 + (0.75 * fraction);
                progress?.Report(new ProcessingProgress(percent, "Scanning for clicks/pops"));
            }
        }

        progress?.Report(new ProcessingProgress(0.95, "Finalizing output buffers"));
        return noiseFloor;
    }

    private static float EstimateNoiseFloor(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        double total = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            total += Math.Abs(samples[i]);
        }

        var mean = total / samples.Length;
        return (float)mean;
    }

    private static void ApplyNoiseReduction(float[] samples, float reductionAmount, float noiseFloor)
    {
        var reduction = Math.Clamp(reductionAmount, 0f, 1f);
        if (reduction <= 0f)
        {
            return;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            var abs = MathF.Abs(sample);
            if (abs < noiseFloor)
            {
                samples[i] = sample * (1f - reduction);
            }
        }
    }

    private static float BlendWithNeighbors(float[] samples, int frame, int channel, int channels, int frameCount, int window, float intensity)
    {
        var start = Math.Max(0, frame - window);
        var end = Math.Min(frameCount - 1, frame + window);

        if (start == end)
        {
            return samples[frame * channels + channel];
        }

        double total = 0;
        var count = 0;
        for (var i = start; i <= end; i++)
        {
            if (i == frame)
            {
                continue;
            }

            total += samples[i * channels + channel];
            count++;
        }

        if (count == 0)
        {
            return samples[frame * channels + channel];
        }

        var neighborAverage = (float)(total / count);
        var sample = samples[frame * channels + channel];
        var clampedIntensity = Math.Clamp(intensity, 0f, 1f);
        return (sample * (1f - clampedIntensity)) + (neighborAverage * clampedIntensity);
    }
}
