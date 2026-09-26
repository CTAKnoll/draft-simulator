using Avalonia.Controls;
using Avalonia.Input;
using DraftSimulator.App.ViewModels;

namespace DraftSimulator.App.Views;

public sealed partial class DraftPage : UserControl
{
    public DraftPage() => InitializeComponent();

    private void CardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is DraftViewModel viewModel && sender is Control { DataContext: DraftCardViewModel card }) viewModel.Preview(card);
    }

    private void CardPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is DraftViewModel viewModel) viewModel.Preview(null);
    }
}
