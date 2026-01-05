using System;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls;
using ReactiveUI;
using VinylTransfer.UI.ViewModels;

namespace VinylTransfer.UI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenFileDialog.RegisterHandler(HandleOpenFileDialogAsync);
            viewModel.SaveFileDialog.RegisterHandler(HandleSaveFileDialogAsync);
        }
    }

    private async Task HandleOpenFileDialogAsync(InteractionContext<Unit, string?> context)
    {
        var dialog = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters =
            {
                new FileDialogFilter { Name = "WAV files", Extensions = { "wav" } },
                new FileDialogFilter { Name = "All files", Extensions = { "*" } }
            }
        };

        var result = await dialog.ShowAsync(this);
        context.SetOutput(result?.FirstOrDefault());
    }

    private async Task HandleSaveFileDialogAsync(InteractionContext<SaveFileDialogRequest, string?> context)
    {
        var dialog = new SaveFileDialog
        {
            Title = context.Input.Title,
            InitialFileName = context.Input.SuggestedFileName
        };

        var result = await dialog.ShowAsync(this);
        context.SetOutput(result);
    }
}
