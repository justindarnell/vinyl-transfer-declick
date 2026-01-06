using System.Linq;
using System.Reactive.Disposables;
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

            ViewModel.OpenFileInteraction.RegisterHandler(async context =>
            {
                var dialog = context.Input;
                var result = await dialog.ShowAsync(this);
                context.SetOutput(result?.FirstOrDefault());
            }).DisposeWith(disposables);

            ViewModel.SaveFileInteraction.RegisterHandler(async context =>
            {
                var dialog = context.Input;
                var result = await dialog.ShowAsync(this);
                context.SetOutput(result);
            }).DisposeWith(disposables);
        });
    }
}
