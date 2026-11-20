using System.Runtime.InteropServices;

namespace AsdShared;

// Shared by several extensions: this file is linked into each of them (see their .csproj), so every DLL stays on its own.

/// <summary>Folder and file pickers that work from an extension (they need the window that is in front).</summary>
internal static class Pick
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static async Task<string?> FolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetForegroundWindow());
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>A "save as" box. The file is created empty by Windows; the caller writes it.</summary>
    public static async Task<string?> SaveAsync(string suggestedName, string extension, string typeName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = suggestedName };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetForegroundWindow());
        picker.FileTypeChoices.Add(typeName, new List<string> { extension });
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> FileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetForegroundWindow());
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
