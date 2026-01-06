using System;
using System.IO;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using NAudio.Wave;
using ReactiveUI;
using VinylTransfer.Core;
using VinylTransfer.Infrastructure;
using VinylTransfer.UI;

namespace VinylTransfer.UI.ViewModels;

public sealed class MainWindowViewModel : ReactiveObject, IDisposable
{
    private static readonly WavFileService SharedWavFileService = new();
    private static readonly DspAudioProcessor SharedDspAudioProcessor = new();
    private static readonly SettingsStore SharedSettingsStore = new();

    private readonly WavFileService _wavFileService = SharedWavFileService;
    private readonly DspAudioProcessor _processor = SharedDspAudioProcessor;
    private readonly SettingsStore _settingsStore = SharedSettingsStore;
    private readonly CompositeDisposable _disposables = new();

    private AudioBuffer? _inputBuffer;
    private AudioBuffer? _processedBuffer;
    private AudioBuffer? _differenceBuffer;
    private IReadOnlyList<DetectedEvent> _detectedEvents = Array.Empty<DetectedEvent>();
    private NoiseProfile? _noiseProfile;
    private string? _loadedPath;
    private bool _isPreviewingProcessed;
    private bool _suppressSettingsSave;
    private WaveOutEvent? _outputDevice;
    private BufferedWaveProvider? _bufferedProvider;
    private bool _isPlaying;
    private long _playbackPosition;
    private EventHandler<StoppedEventArgs>? _playbackStoppedHandler;

    private string _statusMessage = "Status: Load a WAV file to begin. Diagnostics will appear here.";
    private double _clickThreshold = 0.4;
    private double _clickIntensity = 0.6;
    private double _popThreshold = 0.35;
    private double _popIntensity = 0.5;
    private double _noiseFloorDb = -60;
    private double _noiseReductionAmount = 0.5;
    private bool _useMedianRepair = true;
    private bool _useSpectralNoiseReduction = true;
    private bool _useMultiBandTransientDetection = true;
    private bool _useDecrackle = true;
    private double _decrackleIntensity = 0.35;
    private bool _useBandLimitedInterpolation = true;
    private bool _showEventOverlay = true;
    private bool _showNoiseProfileOverlay = true;
    private double _zoomFactor = 1;
    private double _viewOffset;
    private PresetDefinition? _selectedPreset;

    public MainWindowViewModel()
    {
        OpenFileInteraction = new Interaction<OpenFileDialog, string?>();
        SaveFileInteraction = new Interaction<SaveFileDialog, string?>();

        var canProcess = this.WhenAnyValue(vm => vm.InputBuffer).Select(buffer => buffer is not null);
        var canExportProcessed = this.WhenAnyValue(vm => vm.ProcessedBuffer).Select(buffer => buffer is not null);
        var canExportDifference = this.WhenAnyValue(vm => vm.DifferenceBuffer).Select(buffer => buffer is not null);
        var canPreview = this.WhenAnyValue(vm => vm.InputBuffer, vm => vm.ProcessedBuffer)
            .Select(tuple => tuple.Item1 is not null && tuple.Item2 is not null);

        ImportWavCommand = ReactiveCommand.CreateFromTask(ImportWavAsync);
        AutoCleanCommand = ReactiveCommand.CreateFromTask(ProcessAsync, canProcess);
        RecommendSettingsCommand = ReactiveCommand.CreateFromTask(RecommendSettingsAsync, canProcess);
        PreviewCommand = ReactiveCommand.Create(HandlePreview, canPreview);
        PlayPreviewCommand = ReactiveCommand.Create(HandlePlayPreview, canProcess);
        StopPreviewCommand = ReactiveCommand.Create(HandleStopPreview, this.WhenAnyValue(vm => vm.IsPlaying));
        ExportProcessedCommand = ReactiveCommand.CreateFromTask(ExportProcessedAsync, canExportProcessed);
        ExportDifferenceCommand = ReactiveCommand.CreateFromTask(ExportDifferenceAsync, canExportDifference);

        ImportWavCommand.ThrownExceptions.Merge(AutoCleanCommand.ThrownExceptions)
            .Merge(RecommendSettingsCommand.ThrownExceptions)
            .Merge(ExportProcessedCommand.ThrownExceptions)
            .Merge(ExportDifferenceCommand.ThrownExceptions)
            .Merge(PreviewCommand.ThrownExceptions)
            .Merge(PlayPreviewCommand.ThrownExceptions)
            .Merge(StopPreviewCommand.ThrownExceptions)
            .Subscribe(ex => StatusMessage = $"Status: {ex.Message}")
            .DisposeWith(_disposables);

        LoadSettings();
    }

    public Interaction<OpenFileDialog, string?> OpenFileInteraction { get; }

    public Interaction<SaveFileDialog, string?> SaveFileInteraction { get; }

    public ReactiveCommand<Unit, Unit> ImportWavCommand { get; }

    public ReactiveCommand<Unit, Unit> AutoCleanCommand { get; }

    public ReactiveCommand<Unit, Unit> RecommendSettingsCommand { get; }

    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }

    public ReactiveCommand<Unit, Unit> PlayPreviewCommand { get; }

    public ReactiveCommand<Unit, Unit> StopPreviewCommand { get; }

    public ReactiveCommand<Unit, Unit> ExportProcessedCommand { get; }

    public ReactiveCommand<Unit, Unit> ExportDifferenceCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public double ClickThreshold
    {
        get => _clickThreshold;
        set
        {
            this.RaiseAndSetIfChanged(ref _clickThreshold, value);
            SaveSettings();
        }
    }

    public double ClickIntensity
    {
        get => _clickIntensity;
        set
        {
            this.RaiseAndSetIfChanged(ref _clickIntensity, value);
            SaveSettings();
        }
    }

    public double PopThreshold
    {
        get => _popThreshold;
        set
        {
            this.RaiseAndSetIfChanged(ref _popThreshold, value);
            SaveSettings();
        }
    }

    public double PopIntensity
    {
        get => _popIntensity;
        set
        {
            this.RaiseAndSetIfChanged(ref _popIntensity, value);
            SaveSettings();
        }
    }

    public double NoiseFloorDb
    {
        get => _noiseFloorDb;
        set
        {
            this.RaiseAndSetIfChanged(ref _noiseFloorDb, value);
            SaveSettings();
        }
    }

    public double NoiseReductionAmount
    {
        get => _noiseReductionAmount;
        set
        {
            this.RaiseAndSetIfChanged(ref _noiseReductionAmount, value);
            SaveSettings();
        }
    }

    public bool UseMedianRepair
    {
        get => _useMedianRepair;
        set
        {
            this.RaiseAndSetIfChanged(ref _useMedianRepair, value);
            SaveSettings();
        }
    }

    public bool UseSpectralNoiseReduction
    {
        get => _useSpectralNoiseReduction;
        set
        {
            this.RaiseAndSetIfChanged(ref _useSpectralNoiseReduction, value);
            SaveSettings();
        }
    }

    public bool UseMultiBandTransientDetection
    {
        get => _useMultiBandTransientDetection;
        set
        {
            this.RaiseAndSetIfChanged(ref _useMultiBandTransientDetection, value);
            SaveSettings();
        }
    }

    public bool UseDecrackle
    {
        get => _useDecrackle;
        set
        {
            this.RaiseAndSetIfChanged(ref _useDecrackle, value);
            SaveSettings();
        }
    }

    public double DecrackleIntensity
    {
        get => _decrackleIntensity;
        set
        {
            this.RaiseAndSetIfChanged(ref _decrackleIntensity, value);
            SaveSettings();
        }
    }

    public bool UseBandLimitedInterpolation
    {
        get => _useBandLimitedInterpolation;
        set
        {
            this.RaiseAndSetIfChanged(ref _useBandLimitedInterpolation, value);
            SaveSettings();
        }
    }

    public bool ShowEventOverlay
    {
        get => _showEventOverlay;
        set
        {
            this.RaiseAndSetIfChanged(ref _showEventOverlay, value);
            SaveSettings();
        }
    }

    public bool ShowNoiseProfileOverlay
    {
        get => _showNoiseProfileOverlay;
        set
        {
            this.RaiseAndSetIfChanged(ref _showNoiseProfileOverlay, value);
            SaveSettings();
        }
    }

    public double ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            this.RaiseAndSetIfChanged(ref _zoomFactor, value);
            SaveSettings();
        }
    }

    public double ViewOffset
    {
        get => _viewOffset;
        set
        {
            this.RaiseAndSetIfChanged(ref _viewOffset, value);
            SaveSettings();
        }
    }

    public IReadOnlyList<DetectedEvent> DetectedEvents
    {
        get => _detectedEvents;
        private set => this.RaiseAndSetIfChanged(ref _detectedEvents, value);
    }

    public NoiseProfile? NoiseProfile
    {
        get => _noiseProfile;
        private set => this.RaiseAndSetIfChanged(ref _noiseProfile, value);
    }

    public IReadOnlyList<PresetDefinition> PresetOptions { get; } = PresetDefinition.DefaultPresets();

    public PresetDefinition? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPreset, value);
            if (value is not null)
            {
                ApplyPreset(value);
            }
        }
    }

    public AudioBuffer? DisplayBuffer
    {
        get
        {
            if (_isPreviewingProcessed && ProcessedBuffer is not null)
            {
                return ProcessedBuffer;
            }

            return InputBuffer ?? ProcessedBuffer;
        }
    }

    private AudioBuffer? InputBuffer
    {
        get => _inputBuffer;
        set
        {
            this.RaiseAndSetIfChanged(ref _inputBuffer, value);
            this.RaisePropertyChanged(nameof(DisplayBuffer));
        }
    }

    private AudioBuffer? ProcessedBuffer
    {
        get => _processedBuffer;
        set
        {
            this.RaiseAndSetIfChanged(ref _processedBuffer, value);
            this.RaisePropertyChanged(nameof(DisplayBuffer));
        }
    }

    private AudioBuffer? DifferenceBuffer
    {
        get => _differenceBuffer;
        set => this.RaiseAndSetIfChanged(ref _differenceBuffer, value);
    }

    private async Task ImportWavAsync()
    {
        StopPlayback(updateStatus: false);
        var dialog = new OpenFileDialog
        {
            AllowMultiple = false,
            Title = "Select WAV file",
            Filters =
            {
                new FileDialogFilter { Name = "WAV files", Extensions = { "wav" } },
                new FileDialogFilter { Name = "All files", Extensions = { "*" } }
            }
        };

        var path = await OpenFileInteraction.Handle(dialog);
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "Status: Import cancelled.";
            return;
        }

        StatusMessage = "Status: Loading WAV file...";
        var buffer = await Task.Run(() => _wavFileService.Read(path));
        InputBuffer = buffer;
        _loadedPath = path;
        ProcessedBuffer = null;
        DifferenceBuffer = null;
        DetectedEvents = Array.Empty<DetectedEvent>();
        NoiseProfile = AudioAnalysis.BuildNoiseProfile(buffer);
        _isPreviewingProcessed = false;

        StatusMessage = $"Status: Loaded {Path.GetFileName(path)} · {buffer.SampleRate} Hz · {buffer.Channels} ch · {FormatDuration(buffer)}.";
    }

    private async Task ProcessAsync()
    {
        if (InputBuffer is null)
        {
            StatusMessage = "Status: Load a WAV file first.";
            return;
        }

        StopPlayback(updateStatus: false);
        StatusMessage = "Status: Cleaning audio...";
        var settings = BuildProcessingSettings();
        var request = new ProcessingRequest(InputBuffer, settings);
        var result = await Task.Run(() => _processor.Process(request));

        ProcessedBuffer = result.Processed;
        DifferenceBuffer = result.Difference;
        DetectedEvents = result.Artifacts.DetectedEvents;
        NoiseProfile = result.Artifacts.NoiseProfile;
        _isPreviewingProcessed = true;
        this.RaisePropertyChanged(nameof(DisplayBuffer));

        StatusMessage = $"Status: Cleaned audio in {result.Diagnostics.ProcessingTime.TotalMilliseconds:F0} ms · " +
                        $"Clicks: {result.Diagnostics.ClicksDetected} · Pops: {result.Diagnostics.PopsDetected} · " +
                        $"Decrackle: {result.Diagnostics.DecracklesDetected} · Residual clicks: {result.Diagnostics.ResidualClicks} · " +
                        $"Processing gain: {result.Diagnostics.ProcessingGainDb:F1} dB · ΔRMS: {result.Diagnostics.DeltaRms:F4} · " +
                        $"Estimated noise floor: {result.Diagnostics.EstimatedNoiseFloor:F4}.";
    }

    private async Task RecommendSettingsAsync()
    {
        if (InputBuffer is null)
        {
            StatusMessage = "Status: Load a WAV file first.";
            return;
        }

        StatusMessage = "Status: Analyzing audio for recommendations...";
        var buffer = InputBuffer;
        var summary = await Task.Run(() =>
        {
            var settings = AudioAnalysis.RecommendManualSettings(buffer, out var analysis);
            return (settings, analysis);
        });

        ApplyRecommendedSettings(summary.settings);

        StatusMessage = $"Status: Recommended settings applied · " +
                        $"Noise floor: {ConvertAmplitudeToDb(summary.analysis.EstimatedNoiseFloor):F0} dB · " +
                        $"SNR: {summary.analysis.EstimatedSnrDb:F1} dB · " +
                        $"Click threshold: {summary.analysis.ClickThreshold:F2} · " +
                        $"Pop threshold: {summary.analysis.PopThreshold:F2}.";
    }

    private void HandlePreview()
    {
        if (InputBuffer is null || ProcessedBuffer is null)
        {
            StatusMessage = "Status: Import and process audio before previewing.";
            return;
        }

        var wasPlaying = _isPlaying;
        if (wasPlaying)
        {
            // Save current playback position before switching
            SavePlaybackPosition();
        }

        _isPreviewingProcessed = !_isPreviewingProcessed;
        this.RaisePropertyChanged(nameof(DisplayBuffer));
        var source = _isPreviewingProcessed ? "Processed" : "Original";
        StatusMessage = $"Status: Preview set to {source} audio.";

        if (wasPlaying)
        {
            // Resume playback from saved position
            StartPlayback(resumeFromPosition: true);
        }
    }

    private void HandlePlayPreview()
    {
        if (DisplayBuffer is null)
        {
            StatusMessage = "Status: Load audio before playing.";
            return;
        }

        StartPlayback();
    }

    private void HandleStopPreview()
    {
        StopPlayback(updateStatus: true);
    }

    private async Task ExportProcessedAsync()
    {
        if (ProcessedBuffer is null)
        {
            StatusMessage = "Status: No processed audio to export.";
            return;
        }

        var (directory, defaultName) = BuildDefaultExportName("cleaned");
        var dialog = new SaveFileDialog
        {
            Title = "Export processed WAV",
            InitialFileName = defaultName,
            Directory = directory,
            Filters =
            {
                new FileDialogFilter { Name = "WAV files", Extensions = { "wav" } },
                new FileDialogFilter { Name = "All files", Extensions = { "*" } }
            }
        };

        var path = await SaveFileInteraction.Handle(dialog);
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "Status: Export cancelled.";
            return;
        }

        StatusMessage = "Status: Exporting processed WAV...";
        await Task.Run(() => _wavFileService.Write(path, ProcessedBuffer));
        StatusMessage = $"Status: Exported processed WAV to {path}.";
    }

    private async Task ExportDifferenceAsync()
    {
        if (DifferenceBuffer is null)
        {
            StatusMessage = "Status: No difference audio to export.";
            return;
        }

        var (directory, defaultName) = BuildDefaultExportName("difference");
        var dialog = new SaveFileDialog
        {
            Title = "Export difference WAV",
            InitialFileName = defaultName,
            Directory = directory,
            Filters =
            {
                new FileDialogFilter { Name = "WAV files", Extensions = { "wav" } },
                new FileDialogFilter { Name = "All files", Extensions = { "*" } }
            }
        };

        var path = await SaveFileInteraction.Handle(dialog);
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "Status: Export cancelled.";
            return;
        }

        StatusMessage = "Status: Exporting difference WAV...";
        await Task.Run(() => _wavFileService.Write(path, DifferenceBuffer));
        StatusMessage = $"Status: Exported difference WAV to {path}.";
    }

    private ProcessingSettings BuildProcessingSettings()
    {
        var manualSettings = new ManualModeSettings(
            ClickThreshold: (float)ClickThreshold,
            ClickIntensity: (float)ClickIntensity,
            PopThreshold: (float)PopThreshold,
            PopIntensity: (float)PopIntensity,
            NoiseFloor: ConvertDbToAmplitude(NoiseFloorDb),
            NoiseReductionAmount: (float)NoiseReductionAmount,
            UseMedianRepair: UseMedianRepair,
            UseSpectralNoiseReduction: UseSpectralNoiseReduction,
            UseMultiBandTransientDetection: UseMultiBandTransientDetection,
            UseDecrackle: UseDecrackle,
            DecrackleIntensity: (float)DecrackleIntensity,
            UseBandLimitedInterpolation: UseBandLimitedInterpolation);

        var autoSettings = new AutoModeSettings(
            ClickSensitivity: (float)ClickThreshold,
            PopSensitivity: (float)PopThreshold,
            NoiseReductionAmount: (float)NoiseReductionAmount,
            UseMedianRepair: UseMedianRepair,
            UseSpectralNoiseReduction: UseSpectralNoiseReduction,
            UseMultiBandTransientDetection: UseMultiBandTransientDetection,
            UseDecrackle: UseDecrackle,
            DecrackleIntensity: (float)DecrackleIntensity,
            UseBandLimitedInterpolation: UseBandLimitedInterpolation);

        return new ProcessingSettings(autoSettings, manualSettings, UseAutoMode: false);
    }

    private static float ConvertDbToAmplitude(double decibels)
    {
        return MathF.Pow(10f, (float)decibels / 20f);
    }

    private static double ConvertAmplitudeToDb(float amplitude)
    {
        if (amplitude <= 0f)
        {
            return -90;
        }

        return 20 * Math.Log10(amplitude);
    }

    private static string FormatDuration(AudioBuffer buffer)
    {
        if (buffer.FrameCount == 0 || buffer.SampleRate == 0)
        {
            return "0:00";
        }

        var duration = TimeSpan.FromSeconds(buffer.FrameCount / (double)buffer.SampleRate);
        return duration.ToString(duration.TotalHours >= 1 ? "h\\:mm\\:ss" : "m\\:ss");
    }

    private (string? directory, string fileName) BuildDefaultExportName(string suffix)
    {
        if (!string.IsNullOrWhiteSpace(_loadedPath))
        {
            var name = Path.GetFileNameWithoutExtension(_loadedPath) ?? "export";
            var fileName = $"{name}-{suffix}.wav";
            var directory = Path.GetDirectoryName(_loadedPath);

            if (string.IsNullOrEmpty(directory))
            {
                return (null, fileName);
            }

            return (directory, fileName);
        }

        return (null, $"export-{suffix}.wav");
    }

    private void LoadSettings()
    {
        _suppressSettingsSave = true;
        var settings = _settingsStore.Load();
        ClickThreshold = settings.ClickThreshold;
        ClickIntensity = settings.ClickIntensity;
        PopThreshold = settings.PopThreshold;
        PopIntensity = settings.PopIntensity;
        NoiseFloorDb = settings.NoiseFloorDb;
        NoiseReductionAmount = settings.NoiseReductionAmount;
        UseMedianRepair = settings.UseMedianRepair;
        UseSpectralNoiseReduction = settings.UseSpectralNoiseReduction;
        UseMultiBandTransientDetection = settings.UseMultiBandTransientDetection;
        UseDecrackle = settings.UseDecrackle;
        DecrackleIntensity = settings.DecrackleIntensity;
        UseBandLimitedInterpolation = settings.UseBandLimitedInterpolation;
        ShowEventOverlay = settings.ShowEventOverlay;
        ShowNoiseProfileOverlay = settings.ShowNoiseProfileOverlay;
        ZoomFactor = settings.ZoomFactor;
        ViewOffset = settings.ViewOffset;
        _suppressSettingsSave = false;
    }

    private void SaveSettings()
    {
        if (_suppressSettingsSave)
        {
            return;
        }

        var data = new SettingsData
        {
            ClickThreshold = ClickThreshold,
            ClickIntensity = ClickIntensity,
            PopThreshold = PopThreshold,
            PopIntensity = PopIntensity,
            NoiseFloorDb = NoiseFloorDb,
            NoiseReductionAmount = NoiseReductionAmount,
            UseMedianRepair = UseMedianRepair,
            UseSpectralNoiseReduction = UseSpectralNoiseReduction,
            UseMultiBandTransientDetection = UseMultiBandTransientDetection,
            UseDecrackle = UseDecrackle,
            DecrackleIntensity = DecrackleIntensity,
            UseBandLimitedInterpolation = UseBandLimitedInterpolation,
            ShowEventOverlay = ShowEventOverlay,
            ShowNoiseProfileOverlay = ShowNoiseProfileOverlay,
            ZoomFactor = ZoomFactor,
            ViewOffset = ViewOffset
        };

        _ = Task.Run(() =>
        {
            try
            {
                _settingsStore.Save(data);
            }
            catch (Exception ex)
            {
                // Log error but don't block UI - settings save is not critical
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        });
    }

    private void ApplyRecommendedSettings(ManualModeSettings settings)
    {
        _suppressSettingsSave = true;
        ClickThreshold = settings.ClickThreshold;
        ClickIntensity = settings.ClickIntensity;
        PopThreshold = settings.PopThreshold;
        PopIntensity = settings.PopIntensity;
        NoiseFloorDb = ConvertAmplitudeToDb(settings.NoiseFloor);
        NoiseReductionAmount = settings.NoiseReductionAmount;
        UseMedianRepair = settings.UseMedianRepair;
        UseSpectralNoiseReduction = settings.UseSpectralNoiseReduction;
        UseMultiBandTransientDetection = settings.UseMultiBandTransientDetection;
        UseDecrackle = settings.UseDecrackle;
        DecrackleIntensity = settings.DecrackleIntensity;
        UseBandLimitedInterpolation = settings.UseBandLimitedInterpolation;
        _suppressSettingsSave = false;
        SaveSettings();
    }

    private void ApplyPreset(PresetDefinition preset)
    {
        _suppressSettingsSave = true;
        ClickThreshold = preset.ClickThreshold;
        ClickIntensity = preset.ClickIntensity;
        PopThreshold = preset.PopThreshold;
        PopIntensity = preset.PopIntensity;
        NoiseFloorDb = preset.NoiseFloorDb;
        NoiseReductionAmount = preset.NoiseReductionAmount;
        UseMedianRepair = preset.UseMedianRepair;
        UseSpectralNoiseReduction = preset.UseSpectralNoiseReduction;
        UseMultiBandTransientDetection = preset.UseMultiBandTransientDetection;
        UseDecrackle = preset.UseDecrackle;
        DecrackleIntensity = preset.DecrackleIntensity;
        UseBandLimitedInterpolation = preset.UseBandLimitedInterpolation;
        _suppressSettingsSave = false;
        SaveSettings();
        StatusMessage = $"Status: Applied preset '{preset.Name}'.";
    }

    private bool IsPlaying
    {
        get => _isPlaying;
        set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    private void StartPlayback(bool resumeFromPosition = false)
    {
        var buffer = DisplayBuffer;
        if (buffer is null)
        {
            return;
        }

        StopPlayback(updateStatus: false);

        if (!resumeFromPosition)
        {
            _playbackPosition = 0;
        }

        var format = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, buffer.Channels);
        _bufferedProvider = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true
        };

        // Stream audio in chunks to avoid allocating large byte arrays for entire buffer
        const int chunkSizeInSamples = 4096;
        long totalSamples = buffer.Samples.Length;
        int sampleSizeInBytes = sizeof(float);
        var chunkBytes = new byte[chunkSizeInSamples * sampleSizeInBytes];

        // Clamp playback position to valid range
        long sampleOffset = Math.Clamp(_playbackPosition, 0, totalSamples);
        
        while (sampleOffset < totalSamples)
        {
            int samplesThisChunk = (int)Math.Min(chunkSizeInSamples, totalSamples - sampleOffset);
            int bytesThisChunk = samplesThisChunk * sampleSizeInBytes;

            Buffer.BlockCopy(
                buffer.Samples,
                (int)(sampleOffset * sampleSizeInBytes),
                chunkBytes,
                0,
                bytesThisChunk);

            _bufferedProvider.AddSamples(chunkBytes, 0, bytesThisChunk);
            sampleOffset += samplesThisChunk;
        }

        _outputDevice = new WaveOutEvent();
        _playbackStoppedHandler = (_, _) =>
        {
            // Check if device is disposed before accessing
            if (_outputDevice is null)
            {
                return;
            }
            
            IsPlaying = false;
            StopPlayback(updateStatus: true, stopDevice: false);
        };
        _outputDevice.PlaybackStopped += _playbackStoppedHandler;
        _outputDevice.Init(_bufferedProvider);
        _outputDevice.Play();
        IsPlaying = true;
        StatusMessage = "Status: Playing preview audio.";
    }

    private void SavePlaybackPosition()
    {
        if (_bufferedProvider is null || _outputDevice is null)
        {
            _playbackPosition = 0;
            return;
        }

        // Calculate position based on the current time position
        // Note: This is an approximation since NAudio doesn't provide exact sample position
        var outputFormat = _outputDevice.OutputWaveFormat;
        if (outputFormat is null)
        {
            _playbackPosition = 0;
            return;
        }

        // GetPosition returns bytes played
        var bytesPlayed = _outputDevice.GetPosition();
        var bytesPerSample = sizeof(float) * outputFormat.Channels;
        var samplesPlayed = bytesPlayed / bytesPerSample;
        
        _playbackPosition = samplesPlayed;
    }

    private void StopPlayback(bool updateStatus, bool stopDevice = true)
    {
        if (_outputDevice is null)
        {
            IsPlaying = false;
            return;
        }

        if (_playbackStoppedHandler is not null)
        {
            _outputDevice.PlaybackStopped -= _playbackStoppedHandler;
            _playbackStoppedHandler = null;
        }
        
        if (stopDevice && _outputDevice.PlaybackState != NAudio.Wave.PlaybackState.Stopped)
        {
            try
            {
                _outputDevice.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Device already disposed, ignore
            }
        }
        
        _outputDevice.Dispose();
        _outputDevice = null;
        _bufferedProvider = null;
        IsPlaying = false;
        if (updateStatus)
        {
            StatusMessage = "Status: Playback stopped.";
        }
    }

    public void Dispose()
    {
        StopPlayback(updateStatus: false);
        _disposables.Dispose();
    }
}

public sealed record PresetDefinition(
    string Name,
    double ClickThreshold,
    double ClickIntensity,
    double PopThreshold,
    double PopIntensity,
    double NoiseFloorDb,
    double NoiseReductionAmount,
    bool UseMedianRepair,
    bool UseSpectralNoiseReduction,
    bool UseMultiBandTransientDetection,
    bool UseDecrackle,
    double DecrackleIntensity,
    bool UseBandLimitedInterpolation)
{
    public static IReadOnlyList<PresetDefinition> DefaultPresets()
    {
        return new[]
        {
            new PresetDefinition(
                "Gentle Cleanup",
                ClickThreshold: 0.45,
                ClickIntensity: 0.45,
                PopThreshold: 0.38,
                PopIntensity: 0.45,
                NoiseFloorDb: -60,
                NoiseReductionAmount: 0.35,
                UseMedianRepair: true,
                UseSpectralNoiseReduction: true,
                UseMultiBandTransientDetection: true,
                UseDecrackle: true,
                DecrackleIntensity: 0.35,
                UseBandLimitedInterpolation: true),
            new PresetDefinition(
                "Noisy Pressing",
                ClickThreshold: 0.35,
                ClickIntensity: 0.7,
                PopThreshold: 0.32,
                PopIntensity: 0.75,
                NoiseFloorDb: -55,
                NoiseReductionAmount: 0.55,
                UseMedianRepair: true,
                UseSpectralNoiseReduction: true,
                UseMultiBandTransientDetection: true,
                UseDecrackle: true,
                DecrackleIntensity: 0.55,
                UseBandLimitedInterpolation: true),
            new PresetDefinition(
                "Shellac 78s",
                ClickThreshold: 0.3,
                ClickIntensity: 0.8,
                PopThreshold: 0.26,
                PopIntensity: 0.85,
                NoiseFloorDb: -50,
                NoiseReductionAmount: 0.6,
                UseMedianRepair: false,
                UseSpectralNoiseReduction: true,
                UseMultiBandTransientDetection: true,
                UseDecrackle: true,
                DecrackleIntensity: 0.65,
                UseBandLimitedInterpolation: true),
            new PresetDefinition(
                "Pristine Audiophile",
                ClickThreshold: 0.55,
                ClickIntensity: 0.35,
                PopThreshold: 0.45,
                PopIntensity: 0.4,
                NoiseFloorDb: -65,
                NoiseReductionAmount: 0.25,
                UseMedianRepair: true,
                UseSpectralNoiseReduction: false,
                UseMultiBandTransientDetection: true,
                UseDecrackle: false,
                DecrackleIntensity: 0.3,
                UseBandLimitedInterpolation: true)
        };
    }
}
