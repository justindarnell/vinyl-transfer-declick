using ReactiveUI;

namespace VinylTransfer.UI.ViewModels;

public sealed class MainWindowViewModel : ReactiveObject
{
    private string _statusMessage = "Status: Load a WAV file to begin. Diagnostics will appear here.";

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }
}
