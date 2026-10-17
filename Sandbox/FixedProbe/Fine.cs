// Patterns the [Fixed] analyzers accept (no BRUTALCORE0009/0010).
using Brutal.Collections;
using Brutal.Pointers;
using Brutal.Pointers.Extensions;

static unsafe class Fine
{
    static int Main()
    {
        var total = 0;

        // stackalloc memory never moves.
        Span<int> stack = stackalloc int[4];
        stack.Fill(1);
        Ptr<int> fromStack = stack;
        total += fromStack[0];

        // NativeList<T>.AsSpan() is [return: Fixed].
        using var list = new NativeList<int>();
        list.Add(2);
        total += list.AsSpan().AsPtr()[0];

        // A managed array pinned with `fixed`.
        var arr = new[] { 3, 3, 3, 3 };
        fixed (int* p = arr)
            total += new ReadOnlySpan<int>(p, arr.Length).AsPtr()[0];

        Console.WriteLine(total == 6 ? "FixedProbe: accepted patterns ran correctly." : $"unexpected total {total}");
        return total == 6 ? 0 : 1;
    }
}
