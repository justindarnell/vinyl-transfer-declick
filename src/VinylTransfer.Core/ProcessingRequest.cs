namespace VinylTransfer.Core;

public sealed record ProcessingRequest(AudioBuffer Input, ProcessingSettings Settings);

public sealed record ProcessingResult(AudioBuffer Processed, AudioBuffer Difference, ProcessingDiagnostics Diagnostics);

public sealed record ProcessingDiagnostics(
    TimeSpan ProcessingTime,
    int ClicksDetected,
    int PopsDetected,
    float EstimatedNoiseFloor
);
