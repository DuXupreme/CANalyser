namespace CanAnalyzer.Core.Analysis;

/// <summary>
/// Immutable extrema index over ascending timestamps. The source arrays remain shared and must
/// not be mutated. Queries include adjacent samples to preserve lines crossing the view boundary.
/// </summary>
public sealed class SignalRangeIndex
{
    private const int BlockSize = 64;
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly int[] _min;
    private readonly int[] _max;
    private readonly int _leaves;
    public bool SupportsRangeQueries { get; private set; } = true;

    public SignalRangeIndex(double[] x, double[] y)
    {
        if (x.Length != y.Length) throw new ArgumentException("X and Y arrays must have the same length.");
        _x = x; _y = y;
        _leaves = 1;
        while (_leaves < (x.Length + BlockSize - 1) / BlockSize) _leaves *= 2;
        _min = new int[_leaves * 2]; _max = new int[_leaves * 2];
        Array.Fill(_min, -1); Array.Fill(_max, -1);
        for (var i = 0; i < y.Length; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]) || (i > 0 && x[i] < x[i - 1]))
                SupportsRangeQueries = false;
            var node = _leaves + i / BlockSize;
            _min[node] = Minimum(_min[node], i);
            _max[node] = Maximum(_max[node], i);
        }
        for (var node = _leaves - 1; node > 0; node--)
        {
            _min[node] = Minimum(_min[node * 2], _min[node * 2 + 1]);
            _max[node] = Maximum(_max[node * 2], _max[node * 2 + 1]);
        }
    }

    public (double[] X, double[] Y) Select(double? minimum, double? maximum, int maximumPoints)
    {
        if (_x.Length == 0) return ([], []);
        var first = minimum is { } lo && double.IsFinite(lo) ? LowerBound(lo, false) : 0;
        var end = maximum is { } hi && double.IsFinite(hi) ? LowerBound(hi, true) : _x.Length;
        // No source data overlaps this view; do not fall back to plotting the entire dataset.
        if (first == _x.Length || end == 0 || first > end) return ([], []);
        first = Math.Max(0, first - 1);
        end = Math.Min(_x.Length, end + 1);
        var count = end - first;
        if (count <= maximumPoints || maximumPoints < 8)
            return first == 0 && end == _x.Length ? (_x, _y) : (_x[first..end], _y[first..end]);

        var indices = new List<int>(maximumPoints) { first };
        var buckets = (maximumPoints - 2) / 2;
        for (var i = 0; i < buckets; i++)
        {
            var start = first + 1 + (int)((long)i * (count - 2) / buckets);
            var stop = first + 1 + (int)((long)(i + 1) * (count - 2) / buckets);
            var (min, max) = Extrema(start, stop);
            if (min < 0) continue;
            indices.Add(Math.Min(min, max));
            if (min != max) indices.Add(Math.Max(min, max));
        }
        indices.Add(end - 1);
        var xs = new double[indices.Count]; var ys = new double[indices.Count];
        for (var i = 0; i < indices.Count; i++) { xs[i] = _x[indices[i]]; ys[i] = _y[indices[i]]; }
        return (xs, ys);
    }

    private (int Min, int Max) Extrema(int start, int end)
    {
        var min = -1; var max = -1;
        while (start < end && start % BlockSize != 0) Include(start++);
        while (end > start && end % BlockSize != 0) Include(--end);
        var left = _leaves + start / BlockSize; var right = _leaves + end / BlockSize;
        while (left < right)
        {
            if ((left & 1) != 0) IncludeNode(left++);
            if ((right & 1) != 0) IncludeNode(--right);
            left /= 2; right /= 2;
        }
        return (min, max);
        void Include(int i) { min = Minimum(min, i); max = Maximum(max, i); }
        void IncludeNode(int n) { min = Minimum(min, _min[n]); max = Maximum(max, _max[n]); }
    }

    private int Minimum(int a, int b) => a < 0 ? b : b < 0 ? a : _y[b] < _y[a] || (_y[b] == _y[a] && b < a) ? b : a;
    private int Maximum(int a, int b) => a < 0 ? b : b < 0 ? a : _y[b] > _y[a] || (_y[b] == _y[a] && b < a) ? b : a;
    private int LowerBound(double value, bool after)
    {
        var low = 0; var high = _x.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_x[middle] < value || (after && _x[middle] == value)) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
