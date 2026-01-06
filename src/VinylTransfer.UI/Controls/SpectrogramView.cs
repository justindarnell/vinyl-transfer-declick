using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using VinylTransfer.Core;

namespace VinylTransfer.UI.Controls;

public sealed class SpectrogramView : Control
{
    public static readonly StyledProperty<AudioBuffer?> BufferProperty =
        AvaloniaProperty.Register<SpectrogramView, AudioBuffer?>(nameof(Buffer));
    public static readonly StyledProperty<double> ZoomFactorProperty =
        AvaloniaProperty.Register<SpectrogramView, double>(nameof(ZoomFactor), 1d);
    public static readonly StyledProperty<double> ViewOffsetProperty =
        AvaloniaProperty.Register<SpectrogramView, double>(nameof(ViewOffset), 0d);

    private WriteableBitmap? _bitmap;
    private Size _lastSize;
    private AudioBuffer? _lastBuffer;
    private double _lastZoom;
    private double _lastOffset;

    public AudioBuffer? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    public double ZoomFactor
    {
        get => GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    public double ViewOffset
    {
        get => GetValue(ViewOffsetProperty);
        set => SetValue(ViewOffsetProperty, value);
    }

    static SpectrogramView()
    {
        BufferProperty.Changed.AddClassHandler<SpectrogramView>((control, _) => control.InvalidateVisual());
        ZoomFactorProperty.Changed.AddClassHandler<SpectrogramView>((control, _) => control.InvalidateVisual());
        ViewOffsetProperty.Changed.AddClassHandler<SpectrogramView>((control, _) => control.InvalidateVisual());
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var buffer = Buffer;
        if (buffer is null || buffer.Samples.Length == 0)
        {
            DrawPlaceholder(context, bounds);
            return;
        }

        var needsRender = _bitmap is null ||
                          _lastSize != bounds.Size ||
                          !ReferenceEquals(_lastBuffer, buffer) ||
                          Math.Abs(_lastZoom - ZoomFactor) > 0.001 ||
                          Math.Abs(_lastOffset - ViewOffset) > 0.001;

        if (needsRender)
        {
            _bitmap = BuildSpectrogram(buffer, bounds.Size, ZoomFactor, ViewOffset);
            _lastSize = bounds.Size;
            _lastBuffer = buffer;
            _lastZoom = ZoomFactor;
            _lastOffset = ViewOffset;
        }

        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), bounds);
        }
    }

    private static void DrawPlaceholder(DrawingContext context, Rect bounds)
    {
        var text = new FormattedText(
            "Spectrogram will appear after import.",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            Brushes.Gray);

        var point = new Point(
            bounds.X + (bounds.Width - text.Width) / 2,
            bounds.Y + (bounds.Height - text.Height) / 2);

        context.DrawText(text, point);
    }

    private static WriteableBitmap BuildSpectrogram(AudioBuffer buffer, Size size, double zoomFactor, double viewOffset)
    {
        var width = Math.Max(1, (int)Math.Floor(size.Width));
        var height = Math.Max(1, (int)Math.Floor(size.Height));
        var frameCount = buffer.FrameCount;
        if (frameCount <= 0)
        {
            return new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888);
        }

        var zoom = Math.Max(1d, zoomFactor);
        var visibleFrames = Math.Max(1, (int)Math.Round(frameCount / zoom));
        var maxStart = Math.Max(0, frameCount - visibleFrames);
        var offset = Math.Clamp(viewOffset, 0d, 1d);
        var startFrame = (int)Math.Round(maxStart * offset);

        var fftSize = SelectFftSize(buffer.FrameCount, buffer.SampleRate);
        var mono = BuildMonoSamples(buffer);
        if (fftSize < 64 || mono.Length < fftSize)
        {
            return bitmap;
        }

        var window = BuildHannWindow(fftSize);
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888);

        using var framebuffer = bitmap.Lock();
        var stride = framebuffer.RowBytes;
        var pixelBuffer = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                pixelBuffer[rowOffset + (x * 4) + 3] = 255;
            }
        }

        var maxMagnitude = 1e-6f;
        var magnitudes = new float[width, height];
        for (var x = 0; x < width; x++)
        {
            var frameIndex = startFrame + (int)Math.Round((x / (double)width) * Math.Max(1, visibleFrames - 1));
            var maxSampleStart = Math.Max(0, mono.Length - fftSize);
            var sampleStart = Math.Clamp(frameIndex - fftSize / 2, 0, maxSampleStart);
            var spectrum = ComputeSpectrum(mono, sampleStart, fftSize, window);

            for (var y = 0; y < height; y++)
            {
                var bin = (int)Math.Round((1 - (y / (double)Math.Max(1, height - 1))) * (spectrum.Length - 1));
                var magnitude = spectrum[bin];
                magnitudes[x, y] = magnitude;
                if (magnitude > maxMagnitude)
                {
                    maxMagnitude = magnitude;
                }
            }
        }

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var magnitude = magnitudes[x, y];
                var intensity = (float)Math.Log10(1 + (9 * magnitude / maxMagnitude));
                var color = (byte)Math.Clamp(intensity * 255f, 0f, 255f);
                var rowOffset = y * stride;
                pixelBuffer[rowOffset + (x * 4)] = color;
                pixelBuffer[rowOffset + (x * 4) + 1] = (byte)Math.Clamp(color * 0.8f, 0f, 255f);
                pixelBuffer[rowOffset + (x * 4) + 2] = (byte)Math.Clamp(color * 0.6f, 0f, 255f);
                pixelBuffer[rowOffset + (x * 4) + 3] = 255;
            }
        }

        Marshal.Copy(pixelBuffer, 0, framebuffer.Address, pixelBuffer.Length);

        return bitmap;
    }

    private static int SelectFftSize(int frameCount, int sampleRate)
    {
        var target = sampleRate <= 24000 ? 512 : 1024;
        var fftSize = 1;
        while (fftSize * 2 <= Math.Min(target, frameCount))
        {
            fftSize *= 2;
        }

        return fftSize;
    }

    private static float[] BuildMonoSamples(AudioBuffer buffer)
    {
        var samples = buffer.Samples;
        var channels = Math.Max(1, buffer.Channels);
        var frames = buffer.FrameCount;
        var mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var baseIndex = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += samples[baseIndex + channel];
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    private static float[] ComputeSpectrum(float[] samples, int offset, int fftSize, float[] window)
    {
        var re = new float[fftSize];
        var im = new float[fftSize];

        for (var i = 0; i < fftSize; i++)
        {
            re[i] = samples[offset + i] * window[i];
            im[i] = 0f;
        }

        Fft(re, im);

        var bins = fftSize / 2;
        var magnitudes = new float[bins];
        for (var i = 0; i < bins; i++)
        {
            var magnitude = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
            magnitudes[i] = magnitude;
        }

        return magnitudes;
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

    private static void Fft(float[] re, float[] im)
    {
        var n = re.Length;
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
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wlenRe = Math.Cos(angle);
            var wlenIm = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                var wRe = 1.0;
                var wIm = 0.0;
                for (var j = 0; j < len / 2; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = re[i + j + len / 2] * wRe - im[i + j + len / 2] * wIm;
                    var vIm = re[i + j + len / 2] * wIm + im[i + j + len / 2] * wRe;
                    re[i + j] = (float)(uRe + vRe);
                    im[i + j] = (float)(uIm + vIm);
                    re[i + j + len / 2] = (float)(uRe - vRe);
                    im[i + j + len / 2] = (float)(uIm - vIm);
                    var nextWRe = wRe * wlenRe - wIm * wlenIm;
                    var nextWIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nextWRe;
                    wIm = nextWIm;
                }
            }
        }
    }
}
