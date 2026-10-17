// Recipe 7.21: Dear ImGui debug UI on GLFW + OpenGL 4.1 core, using BRUTAL's own backends.
// Needs a display (a hidden window is enough); skipped with --no-window.
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.OpenGlApi;

static unsafe partial class Recipes
{
    static GlGetProcAddressFunc? s_glLoader; // must stay rooted: GLAD keeps the function pointer (T35)

    public static void ImGuiRecipe()
    {
        if (!Glfw.Init())
            throw new Exception("GLFW init failed");
        GlfwWindow window = default!;
        var made = false;
        try
        {
            // A GL 4.1+ core context: the only kind BRUTAL's OpenGL backend supports (T22).
            Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_OPENGL_API);
            Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, 4);
            Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, 1);
            Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_CORE_PROFILE);
            Glfw.WindowHint(GlfwWindowHint.Visible, 0);
            window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "RecipeCheck ImGui", Size = new int2(640, 400) });
            made = true;
            Glfw.PInvoke.MakeContextCurrent(window);
            s_glLoader = name => Glfw.PInvoke.GetProcAddress(name);
            Check(Glad.Initialize(s_glLoader), "Glad.Initialize");

            ImGui.CreateContext();
            var io = ImGui.GetIO();
            io.IniFilename = default; // no imgui.ini written to the working directory
            ImGui.StyleColorsDark();
            var platform = new GlfwBackend(window, GlfwBackend.ClientApi.OpenGL);
            var renderer = new OpenGlBackend();
            try
            {
                var clicked = 0;
                var volume = 0.5f;
                var showGrid = true;
                var size = window.FramebufferSize;
                var pixels = new byte[size.X * size.Y * 4];
                var vertices = 0;
                for (var frame = 0; frame < 6; frame++)
                {
                    Glfw.PollEvents();
                    // Synthetic input after the backend's own update, so the hidden window can't overwrite it:
                    // frames 3-4 press and release the left button over "Click me".
                    renderer.NewFrame();
                    platform.NewFrame();
                    if (frame >= 2)
                        io.AddMousePosEvent(60, 70);
                    if (frame == 3)
                        io.AddMouseButtonEvent(0, true);
                    if (frame == 4)
                        io.AddMouseButtonEvent(0, false);
                    ImGui.NewFrame();

                    ImGui.SetNextWindowPos(new float2(10, 10));
                    ImGui.SetNextWindowSize(new float2(300, 160));
                    ImGui.Begin("Debug"u8);
                    ImGui.Text($"Frame {frame}");
                    if (ImGui.Button("Click me"u8, new float2(120, 30)))
                        clicked++;
                    ImGui.SliderFloat("Volume"u8, ref volume, 0, 1);
                    ImGui.Checkbox("Show grid"u8, ref showGrid);
                    ImGui.End();
                    ImGui.Render();

                    OpenGl.Native.glViewport(0, 0, size.X, size.Y);
                    OpenGl.Native.glClearColor(0, 0, 0, 1);
                    OpenGl.Native.glClear(OpenGl.GL_COLOR_BUFFER_BIT);
                    var drawData = ImGui.GetDrawData();
                    vertices = drawData.TotalVtxCount;
                    renderer.RenderDrawData(drawData);
                    if (frame == 5)
                        fixed (byte* p = pixels)
                            OpenGl.Native.glReadPixels(0, 0, size.X, size.Y, OpenGl.GL_RGBA, OpenGl.GL_UNSIGNED_BYTE, p);
                    Glfw.PInvoke.SwapBuffers(window);
                }

                var lit = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > 30 || pixels[i + 1] > 30 || pixels[i + 2] > 30)
                        lit++;
                Console.WriteLine($"     ImGui: {vertices} vertices, {lit} lit pixels, button clicked {clicked}x");
                var shotDir = Environment.GetEnvironmentVariable("RECIPECHECK_SHOTS"); // optional: look at the frame
                if (!string.IsNullOrEmpty(shotDir))
                {
                    var flipped = new byte[pixels.Length]; // GL rows are bottom-up
                    for (var y = 0; y < size.Y; y++)
                        Array.Copy(pixels, (size.Y - 1 - y) * size.X * 4, flipped, y * size.X * 4, size.X * 4);
                    Directory.CreateDirectory(shotDir);
                    Brutal.StbApi.Stb.WritePng(Path.Combine(shotDir, "7.21-imgui.png"), size.X, size.Y, 4, flipped, size.X * 4);
                }
                Check(vertices > 100, "ImGui produced draw data");
                Check(lit > 2000, "the OpenGL backend drew the window");
                Check(clicked == 1, "a synthetic click on the button made Button() return true once");
            }
            finally
            {
                renderer.Dispose(); // order: renderer, platform, context, window
                platform.Dispose();
                ImGui.DestroyContext();
            }
        }
        finally
        {
            if (made)
                window.Dispose();
            Glfw.Terminate();
        }
    }
}
