using System.Linq;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using ReactiveUI;
using VinylTransfer.UI.ViewModels;

namespace VinylTransfer.UI;

public sealed partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is null)
            {
                return;
            }

            disposables(ViewModel.OpenFileInteraction.RegisterHandler(async context =>
            {
                var dialog = context.Input;
                var result = await dialog.ShowAsync(this);
                context.SetOutput(result?.FirstOrDefault());
            }));

            disposables(ViewModel.SaveFileInteraction.RegisterHandler(async context =>
            {
                var dialog = context.Input;
                var result = await dialog.ShowAsync(this);
                context.SetOutput(result);
            }));
        });
    }
}
