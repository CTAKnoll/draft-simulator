using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DraftSimulator.App.Services;

public interface IFolderPickerService
{
    Task<string?> PickCardDirectoryAsync();
}

public sealed class FolderPickerService(Window owner) : IFolderPickerService
{
    public async Task<string?> PickCardDirectoryAsync()
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select card directory",
            AllowMultiple = false,
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
