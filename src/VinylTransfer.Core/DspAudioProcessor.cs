using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace VinylTransfer.Core;

public sealed class DspAudioProcessor : IAudioProcessor
{
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

        var stopwatch = Stopwatch.StartNew();
        var input = request.Input;
        var settings = request.Settings;
        var samples = input.Samples;
        var processedSamples = new float[samples.Length];
        samples.CopyTo(processedSamples, 0);

        var diagnostics = ApplyProcessing(processedSamples, input, settings, out var clicksDetected, out var popsDetected, out var noiseFloor);

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

    private static float ApplyProcessing(
        float[] samples,
        AudioBuffer input,
        ProcessingSettings settings,
        out int clicksDetected,
        out int popsDetected,
        out float noiseFloor)
    {
        var estimatedNoiseFloor = EstimateNoiseFloor(samples);
        noiseFloor = settings.UseAutoMode ? estimatedNoiseFloor : settings.ManualMode.NoiseFloor;

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
            var useSpectralNoiseReduction = settings.UseAutoMode
                ? settings.AutoMode.UseSpectralNoiseReduction
                : settings.ManualMode.UseSpectralNoiseReduction;
            if (useSpectralNoiseReduction)
            {
                ApplySpectralNoiseReduction(samples, input.SampleRate, input.Channels, reductionAmount);
            }
            else
            {
                ApplyNoiseReduction(samples, reductionAmount, noiseFloor);
            }
        }

        var useMedianRepair = settings.UseAutoMode
            ? settings.AutoMode.UseMedianRepair
            : settings.ManualMode.UseMedianRepair;
        var useMultiBandTransientDetection = settings.UseAutoMode
            ? settings.AutoMode.UseMultiBandTransientDetection
            : settings.ManualMode.UseMultiBandTransientDetection;

        var transientFrames = useMultiBandTransientDetection
            ? DetectTransientFrames(samples, input.SampleRate, input.Channels)
            : Array.Empty<bool>();

        clicksDetected = 0;
        popsDetected = 0;

        var channels = input.Channels;
        for (var frame = 0; frame < input.FrameCount; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var index = frame * channels + channel;
                var sample = samples[index];
                var abs = MathF.Abs(sample);

                var clickThresholdAdjusted = clickThreshold;
                var popThresholdAdjusted = popThreshold;
                if (useMultiBandTransientDetection && transientFrames.Length > frame && transientFrames[frame])
                {
                    clickThresholdAdjusted *= 0.75f;
                    popThresholdAdjusted *= 0.85f;
                }

                if (abs >= popThresholdAdjusted)
                {
                    popsDetected++;
                    samples[index] = useMedianRepair
                        ? MedianWithNeighbors(samples, frame, channel, channels, input.FrameCount, 3)
                        : BlendWithNeighbors(samples, frame, channel, channels, input.FrameCount, 3, popIntensity);
                }
                else if (abs >= clickThresholdAdjusted)
                {
                    clicksDetected++;
                    samples[index] = useMedianRepair
                        ? MedianWithNeighbors(samples, frame, channel, channels, input.FrameCount, 1)
                        : BlendWithNeighbors(samples, frame, channel, channels, input.FrameCount, 1, clickIntensity);
                }
            }
        }

        return estimatedNoiseFloor;
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

    private static float MedianWithNeighbors(float[] samples, int frame, int channel, int channels, int frameCount, int window)
    {
        var start = Math.Max(0, frame - window);
        var end = Math.Min(frameCount - 1, frame + window);
        var maxSize = 2 * window + 1;

        // Use heap allocation for large windows to avoid stack overflow
        // Conservative threshold since this is called in a loop for each sample
        const int MaxStackAllocSize = 32;
        
        if (maxSize <= MaxStackAllocSize)
        {
            Span<float> values = stackalloc float[maxSize];
            return ComputeMedian(samples, frame, channel, channels, start, end, values);
        }
        else
        {
            var values = new float[maxSize];
            return ComputeMedian(samples, frame, channel, channels, start, end, values);
        }
    }

    private static float ComputeMedian(float[] samples, int frame, int channel, int channels, int start, int end, Span<float> values)
    {
        var count = 0;

        for (var i = start; i <= end; i++)
        {
            if (i == frame)
            {
                continue;
            }

            values[count++] = samples[i * channels + channel];
        }

        if (count == 0)
        {
            return samples[frame * channels + channel];
        }

        values.Slice(0, count).Sort();
        var mid = count / 2;
        return count % 2 == 0
            ? (values[mid - 1] + values[mid]) / 2f
            : values[mid];
    }

    private static void ApplySpectralNoiseReduction(float[] samples, int sampleRate, int channels, float reductionAmount)
    {
        // Make frame size adaptive based on sample rate to maintain consistent time resolution.
        // Target ~23ms window (1024 samples at 44.1kHz).
        var targetTimeMs = 23.0;
        var targetFrameSize = (int)(sampleRate * targetTimeMs / 1000.0);
        
        // Round to nearest power of 2 for FFT
        var frameSize = 1;
        while (frameSize < targetFrameSize)
        {
            frameSize <<= 1;
        }
        
        // Clamp to reasonable range
        frameSize = Math.Clamp(frameSize, 512, 8192);
        var hopSize = frameSize / 2;
        
        var reduction = Math.Clamp(reductionAmount, 0f, 1f);
        if (reduction <= 0f || samples.Length == 0)
        {
            return;
        }

        for (var channel = 0; channel < channels; channel++)
        {
            ApplySpectralNoiseReductionChannel(samples, channels, channel, frameSize, hopSize, reduction);
        }
    }

    private static void ApplySpectralNoiseReductionChannel(
        float[] samples,
        int channels,
        int channel,
        int frameSize,
        int hopSize,
        float reductionAmount)
    {
        var totalSamplesPerChannel = samples.Length / channels;
        if (totalSamplesPerChannel <= 0)
        {
            return;
        }

        var window = BuildHannWindow(frameSize);

        // Limit the maximum number of samples per channel processed in a single
        // segment to bound memory usage. This value can be tuned if needed.
        const int MaxSegmentSamplesPerChannel = 1_000_000;

        var segmentStart = 0;
        while (segmentStart < totalSamplesPerChannel)
        {
            var segmentLength = Math.Min(MaxSegmentSamplesPerChannel, totalSamplesPerChannel - segmentStart);

            var frameCount = (segmentLength - frameSize) / hopSize + 1;
            if (frameCount <= 0)
            {
                // Not enough samples in this segment for at least one frame.
                // Move to the next (if any) to avoid an infinite loop.
                segmentStart += segmentLength;
                continue;
            }

            var spectra = new Complex[frameCount][];
            var frameRms = new float[frameCount];
            var output = new float[segmentLength];
            var weight = new float[segmentLength];

            // Analysis: compute spectra and frame RMS for this segment.
            for (var frame = 0; frame < frameCount; frame++)
            {
                var offsetInChannel = segmentStart + frame * hopSize;
                var buffer = new Complex[frameSize];
                double sumSq = 0;
                
                // Calculate safe upper bound for this frame to avoid bounds check in inner loop
                var maxSampleOffset = Math.Min(frameSize, (samples.Length / channels) - offsetInChannel);
                
                for (var i = 0; i < maxSampleOffset; i++)
                {
                    var sampleIndex = (offsetInChannel + i) * channels + channel;
                    var sample = samples[sampleIndex];
                    var windowed = sample * window[i];
                    buffer[i] = new Complex(windowed, 0);
                    sumSq += sample * sample;
                }

                frameRms[frame] = (float)Math.Sqrt(sumSq / frameSize);
                FftUtility.Fft(buffer, invert: false);
                spectra[frame] = buffer;
            }

            var noiseProfile = EstimateNoiseProfile(spectra, frameRms);

            // Synthesis: apply noise reduction, inverse FFT, and overlap-add.
            for (var frame = 0; frame < frameCount; frame++)
            {
                var spectrum = spectra[frame];
                for (var i = 0; i < spectrum.Length; i++)
                {
                    var magnitude = spectrum[i].Magnitude;
                    var noise = noiseProfile[i];
                    var reduced = Math.Max(0, magnitude - noise * reductionAmount);
                    if (magnitude > 0)
                    {
                        spectrum[i] *= reduced / magnitude;
                    }
                }

                FftUtility.Fft(spectrum, invert: true);

                // Write back to output buffer with overlap-add
                var outputOffset = frame * hopSize;
                for (var i = 0; i < frameSize; i++)
                {
                    var outputIndex = outputOffset + i;
                    if (outputIndex < 0 || outputIndex >= output.Length)
                    {
                        continue;
                    }

                    var windowed = spectrum[i].Real * window[i];
                    output[outputIndex] += (float)windowed;
                    weight[outputIndex] += window[i];
                }
            }

            // Normalize by overlap-add weights and write back to the main samples array.
            for (var i = 0; i < segmentLength; i++)
            {
                var channelSampleIndex = segmentStart + i;
                var sampleIndex = channelSampleIndex * channels + channel;
                if (sampleIndex >= samples.Length)
                {
                    break;
                }
                
                var normalized = weight[i] > 0 ? output[i] / weight[i] : output[i];
                samples[sampleIndex] = normalized;
            }

            segmentStart += segmentLength;
        }
    }

    private static float[] EstimateNoiseProfile(Complex[][] spectra, float[] frameRms)
    {
        var frameCount = spectra.Length;
        var bins = spectra[0].Length;
        var indices = Enumerable.Range(0, frameCount).OrderBy(i => frameRms[i]).ToArray();
        var noiseFrames = Math.Max(1, frameCount / 5);
        var profile = new float[bins];

        for (var n = 0; n < noiseFrames; n++)
        {
            var spectrum = spectra[indices[n]];
            for (var bin = 0; bin < bins; bin++)
            {
                profile[bin] += (float)spectrum[bin].Magnitude;
            }
        }

        for (var bin = 0; bin < bins; bin++)
        {
            profile[bin] /= noiseFrames;
        }

        return profile;
    }

    private static float[] BuildHannWindow(int size)
    {
        var window = new float[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
        }

        return window;
    }

    private static bool[] DetectTransientFrames(float[] samples, int sampleRate, int channels)
    {
        var frameSize = 1024;
        var hopSize = 512;
        var totalFrames = samples.Length / channels;
        var frameCount = (totalFrames - frameSize) / hopSize + 1;
        if (frameCount <= 0)
        {
            return Array.Empty<bool>();
        }

        var window = BuildHannWindow(frameSize);
        var lowBand = new float[frameCount];
        var midBand = new float[frameCount];
        var highBand = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * hopSize;
            var buffer = new Complex[frameSize];
            for (var i = 0; i < frameSize; i++)
            {
                var sampleIndex = (offset + i) * channels;
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += samples[sampleIndex + channel];
                }

                var mono = sum / channels;
                buffer[i] = new Complex(mono * window[i], 0);
            }

            FftUtility.Fft(buffer, invert: false);

            var binFrequencyResolution = sampleRate / (float)frameSize;
            for (var bin = 0; bin < buffer.Length / 2; bin++)
            {
                var magnitude = buffer[bin].Magnitude;
                var frequency = bin * binFrequencyResolution;
                var power = (float)(magnitude * magnitude);
                // Frequency bands for transient detection:
                // Low: <2kHz (captures bass/drums/pops)
                // Mid: 2-6kHz (captures vocals/instruments)
                // High: >6kHz (captures clicks/surface noise)
                // These thresholds are optimized for typical vinyl recordings.
                if (frequency < 2000f)
                {
                    lowBand[frame] += power;
                }
                else if (frequency < 6000f)
                {
                    midBand[frame] += power;
                }
                else
                {
                    highBand[frame] += power;
                }
            }
        }

        var lowMedian = Median(lowBand);
        var midMedian = Median(midBand);
        var highMedian = Median(highBand);

        var flags = new bool[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var isTransient = lowBand[frame] > lowMedian * 5f ||
                              midBand[frame] > midMedian * 5f ||
                              highBand[frame] > highMedian * 6f;
            flags[frame] = isTransient;
        }

        var expanded = new bool[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (!flags[frame])
            {
                continue;
            }

            expanded[frame] = true;
            if (frame > 0)
            {
                expanded[frame - 1] = true;
            }

            if (frame + 1 < frameCount)
            {
                expanded[frame + 1] = true;
            }
        }

        // Expand transient flags from frame-level granularity to per-sample resolution.
        // Each frame's transient flag is applied to all samples in the corresponding hop.
        // This provides adequate precision for threshold adjustments while maintaining
        // computational efficiency of frame-level analysis.
        var perSampleFlags = new bool[totalFrames];
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (!expanded[frame])
            {
                continue;
            }

            var start = frame * hopSize;
            var end = Math.Min(totalFrames, start + hopSize);
            for (var i = start; i < end; i++)
            {
                perSampleFlags[i] = true;
            }
        }

        return perSampleFlags;
    }

    private static float Median(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
        {
            return 0f;
        }

        var ordered = values.OrderBy(x => x).ToArray();
        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[mid - 1] + ordered[mid]) / 2f
            : ordered[mid];
    }

    private static class FftUtility
    {
        public static void Fft(Complex[] buffer, bool invert)
        {
            var n = buffer.Length;
            
            // FFT requires buffer size to be a power of 2 for Cooley-Tukey radix-2 algorithm
            if (n == 0 || (n & (n - 1)) != 0)
            {
                throw new ArgumentException("Buffer size must be a power of 2.", nameof(buffer));
            }
            
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                while ((j & bit) != 0)
                {
                    j ^= bit;
                    bit >>= 1;
                }

                j ^= bit;

                if (i < j)
                {
                    (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
                }
            }

            for (var len = 2; len <= n; len <<= 1)
            {
                var angle = 2 * Math.PI / len * (invert ? -1 : 1);
                var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
                for (var i = 0; i < n; i += len)
                {
                    var w = Complex.One;
                    for (var j = 0; j < len / 2; j++)
                    {
                        var u = buffer[i + j];
                        var v = buffer[i + j + len / 2] * w;
                        buffer[i + j] = u + v;
                        buffer[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }

            if (invert)
            {
                for (var i = 0; i < n; i++)
                {
                    buffer[i] /= n;
                }
            }
        }
    }
}
