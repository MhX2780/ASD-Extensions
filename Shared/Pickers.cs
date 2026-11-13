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

    public static async Task<string?> FileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetForegroundWindow());
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
