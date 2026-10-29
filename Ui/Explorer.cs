using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QsrPriceBenchmarks.Ui;

/// <summary>
/// Shows a file in File Explorer, selected. Through the shell's own <c>SHOpenFolderAndSelectItems</c>, not
/// <c>explorer.exe /select</c>: the shell reuses an Explorer window already showing the folder, where the command
/// opened a new window on every export.
/// </summary>
public static class Explorer
{
    /// <summary>Selects <paramref name="filePath"/> in an Explorer window on its folder. Best effort: never throws.</summary>
    public static void Reveal(string filePath)
    {
        try
        {
            if (TrySelect(filePath))
            {
                return;
            }
            // The shell refused the path: a new window is still better than the user not finding the file.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException
            or COMException)
        {
            // Best effort — a failure to reveal must never break what revealed it.
        }
    }

    private static bool TrySelect(string filePath)
    {
        if (SHParseDisplayName(filePath, IntPtr.Zero, out IntPtr item, 0, out _) != 0 || item == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            // No children: the PIDL names the one item, and its folder is the one shown.
            return SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0) == 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name, IntPtr bindingContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);
}
