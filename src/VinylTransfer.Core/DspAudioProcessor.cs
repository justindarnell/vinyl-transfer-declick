using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace VinylTransfer.Core;

public sealed class DspAudioProcessor : IAudioProcessor
{
    // DSP parameters for adaptive FFT window sizing
    // Target ~23ms window provides good time-frequency resolution trade-off:
    //  - Short enough to track rapid spectral changes (transients, modulation)
    //  - Long enough to provide adequate frequency resolution for noise profiling
    //  - Matches perceptual time constants for musical artifacts
    // This value is used consistently across spectral reduction and transient detection.
    private const double AdaptiveWindowTargetMs = 23.0;
    
    // Maximum attenuation depth for spectral noise reduction.
    // Limits how much a frequency bin's magnitude can be reduced (as a fraction of reductionAmount).
    // 0.6 means at most 60% of the requested reduction is applied, preserving some signal.
    private const float MaxAttenuationDepth = 0.6f;
    
    // Scaling factor for gentle flooring mode.
    // When enabled, reduces the effective reduction amount to avoid over-processing.
    // 0.6 provides a good balance between artifact reduction and noise suppression.
    private const float GentleFlooringScale = 0.6f;
    
    // Temporal smoothing factor for per-bin gain interpolation between frames.
    //  - 0.0  => no smoothing (gain follows the instantaneous estimate; can cause "musical noise").
    //  - 1.0  => no adaptation (gain remains at the initial value; effectively disables updating).
    //  0.85 was chosen empirically to balance artifact suppression and responsiveness:
    //  it slows rapid frame-to-frame gain changes enough to reduce musical noise while
    //  still allowing transients and changes in noise floor to be tracked audibly.
    //  If you change this, validate the impact with listening tests across a range of material.
    private const float TemporalSmoothingFactor = 0.85f;

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

        var diagnostics = ApplyProcessing(
            processedSamples,
            input,
            settings,
            out var clicksDetected,
            out var popsDetected,
            out var decracklesDetected,
            out var noiseFloor,
            out var detectedEvents,
            out var transientThresholdSummary);

        var difference = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            difference[i] = samples[i] - processedSamples[i];
        }

        stopwatch.Stop();

        var processed = new AudioBuffer(processedSamples, input.SampleRate, input.Channels);
        var diffBuffer = new AudioBuffer(difference, input.SampleRate, input.Channels);
        var residualClicks = CountResidualClicks(processedSamples, input, settings, noiseFloor);
        var deltaRms = ComputeRms(processedSamples) - ComputeRms(samples);
        var processingGainDb = ComputeProcessingGain(samples, difference);
        var resultDiagnostics = new ProcessingDiagnostics(
            stopwatch.Elapsed,
            clicksDetected,
            popsDetected,
            decracklesDetected,
            residualClicks,
            noiseFloor,
            processingGainDb,
            deltaRms,
            transientThresholdSummary);
        var artifacts = new ProcessingArtifacts(detectedEvents, AudioAnalysis.BuildNoiseProfile(input));

        return new ProcessingResult(processed, diffBuffer, resultDiagnostics, artifacts);
    }

    private static float ApplyProcessing(
        float[] samples,
        AudioBuffer input,
        ProcessingSettings settings,
        out int clicksDetected,
        out int popsDetected,
        out int decracklesDetected,
        out float noiseFloor,
        out List<DetectedEvent> detectedEvents,
        out string transientThresholdSummary)
    {
        var estimatedNoiseFloor = EstimateNoiseFloor(input);
        noiseFloor = settings.UseAutoMode ? estimatedNoiseFloor : settings.ManualMode.NoiseFloor;

        var clickThreshold = settings.UseAutoMode
            ? noiseFloor * (1f + settings.AutoMode.ClickSensitivity * 8f)
            : settings.ManualMode.ClickThreshold;
        var popThreshold = settings.UseAutoMode
            ? noiseFloor * (1f + settings.AutoMode.PopSensitivity * 12f)
            : settings.ManualMode.PopThreshold;

        var clickIntensity = settings.UseAutoMode ? 0.7f + 0.3f * settings.AutoMode.ClickSensitivity : settings.ManualMode.ClickIntensity;
        var popIntensity = settings.UseAutoMode ? 0.8f + 0.2f * settings.AutoMode.PopSensitivity : settings.ManualMode.PopIntensity;
        var useBandLimitedInterpolation = settings.UseAutoMode
            ? settings.AutoMode.UseBandLimitedInterpolation
            : settings.ManualMode.UseBandLimitedInterpolation;

        var useDecrackle = settings.UseAutoMode
            ? settings.AutoMode.UseDecrackle
            : settings.ManualMode.UseDecrackle;
        var decrackleIntensity = settings.UseAutoMode
            ? settings.AutoMode.DecrackleIntensity
            : settings.ManualMode.DecrackleIntensity;

        var reductionAmount = settings.UseAutoMode ? settings.AutoMode.NoiseReductionAmount : settings.ManualMode.NoiseReductionAmount;
        if (reductionAmount > 0f)
        {
            var useSpectralNoiseReduction = settings.UseAutoMode
                ? settings.AutoMode.UseSpectralNoiseReduction
                : settings.ManualMode.UseSpectralNoiseReduction;
            ApplySpectralNoiseReduction(
                samples,
                input.SampleRate,
                input.Channels,
                reductionAmount,
                applyGentleFlooring: useSpectralNoiseReduction);
        }

        var useMedianRepair = settings.UseAutoMode
            ? settings.AutoMode.UseMedianRepair
            : settings.ManualMode.UseMedianRepair;
        var useMultiBandTransientDetection = settings.UseAutoMode
            ? settings.AutoMode.UseMultiBandTransientDetection
            : settings.ManualMode.UseMultiBandTransientDetection;

        var transientFrames = useMultiBandTransientDetection
            ? DetectTransientFrames(samples, input.SampleRate, input.Channels, out transientThresholdSummary)
            : Array.Empty<bool>();
        if (!useMultiBandTransientDetection)
        {
            transientThresholdSummary = string.Empty;
        }

        clicksDetected = 0;
        popsDetected = 0;
        decracklesDetected = 0;
        detectedEvents = new List<DetectedEvent>();

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

                if (useDecrackle && abs >= noiseFloor * 1.8f && abs < clickThresholdAdjusted)
                {
                    if (IsImpulseLike(samples, frame, channel, channels, input.FrameCount, 2, 2.2f, 1.4f))
                    {
                        decracklesDetected++;
                        samples[index] = useBandLimitedInterpolation
                            ? BlendWithInterpolation(samples, frame, channel, channels, input.FrameCount, 6, decrackleIntensity)
                            : BlendWithNeighbors(samples, frame, channel, channels, input.FrameCount, 1, decrackleIntensity);
                        detectedEvents.Add(new DetectedEvent(frame, DetectedEventType.Decrackle, abs));
                    }
                }
                else if (abs >= popThresholdAdjusted)
                {
                    if (IsImpulseLike(samples, frame, channel, channels, input.FrameCount, 3, 2.5f, 1.2f))
                    {
                        popsDetected++;
                        samples[index] = useBandLimitedInterpolation
                            ? BlendWithInterpolation(samples, frame, channel, channels, input.FrameCount, 10, popIntensity)
                            : useMedianRepair
                                ? MedianWithNeighbors(samples, frame, channel, channels, input.FrameCount, 3)
                                : BlendWithNeighbors(samples, frame, channel, channels, input.FrameCount, 3, popIntensity);
                        detectedEvents.Add(new DetectedEvent(frame, DetectedEventType.Pop, abs));
                    }
                }
                else if (abs >= clickThresholdAdjusted)
                {
                    if (IsImpulseLike(samples, frame, channel, channels, input.FrameCount, 2, 2.3f, 1.4f))
                    {
                        clicksDetected++;
                        samples[index] = useBandLimitedInterpolation
                            ? BlendWithInterpolation(samples, frame, channel, channels, input.FrameCount, 6, clickIntensity)
                            : useMedianRepair
                                ? MedianWithNeighbors(samples, frame, channel, channels, input.FrameCount, 1)
                                : BlendWithNeighbors(samples, frame, channel, channels, input.FrameCount, 1, clickIntensity);
                        detectedEvents.Add(new DetectedEvent(frame, DetectedEventType.Click, abs));
                    }
                }
            }
        }

        return estimatedNoiseFloor;
    }

    private static bool IsImpulseLike(
        float[] samples,
        int frame,
        int channel,
        int channels,
        int frameCount,
        int window,
        float energyRatio,
        float hfRatio)
    {
        var start = Math.Max(0, frame - window);
        var end = Math.Min(frameCount - 1, frame + window);
        if (start == end)
        {
            return false;
        }

        double energy = 0;
        var count = 0;
        for (var i = start; i <= end; i++)
        {
            if (i == frame)
            {
                continue;
            }

            var sample = samples[i * channels + channel];
            energy += sample * sample;
            count++;
        }

        if (count == 0)
        {
            return false;
        }

        var localRms = MathF.Sqrt((float)(energy / count));
        var centerSample = samples[frame * channels + channel];
        if (localRms <= 1e-6f)
        {
            return MathF.Abs(centerSample) > 0.001f;
        }

        var centerAbs = MathF.Abs(centerSample);
        var prevIndex = Math.Max(frame - 1, 0) * channels + channel;
        var nextIndex = Math.Min(frame + 1, frameCount - 1) * channels + channel;
        var hfEmphasis = MathF.Abs(2f * centerSample - samples[prevIndex] - samples[nextIndex]);
        return centerAbs > localRms * energyRatio && hfEmphasis > localRms * hfRatio;
    }

    private static float BlendWithInterpolation(
        float[] samples,
        int frame,
        int channel,
        int channels,
        int frameCount,
        int radius,
        float intensity)
    {
        var original = samples[frame * channels + channel];
        var interpolated = BandLimitedInterpolate(samples, frame, channel, channels, frameCount, radius);
        var clampedIntensity = Math.Clamp(intensity, 0f, 1f);
        return (original * (1f - clampedIntensity)) + (interpolated * clampedIntensity);
    }

    private static float BandLimitedInterpolate(
        float[] samples,
        int frame,
        int channel,
        int channels,
        int frameCount,
        int radius)
    {
        const float Cutoff = 0.45f;
        var start = Math.Max(0, frame - radius);
        var end = Math.Min(frameCount - 1, frame + radius);
        if (start == end)
        {
            return samples[frame * channels + channel];
        }

        double sum = 0;
        double weightSum = 0;
        for (var i = start; i <= end; i++)
        {
            if (i == frame)
            {
                continue;
            }

            var offset = i - frame;
            var distance = Math.Abs(offset);
            var sinc = distance == 0 ? 1.0 : Math.Sin(Math.PI * Cutoff * offset) / (Math.PI * Cutoff * offset);
            var window = 0.54 + 0.46 * Math.Cos(Math.PI * distance / radius);
            var weight = sinc * window;
            sum += samples[i * channels + channel] * weight;
            weightSum += weight;
        }

        if (Math.Abs(weightSum) < 1e-9)
        {
            return samples[frame * channels + channel];
        }

        return (float)(sum / weightSum);
    }

    private static int CountResidualClicks(float[] samples, AudioBuffer input, ProcessingSettings settings, float noiseFloor)
    {
        var clickThreshold = settings.UseAutoMode
            ? noiseFloor * (1f + settings.AutoMode.ClickSensitivity * 8f)
            : settings.ManualMode.ClickThreshold;
        var channels = input.Channels;
        var frameCount = input.FrameCount;
        var count = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var sample = samples[frame * channels + channel];
                if (MathF.Abs(sample) >= clickThreshold &&
                    IsImpulseLike(samples, frame, channel, channels, frameCount, 2, 2.1f, 1.2f))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static float ComputeRms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        double sumSq = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            sumSq += sample * sample;
        }

        return (float)Math.Sqrt(sumSq / samples.Length);
    }

    /// <summary>
    /// Computes the processing gain in dB, representing the ratio of input signal to removed content.
    /// This measures how much was removed relative to the original signal, not true SNR improvement.
    /// </summary>
    private static float ComputeProcessingGain(float[] inputSamples, float[] differenceSamples)
    {
        var inputRms = ComputeRms(inputSamples);
        var diffRms = ComputeRms(differenceSamples);
        if (diffRms <= 0f)
        {
            return 0f;
        }

        return (float)(20 * Math.Log10((inputRms + 1e-6f) / (diffRms + 1e-6f)));
    }

    private static float EstimateNoiseFloor(AudioBuffer buffer)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (buffer.Samples.Length == 0)
        {
            return 0f;
        }

        var segmentRms = ComputeSegmentRms(buffer);
        var quietSegments = segmentRms.OrderBy(x => x).Take(Math.Max(1, segmentRms.Count / 5)).ToArray();
        return quietSegments.Length == 0 ? 0f : quietSegments.Average();
    }

    internal static List<float> ComputeSegmentRms(AudioBuffer buffer)
    {
        var samples = buffer.Samples;
        var frames = buffer.FrameCount;
        var channels = buffer.Channels;
        var segmentFrames = Math.Max(buffer.SampleRate * 2, 1);
        var segmentCount = (frames + segmentFrames - 1) / segmentFrames;
        
        var segmentRms = new List<float>(segmentCount);
        if (segmentCount <= 0)
        {
            return segmentRms;
        }

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var startFrame = segment * segmentFrames;
            var endFrame = Math.Min(frames, startFrame + segmentFrames);
            double sumSq = 0;
            var totalSamples = 0;

            for (var frame = startFrame; frame < endFrame; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = samples[frame * channels + channel];
                    sumSq += sample * sample;
                    totalSamples++;
                }
            }

            var rms = totalSamples > 0 ? (float)Math.Sqrt(sumSq / totalSamples) : 0f;
            segmentRms.Add(rms);
        }

        return segmentRms;
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

    private static void ApplySpectralNoiseReduction(
        float[] samples,
        int sampleRate,
        int channels,
        float reductionAmount,
        bool applyGentleFlooring)
    {
        // Make frame size adaptive based on sample rate to maintain consistent time resolution.
        var targetFrameSize = (int)(sampleRate * AdaptiveWindowTargetMs / 1000.0);
        
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

        var effectiveReduction = applyGentleFlooring ? reduction * GentleFlooringScale : reduction;
        for (var channel = 0; channel < channels; channel++)
        {
            ApplySpectralNoiseReductionChannel(samples, channels, channel, frameSize, hopSize, effectiveReduction);
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

        // Persistent per-bin gain smoothing across all segments to maintain temporal continuity.
        var previousGain = new float[frameSize];
        Array.Fill(previousGain, 1f);
        
        var minGain = 1f - (MaxAttenuationDepth * reductionAmount);

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
                    var magnitude = (float)spectrum[i].Magnitude;
                    var noise = noiseProfile[i];
                    if (magnitude <= 0f)
                    {
                        continue;
                    }

                    var reduced = MathF.Max(magnitude - noise * reductionAmount, magnitude * minGain);
                    var targetGain = reduced / magnitude;
                    var smoothedGain = (TemporalSmoothingFactor * previousGain[i]) + ((1f - TemporalSmoothingFactor) * targetGain);
                    previousGain[i] = smoothedGain;
                    if (smoothedGain > 0f)
                    {
                        spectrum[i] *= smoothedGain;
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

    private static bool[] DetectTransientFrames(float[] samples, int sampleRate, int channels, out string transientThresholdSummary)
    {
        var targetFrameSize = (int)(sampleRate * AdaptiveWindowTargetMs / 1000.0);
        var frameSize = 1;
        while (frameSize < targetFrameSize)
        {
            frameSize <<= 1;
        }

        frameSize = Math.Clamp(frameSize, 512, 4096);
        var hopSize = frameSize / 2;
        var totalFrames = samples.Length / channels;
        var frameCount = (totalFrames - frameSize) / hopSize + 1;
        if (frameCount <= 0)
        {
            transientThresholdSummary = string.Empty;
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

        var flags = new bool[frameCount];
        var framesPerSegment = Math.Max((sampleRate * 2) / hopSize, 1);
        var segmentCount = (frameCount + framesPerSegment - 1) / framesPerSegment;
        var lowThresholds = new float[segmentCount];
        var midThresholds = new float[segmentCount];
        var highThresholds = new float[segmentCount];

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var startFrame = segment * framesPerSegment;
            var endFrame = Math.Min(frameCount, startFrame + framesPerSegment);
            var frameSpan = endFrame - startFrame;
            if (frameSpan <= 0)
            {
                continue;
            }

            var lowSlice = new float[frameSpan];
            var midSlice = new float[frameSpan];
            var highSlice = new float[frameSpan];
            for (var i = 0; i < frameSpan; i++)
            {
                var frameIndex = startFrame + i;
                lowSlice[i] = lowBand[frameIndex];
                midSlice[i] = midBand[frameIndex];
                highSlice[i] = highBand[frameIndex];
            }

            lowThresholds[segment] = Percentile(lowSlice, 0.95f);
            midThresholds[segment] = Percentile(midSlice, 0.95f);
            highThresholds[segment] = Percentile(highSlice, 0.95f);

            for (var frame = startFrame; frame < endFrame; frame++)
            {
                var isTransient = lowBand[frame] > lowThresholds[segment] ||
                                  midBand[frame] > midThresholds[segment] ||
                                  highBand[frame] > highThresholds[segment];
                flags[frame] = isTransient;
            }
        }

        transientThresholdSummary = BuildTransientThresholdSummary(
            framesPerSegment,
            lowThresholds,
            midThresholds,
            highThresholds);

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

    private static float Percentile(IReadOnlyList<float> values, float percentile)
    {
        if (values.Count == 0)
        {
            return 0f;
        }

        var clampedPercentile = Math.Clamp(percentile, 0f, 1f);
        var ordered = values.OrderBy(x => x).ToArray();
        var position = clampedPercentile * (ordered.Length - 1);
        var lowerIndex = (int)MathF.Floor(position);
        var upperIndex = (int)MathF.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return ordered[lowerIndex];
        }

        var weight = position - lowerIndex;
        return ordered[lowerIndex] * (1f - weight) + ordered[upperIndex] * weight;
    }

    private static string BuildTransientThresholdSummary(
        int framesPerSegment,
        IReadOnlyList<float> lowThresholds,
        IReadOnlyList<float> midThresholds,
        IReadOnlyList<float> highThresholds)
    {
        if (lowThresholds.Count == 0 || midThresholds.Count == 0 || highThresholds.Count == 0)
        {
            return string.Empty;
        }

        var lowStats = DescribeThresholds(lowThresholds);
        var midStats = DescribeThresholds(midThresholds);
        var highStats = DescribeThresholds(highThresholds);

        return $"Transient thresholds (95th percentile, segmentFrames={framesPerSegment}): " +
               $"low[min={lowStats.Min:0.###}, avg={lowStats.Avg:0.###}, max={lowStats.Max:0.###}], " +
               $"mid[min={midStats.Min:0.###}, avg={midStats.Avg:0.###}, max={midStats.Max:0.###}], " +
               $"high[min={highStats.Min:0.###}, avg={highStats.Avg:0.###}, max={highStats.Max:0.###}]";
    }

    private static (float Min, float Avg, float Max) DescribeThresholds(IReadOnlyList<float> values)
    {
        var min = values[0];
        var max = values[0];
        double sum = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            sum += value;
        }

        var avg = values.Count > 0 ? (float)(sum / values.Count) : 0f;
        return (min, avg, max);
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
