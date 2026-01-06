using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VinylTransfer.Core;

namespace VinylTransfer.UI.Controls;

public sealed class WaveformView : Control
{
    public static readonly StyledProperty<AudioBuffer?> BufferProperty =
        AvaloniaProperty.Register<WaveformView, AudioBuffer?>(nameof(Buffer));

    public AudioBuffer? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    static WaveformView()
    {
        BufferProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
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

        var pen = new Pen(Brushes.DeepSkyBlue, 1);
        var midY = bounds.Height / 2;
        var halfHeight = bounds.Height / 2 - 2;
        var frameCount = buffer.FrameCount;
        if (frameCount == 0)
        {
            DrawPlaceholder(context, bounds);
            return;
        }

        var width = Math.Max(1, (int)Math.Floor(bounds.Width));
        var channels = Math.Max(1, buffer.Channels);
        var samples = buffer.Samples;

        for (var x = 0; x < width; x++)
        {
            var startFrame = (int)Math.Floor(frameCount * (x / (double)width));
            var endFrame = (int)Math.Floor(frameCount * ((x + 1) / (double)width)) - 1;
            if (endFrame < startFrame)
            {
                endFrame = startFrame;
            }

            if (startFrame >= frameCount)
            {
                break;
            }

            endFrame = Math.Min(frameCount - 1, endFrame);
            var maxAmplitude = 0f;

            for (var frame = startFrame; frame <= endFrame; frame++)
            {
                var sampleIndex = frame * channels;
                float sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += samples[sampleIndex + channel];
                }

                var avg = sum / channels;
                var abs = MathF.Abs(avg);
                if (abs > maxAmplitude)
                {
                    maxAmplitude = abs;
                }
            }

            var amplitude = Math.Clamp(maxAmplitude, 0f, 1f);
            var yOffset = amplitude * halfHeight;
            var xPos = bounds.X + x + 0.5;
            var yTop = bounds.Y + midY - yOffset;
            var yBottom = bounds.Y + midY + yOffset;

            context.DrawLine(pen, new Point(xPos, yTop), new Point(xPos, yBottom));
        }
    }

    private static void DrawPlaceholder(DrawingContext context, Rect bounds)
    {
        var text = new FormattedText(
            "Waveform will appear after import.",
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
}
