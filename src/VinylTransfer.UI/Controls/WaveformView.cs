using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VinylTransfer.Core;

namespace VinylTransfer.UI.Controls;

public sealed class WaveformView : Control
{
    private static readonly Pen WaveformPen = new(Brushes.DeepSkyBlue, 1);
    private static readonly Pen NoiseProfilePen = new(Brushes.LightGray, 1, dashStyle: new DashStyle(new[] { 4d, 4d }, 0));
    private static readonly Pen SelectedEventPen = new(Brushes.White, 2);
    
    public static readonly StyledProperty<AudioBuffer?> BufferProperty =
        AvaloniaProperty.Register<WaveformView, AudioBuffer?>(nameof(Buffer));
    public static readonly StyledProperty<IReadOnlyList<DetectedEvent>?> DetectedEventsProperty =
        AvaloniaProperty.Register<WaveformView, IReadOnlyList<DetectedEvent>?>(nameof(DetectedEvents));
    public static readonly StyledProperty<NoiseProfile?> NoiseProfileProperty =
        AvaloniaProperty.Register<WaveformView, NoiseProfile?>(nameof(NoiseProfile));
    public static readonly StyledProperty<bool> ShowEventOverlayProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowEventOverlay), true);
    public static readonly StyledProperty<bool> ShowNoiseProfileOverlayProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowNoiseProfileOverlay), true);
    public static readonly StyledProperty<bool> ShowClickMarkersProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowClickMarkers), true);
    public static readonly StyledProperty<bool> ShowPopMarkersProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowPopMarkers), true);
    public static readonly StyledProperty<bool> ShowDecrackleMarkersProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowDecrackleMarkers), true);
    public static readonly StyledProperty<double> EventOverlayOpacityProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(EventOverlayOpacity), 0.75d);
    public static readonly StyledProperty<double> NoiseProfileOpacityProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(NoiseProfileOpacity), 0.6d);
    public static readonly StyledProperty<int> SelectedEventIndexProperty =
        AvaloniaProperty.Register<WaveformView, int>(nameof(SelectedEventIndex), 0);
    public static readonly StyledProperty<double> ZoomFactorProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(ZoomFactor), 1d);
    public static readonly StyledProperty<double> ViewOffsetProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(ViewOffset), 0d);

    public AudioBuffer? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    public IReadOnlyList<DetectedEvent>? DetectedEvents
    {
        get => GetValue(DetectedEventsProperty);
        set => SetValue(DetectedEventsProperty, value);
    }

    public NoiseProfile? NoiseProfile
    {
        get => GetValue(NoiseProfileProperty);
        set => SetValue(NoiseProfileProperty, value);
    }

    public bool ShowEventOverlay
    {
        get => GetValue(ShowEventOverlayProperty);
        set => SetValue(ShowEventOverlayProperty, value);
    }

    public bool ShowNoiseProfileOverlay
    {
        get => GetValue(ShowNoiseProfileOverlayProperty);
        set => SetValue(ShowNoiseProfileOverlayProperty, value);
    }

    public bool ShowClickMarkers
    {
        get => GetValue(ShowClickMarkersProperty);
        set => SetValue(ShowClickMarkersProperty, value);
    }

    public bool ShowPopMarkers
    {
        get => GetValue(ShowPopMarkersProperty);
        set => SetValue(ShowPopMarkersProperty, value);
    }

    public bool ShowDecrackleMarkers
    {
        get => GetValue(ShowDecrackleMarkersProperty);
        set => SetValue(ShowDecrackleMarkersProperty, value);
    }

    public double EventOverlayOpacity
    {
        get => GetValue(EventOverlayOpacityProperty);
        set => SetValue(EventOverlayOpacityProperty, value);
    }

    public double NoiseProfileOpacity
    {
        get => GetValue(NoiseProfileOpacityProperty);
        set => SetValue(NoiseProfileOpacityProperty, value);
    }

    public int SelectedEventIndex
    {
        get => GetValue(SelectedEventIndexProperty);
        set => SetValue(SelectedEventIndexProperty, value);
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

    static WaveformView()
    {
        BufferProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        DetectedEventsProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        NoiseProfileProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        ShowEventOverlayProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        ShowNoiseProfileOverlayProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        ShowClickMarkersProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        ShowPopMarkersProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        ShowDecrackleMarkersProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        EventOverlayOpacityProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        NoiseProfileOpacityProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        SelectedEventIndexProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        ZoomFactorProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
        ViewOffsetProperty.Changed.AddClassHandler<WaveformView>((control, _) => control.InvalidateVisual());
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

        var pen = WaveformPen;
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
        var zoomFactor = Math.Max(1d, ZoomFactor);
        var visibleFrames = Math.Max(1, (int)Math.Round(frameCount / zoomFactor));
        var maxStart = Math.Max(0, frameCount - visibleFrames);
        var viewOffset = Math.Clamp(ViewOffset, 0d, 1d);
        var startFrameWindow = (int)Math.Round(maxStart * viewOffset);

        for (var x = 0; x < width; x++)
        {
            var startFrame = startFrameWindow + (int)Math.Floor(visibleFrames * (x / (double)width));
            var endFrame = startFrameWindow + (int)Math.Floor(visibleFrames * ((x + 1) / (double)width)) - 1;
            if (startFrame >= startFrameWindow + visibleFrames)
            {
                break;
            }

            startFrame = Math.Clamp(startFrame, 0, frameCount - 1);
            endFrame = Math.Clamp(endFrame, startFrame, frameCount - 1);
            var maxAmplitude = 0f;

            for (var frame = startFrame; frame <= endFrame; frame++)
            {
                var sampleIndex = frame * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = samples[sampleIndex + channel];
                    var absSample = MathF.Abs(sample);
                    if (absSample > maxAmplitude)
                    {
                        maxAmplitude = absSample;
                    }
                }
            }

            var amplitude = Math.Clamp(maxAmplitude, 0f, 1f);
            var yOffset = amplitude * halfHeight;
            var xPos = bounds.X + x + 0.5;
            var yTop = bounds.Y + midY - yOffset;
            var yBottom = bounds.Y + midY + yOffset;

            context.DrawLine(pen, new Point(xPos, yTop), new Point(xPos, yBottom));
        }

        if (ShowNoiseProfileOverlay)
        {
            DrawNoiseProfile(context, bounds, buffer, startFrameWindow, visibleFrames);
        }

        if (ShowEventOverlay)
        {
            DrawDetectedEvents(context, bounds, buffer, startFrameWindow, visibleFrames);
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

    private void DrawDetectedEvents(DrawingContext context, Rect bounds, AudioBuffer buffer, int startFrame, int visibleFrames)
    {
        var events = DetectedEvents;
        if (events is null || events.Count == 0)
        {
            return;
        }

        if (visibleFrames <= 0)
        {
            return;
        }

        var height = bounds.Height;
        var endFrame = startFrame + visibleFrames;
        var eventOpacity = Math.Clamp(EventOverlayOpacity, 0.1d, 1d);
        var clickPen = CreatePenWithOpacity(Colors.OrangeRed, eventOpacity);
        var popPen = CreatePenWithOpacity(Colors.MediumPurple, eventOpacity);
        var decracklePen = CreatePenWithOpacity(Colors.Gold, eventOpacity);
        var selectedFrame = SelectedEventIndex >= 0 && SelectedEventIndex < events.Count
            ? events[SelectedEventIndex].Frame
            : -1;
        foreach (var detectedEvent in events)
        {
            if (detectedEvent.Frame < startFrame || detectedEvent.Frame >= endFrame)
            {
                continue;
            }

            if (!ShouldRenderEvent(detectedEvent.Type))
            {
                continue;
            }

            var x = bounds.X + ((detectedEvent.Frame - startFrame) / (double)visibleFrames) * bounds.Width;
            var pen = detectedEvent.Type switch
            {
                DetectedEventType.Pop => popPen,
                DetectedEventType.Decrackle => decracklePen,
                _ => clickPen
            };
            context.DrawLine(pen, new Point(x, bounds.Y), new Point(x, bounds.Y + height));

            if (detectedEvent.Frame == selectedFrame)
            {
                context.DrawLine(SelectedEventPen, new Point(x, bounds.Y), new Point(x, bounds.Y + height));
            }
        }
    }

    private void DrawNoiseProfile(DrawingContext context, Rect bounds, AudioBuffer buffer, int startFrame, int visibleFrames)
    {
        var profile = NoiseProfile;
        if (profile is null || profile.SegmentRms.Count == 0)
        {
            return;
        }

        var maxRms = profile.SegmentRms.Max();
        if (maxRms <= 0f)
        {
            return;
        }

        var segmentCount = profile.SegmentRms.Count;
        var points = new List<Point>(segmentCount);
        var endFrame = startFrame + visibleFrames;
        for (var segment = 0; segment < segmentCount; segment++)
        {
            var segmentFrame = segment * profile.SegmentFrames;
            if (segmentFrame < startFrame || segmentFrame >= endFrame)
            {
                continue;
            }

            var x = bounds.X + ((segmentFrame - startFrame) / (double)visibleFrames) * bounds.Width;
            var normalized = profile.SegmentRms[segment] / maxRms;
            var y = bounds.Y + bounds.Height - (normalized * bounds.Height);
            points.Add(new Point(x, y));
        }

        for (var i = 1; i < points.Count; i++)
        {
            var noiseOpacity = Math.Clamp(NoiseProfileOpacity, 0.1d, 1d);
            var pen = CreatePenWithOpacity(Colors.LightGray, noiseOpacity, dashed: true);
            context.DrawLine(pen, points[i - 1], points[i]);
        }
    }

    private bool ShouldRenderEvent(DetectedEventType type)
    {
        return type switch
        {
            DetectedEventType.Pop => ShowPopMarkers,
            DetectedEventType.Decrackle => ShowDecrackleMarkers,
            _ => ShowClickMarkers
        };
    }

    private static Pen CreatePenWithOpacity(Color color, double opacity, bool dashed = false)
    {
        var alpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        return dashed
            ? new Pen(brush, 1, dashStyle: new DashStyle(new[] { 4d, 4d }, 0))
            : new Pen(brush, 1);
    }
}
