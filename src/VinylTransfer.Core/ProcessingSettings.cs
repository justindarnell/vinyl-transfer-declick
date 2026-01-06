namespace VinylTransfer.Core;

public sealed record ProcessingSettings(AutoModeSettings AutoMode, ManualModeSettings ManualMode, bool UseAutoMode);

public sealed record AutoModeSettings(
    float ClickSensitivity,
    float PopSensitivity,
    float NoiseReductionAmount,
    bool UseMedianRepair,
    bool UseSpectralNoiseReduction,
    float SpectralMaskingStrength,
    bool UseMultiBandTransientDetection,
    bool UseDecrackle,
    float DecrackleIntensity,
    bool UseBandLimitedInterpolation
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
    float SpectralMaskingStrength,
    bool UseMultiBandTransientDetection,
    bool UseDecrackle,
    float DecrackleIntensity,
    bool UseBandLimitedInterpolation
);
