// Compiled only with -p:ProbeErrors=true. Each line must produce the error named beside it.
using Brutal.Pointers;
using Brutal.Pointers.Extensions;

static class Errors
{
    static void Probe()
    {
        ReadOnlySpan<int> heap = new int[4];
        _ = heap.AsPtr(); // BRUTALCORE0009: non-fixed span passed to a [Fixed] parameter

        Span<int> heap2 = new int[4];
        Ptr<int> p = heap2; // BRUTALCORE0010: implicit Span<T> -> Ptr<T> from a non-fixed source
        _ = p;
    }
}
