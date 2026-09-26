using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DraftSimulator.App.Services;

public interface ICompletionActions
{
    Task CopyTextAsync(string text);
    Task<Stream?> OpenExportFileAsync();
}

public sealed class CompletionActions(Window owner) : ICompletionActions
{
    public async Task CopyTextAsync(string text)
    {
        var clipboard = owner.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    public async Task<Stream?> OpenExportFileAsync()
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Cockatrice card database",
            SuggestedFileName = "draft-simulator.xml",
            DefaultExtension = "xml",
            FileTypeChoices = [new FilePickerFileType("Cockatrice XML") { Patterns = ["*.xml"] }],
        });
        return file is null ? null : await file.OpenWriteAsync();
    }
}
