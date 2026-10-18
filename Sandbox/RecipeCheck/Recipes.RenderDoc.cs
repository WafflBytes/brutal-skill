// Recipe 7.42: RenderDoc without RenderDoc installed, which is what every player's machine looks like: the opt-in
// command-line switch, a failed load, the load latch, RemoveHooks as the reset, and what an unguarded call does.
// Attaching to a real RenderDoc and taking a capture are not run here.
using Brutal.RenderDocApi;

static partial class Recipes
{
    public static void RenderDocRecipe()
    {
        // Without --RenderDoc on the command line, Initialize does nothing and touches no DLL.
        Check(!RenderDoc.Initialize(new RenderDoc.InitOptions(["--level", "2"])), "Initialize without --RenderDoc returns false");
        Check(RenderDoc.State == RenderDoc.ControllerState.NotLoaded && !RenderDoc.ApiLoaded, "and the state stays NotLoaded");

        // With the switch but no RenderDoc at the search path: false, no exception, state LoadFail.
        var missing = Path.Combine(Path.GetTempPath(), "no-renderdoc-here", "renderdoc.dll");
        var opts = new RenderDoc.InitOptions(["--renderdoc"]) { SearchPaths = [missing] };    // the switch is case-insensitive
        Check(!RenderDoc.Initialize(opts), "Initialize with --renderdoc and no RenderDoc returns false");
        Console.WriteLine($"     state {RenderDoc.State}, available {RenderDoc.IsAvailable}, version {RenderDoc.Version}");
        Check(RenderDoc.State == RenderDoc.ControllerState.LoadFail && !RenderDoc.IsAvailable, "the state is LoadFail and IsAvailable is false");
        Check(RenderDoc.Version.Major == -1, "Version reads -1.-1.-1 when nothing loaded");

        // The first attempt latches: later LoadApi calls return the cached result without searching again.
        Check(!RenderDoc.LoadApi(ApiVersions.V1_0_0, missing) && RenderDoc.ApiLoaded, "a second LoadApi returns the latched failure");
        RenderDoc.RemoveHooks();                                                   // the only reset
        Check(!RenderDoc.ApiLoaded && RenderDoc.State == RenderDoc.ControllerState.NotLoaded, "RemoveHooks clears the latch, so a later load retries");

        // Nothing guards the capture calls: they read the native function table through a null pointer.
        Check(Throws<NullReferenceException>(() => _ = RenderDoc.IsFrameCapturing),
            "an unguarded call (IsFrameCapturing) without RenderDoc throws NullReferenceException: check IsAvailable first");
    }
}
