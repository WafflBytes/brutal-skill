// Recipe 7.41: NativeFileDialog on Windows. Open, save and folder dialogs, driven by a helper thread through Win32 (it
// finds the dialog, types a path and presses OK, or closes it), on an STA thread and on the default MTA main thread.
// Needs a desktop session; skipped with --no-window and off Windows.
using System.Runtime.InteropServices;
using Brutal.NativeFileDialogApi;

static unsafe partial class Recipes
{
    public static void NativeFileDialogRecipe()
    {
        if (!OperatingSystem.IsWindows())
            throw new SkipException("Windows only (the dialog driver uses Win32)");

        var dir = Path.Combine(Path.GetTempPath(), $"recipecheck-nfd-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        var existing = Path.Combine(dir, "level1.png");
        File.WriteAllBytes(existing, [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            // Filters are checked in C# before any dialog opens.
            Check(Throws<ArgumentException>(() => NativeFileDialog.OpenDialog(
                    new FileDialogInfo { FilterList = [new FilterItem { Name = "", Spec = "png" }] }, out _)),
                "an empty filter name throws ArgumentException before the dialog opens");

            // Every case on an STA thread, where Windows file dialogs belong.
            var sta = OnThread(ApartmentState.STA, () =>
            {
                NativeFileDialog.Initialize();                            // NFD_Init: COM for this thread
                var filters = new[] { new FilterItem { Name = "Images", Spec = "png,jpg" } };

                var open = WithDriver(DriverAction.TypeAndOk, existing, () =>
                    (NativeFileDialog.OpenDialog(new FileDialogInfo { FilterList = filters, DefaultPath = dir }, out var r), r));
                var save = WithDriver(DriverAction.TypeAndOk, Path.Combine(dir, "save1.png"), () =>
                    (NativeFileDialog.OpenDialog(new SaveFileDialogInfo { FilterList = filters, DefaultPath = dir, DefaultName = "save1.png" }, out var r), r));
                var cancel = WithDriver(DriverAction.Close, "", () =>
                    (NativeFileDialog.OpenDialog(new FileDialogInfo { FilterList = filters }, out var r), r));
                var folder = WithDriver(DriverAction.Close, "", () =>
                    (NativeFileDialog.OpenDialog(new FolderDialogInfo { DefaultPath = dir }, out var r), r));
                NativeFileDialog.Shutdown();
                return (open, save, cancel, folder);
            });
            Console.WriteLine($"     STA: open {sta.open.r} '{sta.open.path}', save {sta.save.r} '{sta.save.path}', closed open {sta.cancel.r}, closed folder {sta.folder.r}");
            Check(sta.open.r == NativeFileDialog.Result.Okay && Path.GetFullPath(sta.open.path) == Path.GetFullPath(existing),
                "open dialog: a typed existing file comes back as Okay with its full path");
            Check(sta.save.r == NativeFileDialog.Result.Okay && Path.GetFileName(sta.save.path) == "save1.png" && !File.Exists(sta.save.path),
                "save dialog: Okay with the typed path; nothing is created on disk");
            Check(sta.cancel.r == NativeFileDialog.Result.Cancel && sta.cancel.path == "", "a closed open dialog gives Cancel and an empty string");
            Check(sta.folder.r == NativeFileDialog.Result.Cancel && sta.folder.path == "", "a closed folder dialog gives Cancel and an empty string");

            // The default console main thread is MTA. NFD_Init's CoInitializeEx(STA) fails there, and Initialize() discards
            // the result; see what the dialog does anyway.
            var mta = OnThread(ApartmentState.MTA, () =>
            {
                NativeFileDialog.Initialize();
                var r = WithDriver(DriverAction.TypeAndOk, existing, () =>
                    (NativeFileDialog.OpenDialog(new FileDialogInfo(), out var res), res));
                NativeFileDialog.Shutdown();
                return r;
            });
            Console.WriteLine($"     MTA: open {mta.r} '{mta.path}' (dialog seen: {mta.seen})");
            Check(mta.r == NativeFileDialog.Result.Okay && Path.GetFullPath(mta.path) == Path.GetFullPath(existing),
                "on an MTA thread the open dialog still works (CoInitializeEx's failure is harmless)");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    enum DriverAction { TypeAndOk, Close }

    // Runs show() while a helper thread waits for this process's dialog window, then types text into its file-name box
    // and presses OK, or closes it.
    static (string path, NativeFileDialog.Result r, bool seen) WithDriver(DriverAction action, string text, Func<(string, NativeFileDialog.Result)> show)
    {
        var seen = false;
        var driver = new Thread(() =>
        {
            var dlg = WaitForDialog(15_000);
            if (dlg == 0) return;
            seen = true;
            Thread.Sleep(300);                                            // let the dialog finish building its controls
            if (action == DriverAction.TypeAndOk)
            {
                var edit = FindFileNameEdit(dlg);
                fixed (char* t = text)
                    SendMessageW(edit, WM_SETTEXT, 0, (nint)t);
                PostMessageW(dlg, WM_COMMAND, IDOK, 0);
            }
            else
                PostMessageW(dlg, WM_CLOSE, 0, 0);
        }) { IsBackground = true };
        driver.Start();
        var (path, r) = show();
        driver.Join(20_000);
        return (path, r, seen);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static T OnThread<T>(ApartmentState apartment, Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = work(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(apartment);
        t.Start();
        if (!t.Join(120_000)) throw new TimeoutException("dialog thread hung");
        if (error != null) throw new InvalidOperationException($"dialog thread failed: {error.Message}", error);
        return result;
    }

    static nint WaitForDialog(int timeoutMs)
    {
        nint found = 0;
        var pid = (uint)Environment.ProcessId;
        var until = Environment.TickCount64 + timeoutMs;
        while (found == 0 && Environment.TickCount64 < until)
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == pid && IsWindowVisible(hwnd) && ClassName(hwnd) == "#32770") { found = hwnd; return false; }
                return true;
            }, 0);
            if (found == 0) Thread.Sleep(50);
        }
        return found;
    }

    // The file-name box is an Edit inside a ComboBox (ComboBoxEx32 > ComboBox > Edit) in both open and save dialogs.
    static nint FindFileNameEdit(nint dlg)
    {
        nint edit = 0;
        EnumChildWindows(dlg, (hwnd, _) =>
        {
            if (ClassName(hwnd) == "Edit" && ClassName(GetParent(hwnd)) == "ComboBox") { edit = hwnd; return false; }
            return true;
        }, 0);
        if (edit == 0) throw new InvalidOperationException("no file-name Edit in the dialog");
        return edit;
    }

    static string ClassName(nint hwnd)
    {
        var buf = stackalloc char[64];
        var n = GetClassNameW(hwnd, buf, 64);
        return new string(buf, 0, n);
    }

    const uint WM_SETTEXT = 0x000C, WM_CLOSE = 0x0010, WM_COMMAND = 0x0111;
    const nint IDOK = 1;
    delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, nint lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(nint parent, EnumProc cb, nint lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint hwnd, char* buf, int max);
    [DllImport("user32.dll")] static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
