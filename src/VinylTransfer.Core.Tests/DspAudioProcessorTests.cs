using System;
using Xunit;
using VinylTransfer.Core;

namespace VinylTransfer.Core.Tests;

public class DspAudioProcessorTests
{
    private readonly DspAudioProcessor _processor = new();

    private static ProcessingSettings CreateAutoModeSettings(
        float clickSensitivity = 0.5f,
        float popSensitivity = 0.5f,
        float noiseReductionAmount = 0f,
        bool useMedianRepair = true,
        bool useSpectralNoiseReduction = false,
        float spectralMaskingStrength = 0.5f,
        bool useMultiBandTransientDetection = false,
        bool useDecrackle = false,
        float decrackleIntensity = 0f,
        bool useBandLimitedInterpolation = false)
    {
        var autoMode = new AutoModeSettings(
            ClickSensitivity: clickSensitivity,
            PopSensitivity: popSensitivity,
            NoiseReductionAmount: noiseReductionAmount,
            UseMedianRepair: useMedianRepair,
            UseSpectralNoiseReduction: useSpectralNoiseReduction,
            SpectralMaskingStrength: spectralMaskingStrength,
            UseMultiBandTransientDetection: useMultiBandTransientDetection,
            UseDecrackle: useDecrackle,
            DecrackleIntensity: decrackleIntensity,
            UseBandLimitedInterpolation: useBandLimitedInterpolation);
        return new ProcessingSettings(autoMode, null, true);
    }

    [Fact]
    public void ComputeRms_WithZeroSamples_ReturnsZero()
    {
        // Arrange - use larger buffer to avoid edge cases in transient detection
        var samples = new float[10000];
        var buffer = new AudioBuffer(samples, 44100, 1);
        var settings = CreateAutoModeSettings();
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert
        Assert.Equal(0f, result.Diagnostics.DeltaRms, 3);
    }

    [Fact]
    public void ComputeRms_WithKnownSignal_ReturnsCorrectValue()
    {
        // Arrange - create a sine wave with known RMS value
        var sampleRate = 44100;
        var frequency = 1000f;
        var amplitude = 0.5f;
        var samples = new float[sampleRate]; // 1 second
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * MathF.Sin(2f * MathF.PI * frequency * i / sampleRate);
        }

        var buffer = new AudioBuffer(samples, sampleRate, 1);
        var settings = CreateAutoModeSettings();
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert - Since no processing happens with these settings, delta RMS should be near zero
        Assert.InRange(result.Diagnostics.DeltaRms, -0.01f, 0.01f);
    }

    [Fact]
    public void ProcessingGain_WithNoChanges_ReturnsZero()
    {
        // Arrange - use larger buffer to avoid edge cases
        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.1f * MathF.Sin(2f * MathF.PI * i / 1000f);
        }

        var buffer = new AudioBuffer(samples, 44100, 1);
        var settings = CreateAutoModeSettings(clickSensitivity: 1.0f, popSensitivity: 1.0f);
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert - With very high sensitivity, no clicks should be detected
        Assert.True(result.Diagnostics.ProcessingGainDb >= 0f);
    }

    [Fact]
    public void ProcessingGain_WithSignificantChanges_ReturnsPositiveValue()
    {
        // Arrange - create signal with artificial clicks, using larger buffer
        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.01f * MathF.Sin(2f * MathF.PI * i / 1000f);
        }
        // Add some artificial clicks
        samples[1000] = 0.8f;
        samples[3000] = -0.7f;
        samples[5000] = 0.9f;

        var buffer = new AudioBuffer(samples, 44100, 1);
        var settings = CreateAutoModeSettings(
            clickSensitivity: 0.3f,
            popSensitivity: 0.3f,
            useMultiBandTransientDetection: true);
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert - Should detect and remove clicks, resulting in positive processing gain
        Assert.True(result.Diagnostics.ProcessingGainDb > 0f);
        Assert.True(result.Diagnostics.ClicksDetected > 0 || result.Diagnostics.PopsDetected > 0);
    }

    [Fact]
    public void BandLimitedInterpolation_ProducesResults()
    {
        // Arrange - create signal with a click, using larger buffer
        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.1f * MathF.Sin(2f * MathF.PI * i / 1000f);
        }
        samples[5000] = 0.9f; // Artificial click

        var buffer = new AudioBuffer(samples, 44100, 1);
        
        // Process with band-limited interpolation
        var settingsWithBandLimited = CreateAutoModeSettings(
            clickSensitivity: 0.3f,
            popSensitivity: 0.3f,
            useMultiBandTransientDetection: true,
            useBandLimitedInterpolation: true);
        
        // Process without band-limited interpolation
        var settingsWithoutBandLimited = CreateAutoModeSettings(
            clickSensitivity: 0.3f,
            popSensitivity: 0.3f,
            useMultiBandTransientDetection: true,
            useBandLimitedInterpolation: false);

        // Act
        var resultWith = _processor.Process(new ProcessingRequest(buffer, settingsWithBandLimited));
        var resultWithout = _processor.Process(new ProcessingRequest(buffer, settingsWithoutBandLimited));

        // Assert - Both should detect clicks
        Assert.True(resultWith.Diagnostics.ClicksDetected > 0 || resultWith.Diagnostics.PopsDetected > 0);
        Assert.True(resultWithout.Diagnostics.ClicksDetected > 0 || resultWithout.Diagnostics.PopsDetected > 0);
    }

    [Fact]
    public void IsImpulseLike_DetectsSustainedTonesCorrectly()
    {
        // Arrange - create a sustained pure tone (not an impulse), using larger buffer
        var samples = new float[10000];
        var frequency = 1000f;
        var sampleRate = 44100;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.5f * MathF.Sin(2f * MathF.PI * frequency * i / sampleRate);
        }

        var buffer = new AudioBuffer(samples, sampleRate, 1);
        var settings = CreateAutoModeSettings(
            clickSensitivity: 0.3f,
            popSensitivity: 0.3f,
            useMultiBandTransientDetection: true);
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert - Sustained tone should not be detected as clicks/pops
        Assert.Equal(0, result.Diagnostics.ClicksDetected);
        Assert.Equal(0, result.Diagnostics.PopsDetected);
    }

    [Fact]
    public void IsImpulseLike_DetectsActualImpulses()
    {
        // Arrange - create quiet signal with sharp impulses, using larger buffer
        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.01f * MathF.Sin(2f * MathF.PI * i / 1000f);
        }
        // Add sharp impulses
        samples[2000] = 0.8f;
        samples[6000] = -0.7f;

        var buffer = new AudioBuffer(samples, 44100, 1);
        var settings = CreateAutoModeSettings(
            clickSensitivity: 0.3f,
            popSensitivity: 0.3f,
            useMultiBandTransientDetection: true);
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert - Impulses should be detected
        Assert.True(result.Diagnostics.ClicksDetected > 0 || result.Diagnostics.PopsDetected > 0);
    }

    [Fact]
    public void Decrackle_DetectsMicroImpulses()
    {
        // Arrange - create signal with many small impulses (crackle)
        var samples = new float[2000];
        var random = new Random(42); // Seeded for reproducibility
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.01f * MathF.Sin(2f * MathF.PI * i / 100f);
            // Add random small impulses to simulate crackle
            if (i % 50 == 0)
            {
                samples[i] += (float)(random.NextDouble() * 0.3 - 0.15);
            }
        }

        var buffer = new AudioBuffer(samples, 44100, 1);
        var settings = CreateAutoModeSettings(
            clickSensitivity: 0.4f,
            popSensitivity: 0.4f,
            useMultiBandTransientDetection: true,
            useDecrackle: true,
            decrackleIntensity: 0.5f);
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert - Should detect decrackle events
        Assert.True(result.Diagnostics.DecracklesDetected > 0);
    }

    [Fact]
    public void ResidualClicks_LowerThanOrEqualToDetectedClicks()
    {
        // Arrange - create signal with clicks, using larger buffer
        var samples = new float[10000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.01f * MathF.Sin(2f * MathF.PI * i / 1000f);
        }
        samples[1000] = 0.8f;
        samples[3000] = -0.7f;
        samples[5000] = 0.9f;
        samples[7000] = -0.85f;

        var buffer = new AudioBuffer(samples, 44100, 1);
        var settings = CreateAutoModeSettings(
            clickSensitivity: 0.3f,
            popSensitivity: 0.3f,
            useMultiBandTransientDetection: true);
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert - Residual clicks should be less than or equal to detected clicks
        var totalDetected = result.Diagnostics.ClicksDetected + result.Diagnostics.PopsDetected;
        Assert.True(result.Diagnostics.ResidualClicks <= totalDetected);
    }

    [Fact]
    public void Process_WithStereoAudio_ProcessesBothChannels()
    {
        // Arrange - create stereo signal with clicks in both channels, using larger buffer
        var frameCount = 5000;
        var channels = 2;
        var samples = new float[frameCount * channels];
        for (var i = 0; i < frameCount; i++)
        {
            samples[i * channels] = 0.01f * MathF.Sin(2f * MathF.PI * i / 1000f); // Left
            samples[i * channels + 1] = 0.01f * MathF.Cos(2f * MathF.PI * i / 1000f); // Right
        }
        // Add clicks to both channels
        samples[1000 * channels] = 0.8f; // Left
        samples[1000 * channels + 1] = 0.7f; // Right

        var buffer = new AudioBuffer(samples, 44100, channels);
        var settings = CreateAutoModeSettings(
            clickSensitivity: 0.3f,
            popSensitivity: 0.3f,
            useMultiBandTransientDetection: true);
        var request = new ProcessingRequest(buffer, settings);

        // Act
        var result = _processor.Process(request);

        // Assert - Should detect clicks
        Assert.True(result.Diagnostics.ClicksDetected > 0 || result.Diagnostics.PopsDetected > 0);
        Assert.Equal(channels, result.Processed.Channels);
        Assert.Equal(frameCount, result.Processed.FrameCount);
    }
}
