using System.Diagnostics;

namespace VinylTransfer.Core;

public sealed class DspAudioProcessor : IAudioProcessor
{
    // Click/pop sensitivity multipliers for auto mode
    private const float ClickSensitivityMultiplier = 8f;
    private const float PopSensitivityMultiplier = 12f;

    // Base intensity values for auto mode
    private const float BaseClickIntensity = 0.7f;
    private const float ClickIntensityRange = 0.3f;
    private const float BasePopIntensity = 0.8f;
    private const float PopIntensityRange = 0.2f;

    // Window sizes for neighbor blending during repair
    private const int PopRepairWindow = 3;
    private const int ClickRepairWindow = 1;

    // Percentile for noise floor estimation (25th percentile)
    private const double NoiseFloorPercentile = 0.25;

    public ProcessingResult Process(ProcessingRequest request)
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

        // Validate that the appropriate mode settings are not null
        if (request.Settings.UseAutoMode && request.Settings.AutoMode is null)
        {
            throw new ArgumentException("AutoMode settings are required when UseAutoMode is true.", nameof(request));
        }

        if (!request.Settings.UseAutoMode && request.Settings.ManualMode is null)
        {
            throw new ArgumentException("ManualMode settings are required when UseAutoMode is false.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        var input = request.Input;
        var settings = request.Settings;
        var samples = input.Samples;
        var processedSamples = new float[samples.Length];
        samples.CopyTo(processedSamples, 0);

        ApplyProcessing(processedSamples, input, settings, out var clicksDetected, out var popsDetected, out var noiseFloor);

        var difference = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            difference[i] = samples[i] - processedSamples[i];
        }

        stopwatch.Stop();

        var processed = new AudioBuffer(processedSamples, input.SampleRate, input.Channels);
        var diffBuffer = new AudioBuffer(difference, input.SampleRate, input.Channels);
        var resultDiagnostics = new ProcessingDiagnostics(stopwatch.Elapsed, clicksDetected, popsDetected, noiseFloor);

        return new ProcessingResult(processed, diffBuffer, resultDiagnostics);
    }

    private static void ApplyProcessing(
        float[] samples,
        AudioBuffer input,
        ProcessingSettings settings,
        out int clicksDetected,
        out int popsDetected,
        out float noiseFloor)
    {
        // Estimate noise floor from original samples
        noiseFloor = EstimateNoiseFloor(samples);

        // Calculate thresholds for click/pop detection
        var clickThreshold = settings.UseAutoMode
            ? noiseFloor * (1f + settings.AutoMode.ClickSensitivity * ClickSensitivityMultiplier)
            : settings.ManualMode.ClickThreshold;
        var popThreshold = settings.UseAutoMode
            ? noiseFloor * (1f + settings.AutoMode.PopSensitivity * PopSensitivityMultiplier)
            : settings.ManualMode.PopThreshold;

        var clickIntensity = settings.UseAutoMode 
            ? BaseClickIntensity + ClickIntensityRange * settings.AutoMode.ClickSensitivity 
            : settings.ManualMode.ClickIntensity;
        var popIntensity = settings.UseAutoMode 
            ? BasePopIntensity + PopIntensityRange * settings.AutoMode.PopSensitivity 
            : settings.ManualMode.PopIntensity;

        clicksDetected = 0;
        popsDetected = 0;

        // Create a copy of original samples for neighbor averaging
        var originalSamples = new float[samples.Length];
        samples.CopyTo(originalSamples, 0);

        // First pass: detect and repair clicks/pops using original samples for neighbor averaging
        var channels = input.Channels;
        for (var frame = 0; frame < input.FrameCount; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                var sample = originalSamples[index];
                var abs = MathF.Abs(sample);

                if (abs >= popThreshold)
                {
                    popsDetected++;
                    samples[index] = BlendWithNeighbors(originalSamples, frame, channel, channels, input.FrameCount, PopRepairWindow, popIntensity);
                }
                else if (abs >= clickThreshold)
                {
                    clicksDetected++;
                    samples[index] = BlendWithNeighbors(originalSamples, frame, channel, channels, input.FrameCount, ClickRepairWindow, clickIntensity);
                }
            }
        }

        // Second pass: apply noise reduction after click/pop correction
        var reductionAmount = settings.UseAutoMode ? settings.AutoMode.NoiseReductionAmount : settings.ManualMode.NoiseReductionAmount;
        if (reductionAmount > 0f)
        {
            ApplyNoiseReduction(samples, reductionAmount, noiseFloor);
        }
    }

    private static float EstimateNoiseFloor(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        // Use a percentile-based estimate of the absolute sample magnitudes
        // to reduce the influence of transient outliers (clicks/pops).
        var magnitudes = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            magnitudes[i] = MathF.Abs(samples[i]);
        }

        Array.Sort(magnitudes);

        // Choose a lower percentile (e.g., 25th) as the noise floor.
        // This approximates the background noise level while ignoring louder transients.
        var index = (int)Math.Floor(NoiseFloorPercentile * (magnitudes.Length - 1));
        if (index < 0)
        {
            index = 0;
        }
        else if (index >= magnitudes.Length)
        {
            index = magnitudes.Length - 1;
        }

        return magnitudes[index];
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
