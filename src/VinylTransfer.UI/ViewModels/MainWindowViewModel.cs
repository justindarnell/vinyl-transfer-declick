using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using VinylTransfer.Core;
using VinylTransfer.Infrastructure;

namespace VinylTransfer.UI.ViewModels;

public sealed class MainWindowViewModel : ReactiveObject, IDisposable
{
    private string _statusMessage = "Status: Load a WAV file to begin. Diagnostics will appear here.";
    private string _progressStage = "Idle";
    private double _progressValue;
    private bool _isProcessing;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _useAutoMode = true;
    private float _autoClickSensitivity = 0.55f;
    private float _autoPopSensitivity = 0.5f;
    private float _autoNoiseReduction = 0.45f;
    private float _manualClickThreshold = 0.4f;
    private float _manualClickIntensity = 0.6f;
    private float _manualPopThreshold = 0.35f;
    private float _manualPopIntensity = 0.5f;
    private float _manualNoiseFloor = 0.02f;
    private float _manualNoiseReduction = 0.4f;
    private AudioBuffer? _inputBuffer;
    private ProcessingResult? _lastResult;
    private readonly WavFileService _wavFileService = new();
    private readonly IAudioProcessor _audioProcessor = new DspAudioProcessor();

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string ProgressStage
    {
        get => _progressStage;
        set => this.RaiseAndSetIfChanged(ref _progressStage, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            this.RaiseAndSetIfChanged(ref _isProcessing, value);
            this.RaisePropertyChanged(nameof(CanInteract));
        }
    }

    public bool CanInteract => !IsProcessing;

    public bool UseAutoMode
    {
        get => _useAutoMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _useAutoMode, value);
            this.RaisePropertyChanged(nameof(IsManualMode));
        }
    }

    public bool IsManualMode => !UseAutoMode;

    public float AutoClickSensitivity
    {
        get => _autoClickSensitivity;
        set => this.RaiseAndSetIfChanged(ref _autoClickSensitivity, value);
    }

    public float AutoPopSensitivity
    {
        get => _autoPopSensitivity;
        set => this.RaiseAndSetIfChanged(ref _autoPopSensitivity, value);
    }

    public float AutoNoiseReduction
    {
        get => _autoNoiseReduction;
        set => this.RaiseAndSetIfChanged(ref _autoNoiseReduction, value);
    }

    public float ManualClickThreshold
    {
        get => _manualClickThreshold;
        set => this.RaiseAndSetIfChanged(ref _manualClickThreshold, value);
    }

    public float ManualClickIntensity
    {
        get => _manualClickIntensity;
        set => this.RaiseAndSetIfChanged(ref _manualClickIntensity, value);
    }

    public float ManualPopThreshold
    {
        get => _manualPopThreshold;
        set => this.RaiseAndSetIfChanged(ref _manualPopThreshold, value);
    }

    public float ManualPopIntensity
    {
        get => _manualPopIntensity;
        set => this.RaiseAndSetIfChanged(ref _manualPopIntensity, value);
    }

    public float ManualNoiseFloor
    {
        get => _manualNoiseFloor;
        set => this.RaiseAndSetIfChanged(ref _manualNoiseFloor, value);
    }

    public float ManualNoiseReduction
    {
        get => _manualNoiseReduction;
        set => this.RaiseAndSetIfChanged(ref _manualNoiseReduction, value);
    }

    public bool HasInput => _inputBuffer is not null;

    public bool HasResult => _lastResult is not null;

    public Interaction<Unit, string?> OpenFileDialog { get; } = new();

    public Interaction<SaveFileDialogRequest, string?> SaveFileDialog { get; } = new();

    public ReactiveCommand<Unit, Unit> ImportWavCommand { get; }

    public ReactiveCommand<Unit, Unit> RunProcessingCommand { get; }

    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }

    public ReactiveCommand<Unit, Unit> ExportProcessedCommand { get; }

    public ReactiveCommand<Unit, Unit> ExportDifferenceCommand { get; }

    public MainWindowViewModel()
    {
        var canInteract = this.WhenAnyValue(x => x.IsProcessing).Select(isProcessing => !isProcessing);
        ImportWavCommand = ReactiveCommand.CreateFromTask(ImportWavAsync, canInteract);

        var canProcess = this.WhenAnyValue(x => x.HasInput, x => x.IsProcessing, (hasInput, isProcessing) => hasInput && !isProcessing);
        RunProcessingCommand = ReactiveCommand.CreateFromTask(RunProcessingAsync, canProcess);

        PreviewCommand = ReactiveCommand.Create(ShowPreviewStatus, canProcess);

        var canExport = this.WhenAnyValue(x => x.HasResult, x => x.IsProcessing, (hasResult, isProcessing) => hasResult && !isProcessing);
        ExportProcessedCommand = ReactiveCommand.CreateFromTask(ExportProcessedAsync, canExport);
        ExportDifferenceCommand = ReactiveCommand.CreateFromTask(ExportDifferenceAsync, canExport);
    }

    private async Task ImportWavAsync()
    {
        var path = await OpenFileDialog.Handle(Unit.Default);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _inputBuffer = _wavFileService.Read(path);
            _lastResult = null;
            this.RaisePropertyChanged(nameof(HasInput));
            this.RaisePropertyChanged(nameof(HasResult));
            StatusMessage = $"Status: Loaded {_inputBuffer.FrameCount:N0} frames @ {_inputBuffer.SampleRate} Hz.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Status: Failed to load WAV file. {ex.Message}";
        }
    }

    private async Task RunProcessingAsync()
    {
        if (_inputBuffer is null)
        {
            StatusMessage = "Status: Load a WAV file before processing.";
            return;
        }

        IsProcessing = true;
        ProgressStage = "Preparing...";
        ProgressValue = 0;
        StatusMessage = "Status: Processing audio...";

        var settings = BuildSettings();
        var request = new ProcessingRequest(_inputBuffer, settings);
        var progress = new Progress<ProcessingProgress>(UpdateProgress);

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            _lastResult = await Task.Run(
                () => _audioProcessor.Process(request, progress, _cancellationTokenSource.Token),
                _cancellationTokenSource.Token);

            this.RaisePropertyChanged(nameof(HasResult));

            if (_lastResult is not null)
            {
                var diagnostics = _lastResult.Diagnostics;
                StatusMessage =
                    $"Status: Complete in {diagnostics.ProcessingTime.TotalMilliseconds:N0} ms. " +
                    $"Clicks: {diagnostics.ClicksDetected:N0}, Pops: {diagnostics.PopsDetected:N0}, " +
                    $"Noise floor: {diagnostics.EstimatedNoiseFloor:F4}.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Status: Processing cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Status: Processing failed. {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void UpdateProgress(ProcessingProgress progress)
    {
        ProgressValue = Math.Clamp(progress.Percent * 100.0, 0, 100);
        ProgressStage = progress.Stage;
    }

    private void ShowPreviewStatus()
    {
        if (_lastResult is null)
        {
            StatusMessage = "Status: Process audio before previewing.";
            return;
        }

        StatusMessage = "Status: A/B preview is queued. (Playback pipeline wiring coming next.)";
    }

    private async Task ExportProcessedAsync()
    {
        if (_lastResult?.Processed is null)
        {
            StatusMessage = "Status: Nothing to export yet.";
            return;
        }

        var path = await SaveFileDialog.Handle(new SaveFileDialogRequest("Export processed WAV", "processed.wav"));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _wavFileService.Write(path, _lastResult.Processed);
            StatusMessage = $"Status: Exported processed WAV to {path}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Status: Failed to export processed WAV. {ex.Message}";
        }
    }

    private async Task ExportDifferenceAsync()
    {
        if (_lastResult?.Difference is null)
        {
            StatusMessage = "Status: Nothing to export yet.";
            return;
        }

        var path = await SaveFileDialog.Handle(new SaveFileDialogRequest("Export difference WAV", "difference.wav"));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _wavFileService.Write(path, _lastResult.Difference);
            StatusMessage = $"Status: Exported difference WAV to {path}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Status: Failed to export difference WAV. {ex.Message}";
        }
    }

    private ProcessingSettings BuildSettings()
    {
        var auto = new AutoModeSettings(AutoClickSensitivity, AutoPopSensitivity, AutoNoiseReduction);
        var manual = new ManualModeSettings(
            ManualClickThreshold,
            ManualClickIntensity,
            ManualPopThreshold,
            ManualPopIntensity,
            ManualNoiseFloor,
            ManualNoiseReduction);

        return new ProcessingSettings(auto, manual, UseAutoMode);
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
    }
}

public sealed record SaveFileDialogRequest(string Title, string? SuggestedFileName);
