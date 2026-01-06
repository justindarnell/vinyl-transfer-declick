namespace VinylTransfer.Core;

public sealed record ProcessingRequest(AudioBuffer Input, ProcessingSettings Settings);

public sealed record ProcessingResult(AudioBuffer Processed, AudioBuffer Difference, ProcessingDiagnostics Diagnostics, ProcessingArtifacts Artifacts);

public sealed record ProcessingDiagnostics(
    TimeSpan ProcessingTime,
    int ClicksDetected,
    int PopsDetected,
    int DecracklesDetected,
    int ResidualClicks,
    float EstimatedNoiseFloor,
    float SnrImprovementDb,
    float DeltaRms
);
