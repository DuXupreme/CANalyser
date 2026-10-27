namespace CanAnalyzer.Core.Utilities;

/// <summary>Cooperative work on private buffers. Allocation and an individual OS/array copy are not preempted.</summary>
public static class CancellableArrayOperations
{
    private const int ChunkSize = 4096;

    public static List<T> ToList<T>(T[] source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<T>(source.Length);
        AddRange(result, source, cancellationToken);
        return result;
    }

    public static T[] ToArray<T>(List<T> source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new T[source.Count];
        for (var offset = 0; offset < result.Length; offset += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.CopyTo(offset, result, offset, Math.Min(ChunkSize, result.Length - offset));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static void AddRange<T>(List<T> target, T[] source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled) { target.AddRange(source); return; }
        for (var offset = 0; offset < source.Length; offset += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.AddRange(new ArraySegment<T>(source, offset, Math.Min(ChunkSize, source.Length - offset)));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Stable merge sort with bounded cancellation checks. Do not publish the working array until this returns.</summary>
    public static void Sort<T>(T[] items, Comparison<T> comparison, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Length < 2) return;
        var source = items;
        var target = new T[items.Length];
        for (long width = 1; width < items.Length; width *= 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (long first = 0; first < items.Length; first += width * 2)
            {
                var left = (int)first;
                var middle = (int)Math.Min(first + width, items.Length);
                var right = middle;
                var end = (int)Math.Min(first + width * 2, items.Length);
                for (var output = left; output < end; output++)
                {
                    if ((output & (ChunkSize - 1)) == 0) cancellationToken.ThrowIfCancellationRequested();
                    target[output] = left < middle && (right >= end || comparison(source[left], source[right]) <= 0)
                        ? source[left++] : source[right++];
                }
            }
            (source, target) = (target, source);
        }
        if (!ReferenceEquals(source, items))
            for (var offset = 0; offset < items.Length; offset += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Copy(source, offset, items, offset, Math.Min(ChunkSize, items.Length - offset));
            }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
