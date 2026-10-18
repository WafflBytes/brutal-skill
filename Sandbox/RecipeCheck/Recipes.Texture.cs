// Recipe 7.38: Brutal.Texture (KTX2 through libktx, DDS through gli). Makes a Basis-supercompressed KTX2 in memory
// with libktx's own encoder, then loads it the ways a game would: TextureLoader.LoadFromFile with the default
// transcode target (T32: BC7) and with Rgba32, LoadFromMemory (bug B10) and a DDS round trip. Headless; files go to a temp folder.
using Brutal.KtxApi;
using Brutal.TextureApi;
using KtxLoader = Brutal.TextureApi.Ktx.Loader;

static unsafe partial class Recipes
{
    public static void TextureRecipe()
    {
        const int Size = 64;
        var pixels = new byte[Size * Size * 4]; // smooth RGBA gradient: easy for Basis, so the error stays small
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var i = (y * Size + x) * 4;
                pixels[i] = (byte)(x * 4);
                pixels[i + 1] = (byte)(y * 4);
                pixels[i + 2] = 128;
                pixels[i + 3] = 255;
            }

        // ---- encode: ktxTexture2_Create -> SetImageFromMemory -> CompressBasis -> WriteToMemory ----
        var info = new KtxTextureCreateInfo
        {
            VkFormat = (uint)VkFormat.R8G8B8A8Unorm,
            BaseWidth = Size, BaseHeight = Size, BaseDepth = 1,
            NumDimensions = 2, NumLevels = 1, NumLayers = 1, NumFaces = 1,
        };
        Check(KtxTexture2.Create(in info, KtxTextureCreateStorageEnum.TextureCreateAllocStorage, out var k2) == KtxErrorCode.Success, "ktxTexture2_Create");
        byte[] basisKtx2;
        try
        {
            Check(k2.SetImageFromMemory(0, 0, 0, pixels) == KtxErrorCode.Success, "SetImageFromMemory");
            Check(k2.CompressBasis(255) == KtxErrorCode.Success, "CompressBasis(255): ETC1S at top quality (0 wrecked a block here, see 7.38)");
            Check(k2.NeedsTranscoding(), "a Basis texture needs transcoding");
            Check(k2.WriteToMemory(out var mem) == KtxErrorCode.Success, "WriteToMemory");
            using (mem)
                basisKtx2 = mem.Span.ToArray();
        }
        finally
        {
            k2.Destroy();
        }
        Console.WriteLine($"     Basis KTX2: {basisKtx2.Length} bytes for {pixels.Length} bytes of RGBA");

        var dir = Path.Combine(Path.GetTempPath(), "recipecheck-texture-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "gradient.ktx2");
            File.WriteAllBytes(path, basisKtx2);

            // T32: LoadFromFile transcodes Basis to BC7 unless told otherwise.
            var bc7 = TextureLoader.LoadFromFile(path);
            try
            {
                Console.WriteLine($"     LoadFromFile default: {bc7.Format}, {bc7.Extent}, {bc7.Data.Length} bytes");
                Check(bc7.Format == TextureFormat.BC7_UNorm_Block && bc7.Data.Length == Size * Size, "T32: LoadFromFile transcodes Basis to BC7 by default (1 byte per texel)");
            }
            finally
            {
                TextureLoader.Unload(bc7);
            }

            var rgba = TextureLoader.LoadFromFile(path, new KtxLoader.LoadSettings { SuperCompressionTranscodeFormat = KtxTranscodeFmt.Rgba32 });
            try
            {
                var data = rgba.Data;
                var worst = 0;
                for (var i = 0; i < pixels.Length; i++)
                    worst = Math.Max(worst, Math.Abs(data[i] - pixels[i]));
                Console.WriteLine($"     LoadFromFile Rgba32: {rgba.Format}, {rgba.Extent}, worst channel error {worst}/255");
                Check(rgba.Format == TextureFormat.R8G8B8A8_UNorm && rgba.Extent == new TexelCount3(Size, Size, 1) && data.Length == pixels.Length, "Rgba32 transcode gives R8G8B8A8 at full size");
                Check(worst <= 24, "Basis round trip is close to the source (lossy)");

                // DDS through gli: write the decoded texture, read it back unchanged.
                var dds = Path.Combine(dir, "gradient.dds");
                TextureWriter.SaveToFile(rgba, dds);
                var back = TextureLoader.LoadFromFile(dds);
                try
                {
                    Console.WriteLine($"     DDS round trip: {back.Format}, {back.Extent}, {new FileInfo(dds).Length} bytes on disk");
                    Check(back.Format == TextureFormat.R8G8B8A8_UNorm && back.Data.SequenceEqual(data), "DDS write + read keeps format and texels");
                }
                finally
                {
                    TextureLoader.Unload(back);
                }
            }
            finally
            {
                TextureLoader.Unload(rgba);
            }

            // B10: LoadFromMemory never transcodes, so a Basis KTX2 from memory throws (and the native handle leaks).
            // Inverted check: fails once fixed upstream.
            var threw = false;
            try
            {
                TextureLoader.Unload(TextureLoader.LoadFromMemory(basisKtx2, TextureLoader.FormatType.Ktx2,
                    new KtxLoader.LoadSettings { SuperCompressionTranscodeFormat = KtxTranscodeFmt.Rgba32 }));
            }
            catch (TextureException e)
            {
                threw = e.Message.Contains("supercompressed");
            }
            Check(threw, "B10 still present: TextureLoader.LoadFromMemory throws 'Texture is supercompressed' for Basis KTX2");

            // Workaround: create, transcode and wrap it yourself.
            Check(KtxTexture2.CreateFromMemory(basisKtx2, KtxTextureCreateFlagBits.LoadImageDataBit, out var h) == KtxErrorCode.Success, "KtxTexture2.CreateFromMemory");
            Check(h.TranscodeBasis(KtxTranscodeFmt.Rgba32, KtxTranscodeFlagBits.HighQuality) == KtxErrorCode.Success, "TranscodeBasis(Rgba32)");
            var mine = new Brutal.TextureApi.Ktx.KtxTexture(h); // takes the handle; Destroy() frees it
            try
            {
                Console.WriteLine($"     from memory, transcoded by hand: {mine.Format}, {mine.Extent}");
                Check(mine.Format == TextureFormat.R8G8B8A8_UNorm && mine.Data.Length == pixels.Length, "workaround: CreateFromMemory + TranscodeBasis + new KtxTexture(handle)");
            }
            finally
            {
                mine.Destroy();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
