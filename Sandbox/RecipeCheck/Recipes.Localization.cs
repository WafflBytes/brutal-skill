// Recipe 7.37: Brutal.Localization. LString templates, a provider fed from a ResX file (written and read back in
// memory), reordered and wrong replacement names, and bug B6 (Format<TWriter> copies a ref-struct writer). Headless.
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Localization;
using Brutal.Localization.FixedBuffers;
using Brutal.Localization.ResX;

static unsafe partial class Recipes
{
    public static void LocalizationRecipe()
    {
        // Own instance, so the recipe doesn't touch the LStrings singleton.
        var strings = new LocalizedStrings();
        var coins = strings.Create("hud.coins", "{Name} has {Count} coins");
        var title = strings.Create("menu.title", "Main menu");
        Check(coins.Format("Ann", 3) == "Ann has 3 coins", "template formats before any provider is set");

        // A French table as ResX, written and read back without touching the disk. The French reorders the variables.
        var sw = new StringWriter();
        using (var w = new ResXWriter(sw))
            w.AddString("hud.coins", "{Count} pieces pour {Name}", "reordered on purpose");
        string resx = sw.ToString();
        Dictionary<string, string> fr;
        using (var r = new ResXReader(new StringReader(resx)))
            fr = r.Data.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        Check(fr.Count == 1 && fr["hud.coins"] == "{Count} pieces pour {Name}", "ResX round trip keeps name and value");

        strings.SetLocalizedStringProvider((LString s, [NotNullWhen(true)] out string? text) => fr.TryGetValue(s.Id.ToString(), out text));
        Console.WriteLine($"     fr: \"{coins.Format("Ann", 3)}\", missing id stays \"{title.Format()}\"");
        Check(coins.Format("Ann", 3) == "3 pieces pour Ann", "provider localizes existing LStrings; arguments follow the template's order, not the translation's");
        Check(title.Format() == "Main menu", "an id the provider lacks keeps its template");

        // A translation must use the template's variable names and count.
        var threw = false;
        try { coins.Localize("{Count} pieces pour {Player}"); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "Localize with an unknown variable name throws ArgumentException");
        threw = false;
        try { _ = coins.Format("Ann"); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "Format with the wrong argument count throws ArgumentException");

        strings.RemoveLocalizedStringProvider();
        Check(coins.Format("Ann", 3) == "Ann has 3 coins", "RemoveLocalizedStringProvider returns to the template");

        // B6: Format<TWriter> takes the writer by value. A ref-struct RingBufferWriter is copied, so the caller's copy
        // never advances and the next Format writes over the previous result. Inverted check: fails once fixed upstream.
        Span<char> buf = stackalloc char[512];
        var ring = new RingBufferWriter<char>(buf);
        var a = coins.Format(ring, "Ann", 3);
        string aBefore = a.ToString();
        var b = coins.Format(ring, "Bob", 12);
        bool sameStart = Unsafe.AreSame(ref MemoryMarshal.GetReference(a), ref MemoryMarshal.GetReference(b));
        Console.WriteLine($"     B6: first \"{aBefore}\" now reads \"{a.ToString()}\", second \"{b.ToString()}\", same start {sameStart}");
        Check(sameStart && a.ToString() != aBefore, "B6 still present: a second Format into the same RingBufferWriter overwrites the first");

        // Workaround: a class writer is shared by reference, so it advances.
        var arr = new ArrayCharWriter(512);
        var c = coins.Format(arr, "Ann", 3).ToString();
        var d = coins.Format(arr, "Bob", 12).ToString();
        Check(c == "Ann has 3 coins" && d == "Bob has 12 coins" && arr.Written == c.Length + d.Length
            && new string(arr.Buffer, 0, arr.Written) == c + d, "a class writer advances between Format calls");
    }

    // Minimal class writer over a pinned array (IFixedBufferWriter wants memory the GC won't move).
    sealed class ArrayCharWriter(int capacity) : IFixedBufferWriter<char>
    {
        public readonly char[] Buffer = GC.AllocateArray<char>(capacity, pinned: true);
        public int Written;

        public void Advance(ElementCount count)
        {
            if (count < 0 || Written + count > Buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            Written += count;
        }

        public Span<char> GetSpan(ElementCount lengthHint) =>
            TryGetSpan(lengthHint, out var s) ? s : throw new ArgumentException("Out of buffer.");

        public bool TryGetSpan(ElementCount lengthHint, out Span<char> spanOut)
        {
            spanOut = Buffer.AsSpan(Written);
            return lengthHint == LengthHint.NonZeroLengthHint || lengthHint == LengthHint.MaxLengthHint
                ? !spanOut.IsEmpty
                : spanOut.Length >= lengthHint;
        }
    }
}
