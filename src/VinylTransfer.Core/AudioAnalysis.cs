using System;
using System.Collections.Generic;
using System.Linq;

namespace VinylTransfer.Core;

public static class AudioAnalysis
{
    public static ManualModeSettings RecommendManualSettings(AudioBuffer buffer, out AnalysisSummary summary)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        var absSamples = GetAbsoluteSamples(buffer);
        var noiseFloor = EstimateNoiseFloor(absSamples);
        var clickThreshold = GetPercentile(absSamples, 0.995f);
        var popThreshold = GetPercentile(absSamples, 0.999f);

        if (popThreshold < clickThreshold)
        {
            popThreshold = Math.Min(1f, clickThreshold + 0.05f);
        }

        var clickIntensity = Math.Clamp(0.5f + (clickThreshold * 0.3f), 0.35f, 0.85f);
        var popIntensity = Math.Clamp(0.6f + (popThreshold * 0.25f), 0.45f, 0.9f);

        var noiseReduction = Math.Clamp((float)(noiseFloor * 8f), 0.2f, 0.8f);

        summary = new AnalysisSummary(
            EstimatedNoiseFloor: noiseFloor,
            ClickThreshold: clickThreshold,
            PopThreshold: popThreshold,
            ClickIntensity: clickIntensity,
            PopIntensity: popIntensity,
            NoiseReductionAmount: noiseReduction);

        return new ManualModeSettings(
            ClickThreshold: clickThreshold,
            ClickIntensity: clickIntensity,
            PopThreshold: popThreshold,
            PopIntensity: popIntensity,
            NoiseFloor: noiseFloor,
            NoiseReductionAmount: noiseReduction);
    }

    private static float[] GetAbsoluteSamples(AudioBuffer buffer)
    {
        var samples = buffer.Samples;
        if (samples.Length == 0)
        {
            return Array.Empty<float>();
        }

        var sampleCount = Math.Min(samples.Length, 100_000);
        var step = Math.Max(1, samples.Length / sampleCount);
        var values = new List<float>(sampleCount);

        for (var i = 0; i < samples.Length; i += step)
        {
            values.Add(MathF.Abs(samples[i]));
        }

        return values.ToArray();
    }

    private static float EstimateNoiseFloor(IReadOnlyList<float> absSamples)
    {
        if (absSamples.Count == 0)
        {
            return 0f;
        }

        double total = 0;
        for (var i = 0; i < absSamples.Count; i++)
        {
            total += absSamples[i];
        }

        return (float)(total / absSamples.Count);
    }

    private static float GetPercentile(float[] absSamples, float percentile)
    {
        if (absSamples.Length == 0)
        {
            return 0f;
        }

        Array.Sort(absSamples);
        var clamped = Math.Clamp(percentile, 0f, 1f);
        var index = (int)MathF.Floor((absSamples.Length - 1) * clamped);
        return absSamples[index];
    }
}

public sealed record AnalysisSummary(
    float EstimatedNoiseFloor,
    float ClickThreshold,
    float PopThreshold,
    float ClickIntensity,
    float PopIntensity,
    float NoiseReductionAmount
);
