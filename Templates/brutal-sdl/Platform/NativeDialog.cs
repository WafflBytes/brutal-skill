using System.Runtime.InteropServices;

namespace BrutalGame.Platform;

/// <summary>
/// Error dialogs and start-up checks that work without SDL, so they still work when SDL's DLL can't load.
/// </summary>
public static partial class NativeDialog
{
    public const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>
    /// True when the Visual C++ runtime DLLs that BRUTAL's native libraries import can be loaded.
    /// SDL_ttf and others also import VCRUNTIME140_1.dll, so check both.
    /// </summary>
    public static bool VcRuntimeInstalled()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        foreach (var dll in new[] { "vcruntime140.dll", "vcruntime140_1.dll" })
        {
            if (!NativeLibrary.TryLoad(dll, typeof(NativeDialog).Assembly, DllImportSearchPath.System32, out var handle))
                return false;
            NativeLibrary.Free(handle);
        }
        return true;
    }

    /// <summary>Win32 MessageBoxW on Windows; stderr elsewhere.</summary>
    public static void ShowError(string title, string message)
    {
        if (OperatingSystem.IsWindows())
            MessageBoxW(0, message, title, 0x10); // MB_ICONERROR
        else
            Console.Error.WriteLine($"{title}: {message}");
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
