using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using NAudio.Wave;
using ReactiveUI;
using VinylTransfer.Core;
using VinylTransfer.Infrastructure;
using VinylTransfer.UI;

namespace VinylTransfer.UI.ViewModels;

public sealed class MainWindowViewModel : ReactiveObject
{
    private readonly WavFileService _wavFileService = new();
    private readonly DspAudioProcessor _processor = new();
    private readonly SettingsStore _settingsStore = new();

    private AudioBuffer? _inputBuffer;
    private AudioBuffer? _processedBuffer;
    private AudioBuffer? _differenceBuffer;
    private string? _loadedPath;
    private bool _isPreviewingProcessed;
    private bool _suppressSettingsSave;
    private WaveOutEvent? _outputDevice;
    private BufferedWaveProvider? _bufferedProvider;
    private bool _isPlaying;
    private EventHandler<StoppedEventArgs>? _playbackStoppedHandler;

    private string _statusMessage = "Status: Load a WAV file to begin. Diagnostics will appear here.";
    private double _clickThreshold = 0.4;
    private double _clickIntensity = 0.6;
    private double _popThreshold = 0.35;
    private double _popIntensity = 0.5;
    private double _noiseFloorDb = -60;
    private double _noiseReductionAmount = 0.5;

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
            .Subscribe(ex => StatusMessage = $"Status: {ex.Message}");

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
        _isPreviewingProcessed = true;
        this.RaisePropertyChanged(nameof(DisplayBuffer));

        StatusMessage = $"Status: Cleaned audio in {result.Diagnostics.ProcessingTime.TotalMilliseconds:F0} ms · " +
                        $"Clicks: {result.Diagnostics.ClicksDetected} · Pops: {result.Diagnostics.PopsDetected} · " +
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

        _isPreviewingProcessed = !_isPreviewingProcessed;
        this.RaisePropertyChanged(nameof(DisplayBuffer));
        var source = _isPreviewingProcessed ? "Processed" : "Original";
        StatusMessage = $"Status: Preview set to {source} audio.";

        if (_isPlaying)
        {
            StartPlayback();
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
            NoiseReductionAmount: (float)NoiseReductionAmount);

        var autoSettings = new AutoModeSettings(
            ClickSensitivity: (float)ClickThreshold,
            PopSensitivity: (float)PopThreshold,
            NoiseReductionAmount: (float)NoiseReductionAmount);

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
            var directory = Path.GetDirectoryName(_loadedPath);
            var name = Path.GetFileNameWithoutExtension(_loadedPath);
            return (directory, $"{name}-{suffix}.wav");
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
            NoiseReductionAmount = NoiseReductionAmount
        };

        _settingsStore.Save(data);
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
        _suppressSettingsSave = false;
        SaveSettings();
    }

    private bool IsPlaying
    {
        get => _isPlaying;
        set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    private void StartPlayback()
    {
        var buffer = DisplayBuffer;
        if (buffer is null)
        {
            return;
        }

        StopPlayback(updateStatus: false);

        var format = WaveFormat.CreateIeeeFloatWaveFormat(buffer.SampleRate, buffer.Channels);
        _bufferedProvider = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true
        };

        var bytes = new byte[buffer.Samples.Length * sizeof(float)];
        Buffer.BlockCopy(buffer.Samples, 0, bytes, 0, bytes.Length);
        _bufferedProvider.AddSamples(bytes, 0, bytes.Length);

        _outputDevice = new WaveOutEvent();
        _playbackStoppedHandler = (_, _) =>
        {
            IsPlaying = false;
            StopPlayback(updateStatus: true, stopDevice: false);
        };
        _outputDevice.PlaybackStopped += _playbackStoppedHandler;
        _outputDevice.Init(_bufferedProvider);
        _outputDevice.Play();
        IsPlaying = true;
        StatusMessage = "Status: Playing preview audio.";
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
        if (stopDevice)
        {
            _outputDevice.Stop();
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
}
