// Recipe 7.24: SDL3 gamepads, tested with a virtual gamepad (no hardware, no window). Covers hot-plug events,
// opening by instance id, axes, buttons and rumble.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.SdlApi;

static unsafe partial class Recipes
{
    static int _rumbleCalls;
    static ushort _rumbleLow;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte OnVirtualRumble(void* userdata, ushort low, ushort high)
    {
        _rumbleCalls++;
        _rumbleLow = low;
        return 1; // true = rumble supported
    }

    public static void GamepadRecipe()
    {
        Sdl.Init.Initialize(SdlInitFlags.Gamepad | SdlInitFlags.Events); // Gamepad implies Joystick
        try
        {
            var name = "RecipeCheck Pad\0"u8;
            uint id;
            fixed (byte* pName = name)
            {
                var desc = new SdlVirtualJoystickDesc
                {
                    version = (uint)sizeof(SdlVirtualJoystickDesc), // SDL_INIT_INTERFACE: version = struct size
                    type = (ushort)SdlJoystickType.Gamepad, // gets a standard mapping automatically
                    naxes = (ushort)SdlGamepadAxis.Count,
                    nbuttons = 15, // South..DpadRight, Guide, Start etc. in SdlGamepadButton order
                    name = pName,
                    Rumble = &OnVirtualRumble,
                };
                id = Sdl.Joystick.AttachVirtualJoystick(&desc);
            }
            Check(id != 0, "AttachVirtualJoystick returned an instance id");

            // Hot-plug: pads already connected at start-up arrive as GamepadAdded too, so handle it in the event loop.
            var added = false;
            Sdl.Gamepad.UpdateGamepads();
            while (Sdl.Events.PollEvent(out var ev))
                if (ev.type == SdlEventType.GamepadAdded && ev.gdevice.which == id)
                    added = true;
            Check(added, "GamepadAdded event for the new pad");
            Check(Sdl.Gamepad.IsGamepad(id), "IsGamepad(id)");

            var pad = Sdl.Gamepad.OpenGamepad(id); // throws SdlException on failure
            try
            {
                Console.WriteLine($"     gamepad: {Sdl.Gamepad.GetGamepadName(pad)} ({Sdl.Gamepad.GetGamepadType(pad)})");
                var joy = Sdl.Gamepad.GetGamepadJoystick(pad); // borrowed: don't close it (T17)

                // Drive the virtual device, then read it through the normal gamepad API.
                Sdl.Joystick.SetJoystickVirtualAxis(joy, (int)SdlGamepadAxis.Leftx, -32768);
                Sdl.Joystick.SetJoystickVirtualAxis(joy, (int)SdlGamepadAxis.RightTrigger, 32767);
                Sdl.Joystick.SetJoystickVirtualButton(joy, (int)SdlGamepadButton.South, true);
                Sdl.Gamepad.UpdateGamepads(); // also happens inside PollEvent
                Check(Sdl.Gamepad.GetGamepadAxis(pad, SdlGamepadAxis.Leftx) == -32768, "left stick X at -32768");
                Check(Sdl.Gamepad.GetGamepadAxis(pad, SdlGamepadAxis.RightTrigger) == 32767, "right trigger at 32767 (triggers are 0..32767)");
                Check(Sdl.Gamepad.GetGamepadButton(pad, SdlGamepadButton.South), "South (A on Xbox) held");

                var buttonEvent = false;
                while (Sdl.Events.PollEvent(out var ev))
                    if (ev.type == SdlEventType.GamepadButtonDown && ev.gbutton.which == id && (SdlGamepadButton)ev.gbutton.button == SdlGamepadButton.South)
                        buttonEvent = true;
                Check(buttonEvent, "GamepadButtonDown event for South");

                Check(Sdl.Gamepad.RumbleGamepad(pad, 40000, 20000, 100), "RumbleGamepad accepted");
                Check(_rumbleCalls > 0 && _rumbleLow == 40000, $"rumble reached the device ({_rumbleCalls} call(s), low {_rumbleLow})");
            }
            finally
            {
                Sdl.Gamepad.CloseGamepad(pad);
            }

            Sdl.Joystick.DetachVirtualJoystick(id);
            var removed = false;
            while (Sdl.Events.PollEvent(out var ev))
                if (ev.type == SdlEventType.GamepadRemoved && ev.gdevice.which == id)
                    removed = true;
            Check(removed, "GamepadRemoved event after unplugging");
        }
        finally
        {
            Sdl.Init.Quit();
        }
    }
}
