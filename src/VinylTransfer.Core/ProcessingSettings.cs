namespace VinylTransfer.Core;

public sealed record ProcessingSettings(
    AutoModeSettings? AutoMode,
    ManualModeSettings? ManualMode,
    bool UseAutoMode
)
{
    public static ProcessingSettings ForAutoMode(AutoModeSettings autoMode) =>
        new(autoMode ?? throw new ArgumentNullException(nameof(autoMode)), null, true);

    public static ProcessingSettings ForManualMode(ManualModeSettings manualMode) =>
        new(null, manualMode ?? throw new ArgumentNullException(nameof(manualMode)), false);
}

public sealed record AutoModeSettings(
    float ClickSensitivity,
    float PopSensitivity,
    float NoiseReductionAmount
);

public sealed record ManualModeSettings(
    float ClickThreshold,
    float ClickIntensity,
    float PopThreshold,
    float PopIntensity,
    float NoiseFloor,
    float NoiseReductionAmount
);
