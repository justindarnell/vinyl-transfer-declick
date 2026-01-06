namespace VinylTransfer.Core;

public sealed record ProcessingSettings(AutoModeSettings AutoMode, ManualModeSettings ManualMode, bool UseAutoMode);

public sealed record AutoModeSettings(
    float ClickSensitivity,
    float PopSensitivity,
    float NoiseReductionAmount,
    bool UseMedianRepair,
    bool UseSpectralNoiseReduction,
    bool UseMultiBandTransientDetection
);

public sealed record ManualModeSettings(
    float ClickThreshold,
    float ClickIntensity,
    float PopThreshold,
    float PopIntensity,
    float NoiseFloor,
    float NoiseReductionAmount,
    bool UseMedianRepair,
    bool UseSpectralNoiseReduction,
    bool UseMultiBandTransientDetection
);
