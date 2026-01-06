using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using VinylTransfer.Core;

namespace VinylTransfer.Infrastructure;

public sealed class DetectedEventExportService
{
    public void Export(string path, AudioBuffer buffer, IReadOnlyList<DetectedEvent> detectedEvents)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Export path is required.", nameof(path));
        }

        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            WriteCsv(path, buffer, detectedEvents);
            return;
        }

        WriteJson(path, buffer, detectedEvents);
    }

    private static void WriteCsv(string path, AudioBuffer buffer, IReadOnlyList<DetectedEvent> detectedEvents)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Index,Timecode,Seconds,Frame,Type,Strength,SampleRate,Channels");

        var sampleRate = buffer.SampleRate;
        for (var i = 0; i < detectedEvents.Count; i++)
        {
            var detectedEvent = detectedEvents[i];
            var seconds = detectedEvent.Frame / (double)sampleRate;
            var timecode = TimeSpan.FromSeconds(seconds).ToString("m\\:ss\\.fff", CultureInfo.InvariantCulture);
            builder.AppendLine(string.Join(',',
                i,
                timecode,
                seconds.ToString("F6", CultureInfo.InvariantCulture),
                detectedEvent.Frame,
                detectedEvent.Type,
                detectedEvent.Strength.ToString("F6", CultureInfo.InvariantCulture),
                sampleRate,
                buffer.Channels));
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static void WriteJson(string path, AudioBuffer buffer, IReadOnlyList<DetectedEvent> detectedEvents)
    {
        var sampleRate = buffer.SampleRate;
        var payload = new
        {
            Metadata = new
            {
                SampleRate = sampleRate,
                Channels = buffer.Channels,
                FrameCount = buffer.FrameCount
            },
            Events = BuildEventPayload(sampleRate, detectedEvents)
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static IEnumerable<object> BuildEventPayload(int sampleRate, IReadOnlyList<DetectedEvent> detectedEvents)
    {
        for (var i = 0; i < detectedEvents.Count; i++)
        {
            var detectedEvent = detectedEvents[i];
            var seconds = detectedEvent.Frame / (double)sampleRate;
            yield return new
            {
                Index = i,
                TimeSeconds = seconds,
                Timecode = TimeSpan.FromSeconds(seconds).ToString("m\\:ss\\.fff", CultureInfo.InvariantCulture),
                detectedEvent.Frame,
                detectedEvent.Type,
                detectedEvent.Strength
            };
        }
    }
}
