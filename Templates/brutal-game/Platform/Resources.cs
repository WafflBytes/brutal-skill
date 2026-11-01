using System.Runtime.InteropServices;

namespace BrutalGame.Platform;

/// <summary>Embedded assets (see the EmbeddedResource items in the .csproj). Embedding means they can't go missing.</summary>
public static class Resources
{
    public static byte[] Read(string logicalName)
    {
        using var stream =
            typeof(Resources).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"embedded resource '{logicalName}' missing");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

/// <summary>
/// A copy of some bytes in native memory that never moves. Use it when native code keeps reading the data after
/// the call returns: SDL_ttf reads a font lazily for as long as the font is open, so a pinned managed array
/// would have to stay pinned that long.
/// </summary>
public sealed unsafe class NativeBlob : IDisposable
{
    public byte* Data { get; private set; }
    public int Length { get; }

    public NativeBlob(ReadOnlySpan<byte> source)
    {
        Length = source.Length;
        Data = (byte*)NativeMemory.Alloc((nuint)Length);
        source.CopyTo(new Span<byte>(Data, Length));
    }

    public void Dispose()
    {
        if (Data == null)
            return;
        NativeMemory.Free(Data);
        Data = null;
    }
}
