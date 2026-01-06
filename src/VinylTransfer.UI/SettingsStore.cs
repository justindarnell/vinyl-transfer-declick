using System;
using System.IO;
using System.Text.Json;

namespace VinylTransfer.UI;

public sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VinylTransfer.DeClick");
        _settingsPath = Path.Combine(root, "settings.json");
    }

    public SettingsData Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new SettingsData();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
        }
        catch (IOException)
        {
            return new SettingsData();
        }
        catch (JsonException)
        {
            return new SettingsData();
        }
    }

    public void Save(SettingsData data)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (IOException)
        {
            // Ignore persistence errors to avoid breaking UI interactions.
        }
    }
}

public sealed record SettingsData
{
    public double ClickThreshold { get; init; } = 0.4;
    public double ClickIntensity { get; init; } = 0.6;
    public double PopThreshold { get; init; } = 0.35;
    public double PopIntensity { get; init; } = 0.5;
    public double NoiseFloorDb { get; init; } = -60;
    public double NoiseReductionAmount { get; init; } = 0.5;
    public bool UseMedianRepair { get; init; } = true;
    public bool UseSpectralNoiseReduction { get; init; } = true;
    public bool UseMultiBandTransientDetection { get; init; } = true;
    public bool UseDecrackle { get; init; } = true;
    public double DecrackleIntensity { get; init; } = 0.35;
    public bool UseBandLimitedInterpolation { get; init; } = true;
    public bool ShowEventOverlay { get; init; } = true;
    public bool ShowNoiseProfileOverlay { get; init; } = true;
}
