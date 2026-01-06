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

        var segmentSummary = AnalyzeSegments(buffer);
        var noiseFloor = segmentSummary.NoiseFloor;
        var clickThreshold = segmentSummary.ClickThreshold;
        var popThreshold = segmentSummary.PopThreshold;

        if (popThreshold < clickThreshold)
        {
            popThreshold = Math.Min(1f, clickThreshold + 0.05f);
        }

        var clickIntensity = Math.Clamp(0.5f + (clickThreshold * 0.3f), 0.35f, 0.85f);
        var popIntensity = Math.Clamp(0.6f + (popThreshold * 0.25f), 0.45f, 0.9f);

        var noiseReduction = EstimateNoiseReduction(noiseFloor, segmentSummary.SnrDb);

        summary = new AnalysisSummary(
            EstimatedNoiseFloor: noiseFloor,
            EstimatedSnrDb: segmentSummary.SnrDb,
            SegmentCount: segmentSummary.SegmentCount,
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
            NoiseReductionAmount: noiseReduction,
            UseMedianRepair: true,
            UseSpectralNoiseReduction: true,
            UseMultiBandTransientDetection: true,
            UseDecrackle: true,
            DecrackleIntensity: 0.4f,
            UseBandLimitedInterpolation: true);
    }

    private static float GetPercentile(float[] absSamples, float percentile)
    {
        if (absSamples.Length == 0)
        {
            return 0f;
        }

        var sorted = (float[])absSamples.Clone();
        Array.Sort(sorted);
        var clamped = Math.Clamp(percentile, 0f, 1f);
        var index = (int)MathF.Floor((sorted.Length - 1) * clamped);
        return sorted[index];
    }

    private static SegmentAnalysis AnalyzeSegments(AudioBuffer buffer)
    {
        var samples = buffer.Samples;
        if (samples.Length == 0)
        {
            return new SegmentAnalysis(0f, 0f, 0f, 0);
        }

        var frames = buffer.FrameCount;
        var channels = buffer.Channels;
        var sampleRate = buffer.SampleRate;
        var segmentFrames = Math.Max(sampleRate * 2, 1);
        var segmentCount = (frames + segmentFrames - 1) / segmentFrames;

        var clickThresholds = new List<float>(segmentCount);
        var popThresholds = new List<float>(segmentCount);
        var segmentRms = DspAudioProcessor.ComputeSegmentRms(buffer);

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var startFrame = segment * segmentFrames;
            var endFrame = Math.Min(frames, startFrame + segmentFrames);
            var absValues = new List<float>((endFrame - startFrame) / 4);

            for (var frame = startFrame; frame < endFrame; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = samples[frame * channels + channel];
                    // Process every 4th frame for efficiency. This subsampling provides
                    // a representative percentile estimate while reducing memory and CPU overhead.
                    // Note: Short-duration clicks between sampled frames may be missed.
                    if (frame % 4 == 0)
                    {
                        absValues.Add(MathF.Abs(sample));
                    }
                }
            }

            var absArray = absValues.ToArray();
            clickThresholds.Add(GetPercentile(absArray, 0.995f));
            popThresholds.Add(GetPercentile(absArray, 0.999f));
        }

        var clickThreshold = Median(clickThresholds);
        var popThreshold = Median(popThresholds);

        var quietSegments = segmentRms.OrderBy(x => x).Take(Math.Max(1, segmentRms.Count / 5)).ToArray();
        var noiseFloor = quietSegments.Length == 0 ? 0f : quietSegments.Average();

        var overallRms = segmentRms.Count == 0 ? 0f : segmentRms.Average();
        var snrDb = noiseFloor > 0f ? (float)(20 * Math.Log10(overallRms / noiseFloor)) : 0f;

        return new SegmentAnalysis(noiseFloor, clickThreshold, popThreshold, segmentCount)
        {
            SnrDb = snrDb
        };
    }

    public static NoiseProfile BuildNoiseProfile(AudioBuffer buffer)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        var segmentRms = DspAudioProcessor.ComputeSegmentRms(buffer);
        var segmentFrames = Math.Max(buffer.SampleRate * 2, 1);
        return new NoiseProfile(segmentRms, segmentFrames, buffer.SampleRate);
    }

    private static float EstimateNoiseReduction(float noiseFloor, float snrDb)
    {
        if (noiseFloor <= 0f)
        {
            return 0.2f;
        }

        var reduction = 1f - (snrDb / 40f);
        return Math.Clamp(reduction, 0.2f, 0.8f);
    }

    private static float Median(List<float> values)
    {
        if (values.Count == 0)
        {
            return 0f;
        }

        var count = values.Count;
        var sorted = new List<float>(values);
        sorted.Sort();
        var mid = count / 2;
        return count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2f
            : sorted[mid];
    }
}

public sealed record AnalysisSummary(
    float EstimatedNoiseFloor,
    float EstimatedSnrDb,
    int SegmentCount,
    float ClickThreshold,
    float PopThreshold,
    float ClickIntensity,
    float PopIntensity,
    float NoiseReductionAmount
);

public sealed record SegmentAnalysis(
    float NoiseFloor,
    float ClickThreshold,
    float PopThreshold,
    int SegmentCount)
{
    public float SnrDb { get; init; }
}
