// Recipe 7.45: typed text in an SDL3 window (an IP address box, a name entry): text-input events, synthetic text
// events for tests, and the clipboard. Opens a hidden window; the clipboard text is saved and put back afterwards.
using System.Runtime.InteropServices;
using Brutal.SdlApi;

static unsafe partial class Recipes
{
    public static void TextInputRecipe()
    {
        Sdl.Init.Initialize(SdlInitFlags.Video | SdlInitFlags.Events);
        var window = Sdl.Video.CreateWindow("RecipeCheck 7.45", 320, 200, SdlWindowFlags.Hidden);
        try
        {
            // Text-input events carry the characters the keyboard layout produced, so digits typed on a Czech or
            // French layout (Shift + number row) arrive as digits. Key codes don't: read text from these events.
            Check(Sdl.Keyboard.StartTextInput(window), "StartTextInput");
            Check(Sdl.Keyboard.TextInputActive(window), "TextInputActive after StartTextInput");

            // A synthetic text event, as a --smoke script would push one. The UTF-8 must end in a NUL.
            var typed = "10.0.0.7:27777č"; // ends in a non-ASCII letter to prove UTF-8 round-trips
            var bytes = System.Text.Encoding.UTF8.GetBytes(typed + "\0");
            var buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
            var ev = new SdlEvent();
            ev.text.type = SdlEventType.TextInput;
            ev.text.windowID = Sdl.Video.GetWindowID(window);
            ev.text.text = buffer;
            Check(Sdl.Events.PushEvent(&ev), "PushEvent(TextInput)");
            // T58: SDL doesn't copy a pushed event's text; it keeps this pointer. Overwriting the first byte before
            // polling shows it: the polled text changes too. Keep the buffer alive and unchanged until PollEvent.
            buffer[0] = (byte)'X';
            string? received = null;
            while (Sdl.Events.PollEvent(out var e))
                if (e.type == SdlEventType.TextInput)
                    received = Marshal.PtrToStringUTF8((nint)e.text.text); // copy now: event text isn't yours to keep
            NativeMemory.Free(buffer); // only after the event has been polled
            Check(received != null, "the pushed TextInput event came back from PollEvent");
            Check(received == "X" + typed[1..], $"T58: PushEvent keeps the caller's text pointer (got \"{received}\"); if this fails, SDL now copies and T58 can go");

            Check(Sdl.Keyboard.StopTextInput(window), "StopTextInput");
            Check(!Sdl.Keyboard.TextInputActive(window), "TextInputActive false after StopTextInput");

            // Clipboard: no wrapper class, only Sdl.PInvoke. GetClipboardText returns SDL-allocated UTF-8 that you
            // free with Sdl.PInvoke.Free; it returns "" (not null) when the clipboard holds no text.
            static string ReadClipboard()
            {
                var p = Sdl.PInvoke.GetClipboardText();
                try
                {
                    return p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";
                }
                finally
                {
                    Sdl.PInvoke.Free(p);
                }
            }
            var hadText = Sdl.PInvoke.HasClipboardText() != 0;
            var saved = hadText ? ReadClipboard() : null;
            try
            {
                Check(Sdl.PInvoke.SetClipboardText("192.168.1.20") != 0, "SetClipboardText");
                Check(Sdl.PInvoke.HasClipboardText() != 0, "HasClipboardText after setting it");
                var back = ReadClipboard();
                Check(back == "192.168.1.20", $"GetClipboardText round trip (got \"{back}\")");
            }
            finally
            {
                if (saved != null)
                    Sdl.PInvoke.SetClipboardText(saved); // put the user's clipboard back
            }
        }
        finally
        {
            Sdl.Video.DestroyWindow(window);
            Sdl.Init.Quit();
        }
    }
}
