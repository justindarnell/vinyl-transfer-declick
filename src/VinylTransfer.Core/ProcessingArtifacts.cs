using System.Collections.Generic;

namespace VinylTransfer.Core;

public sealed record ProcessingArtifacts(
    IReadOnlyList<DetectedEvent> DetectedEvents,
    NoiseProfile? NoiseProfile
);

public sealed record DetectedEvent(
    int Frame,
    DetectedEventType Type,
    float Strength
);

public enum DetectedEventType
{
    Decrackle,
    Click,
    Pop
}

public sealed record NoiseProfile(
    IReadOnlyList<float> SegmentRms,
    int SegmentFrames,
    int SampleRate
);
