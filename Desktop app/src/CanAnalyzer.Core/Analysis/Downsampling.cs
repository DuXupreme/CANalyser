namespace CanAnalyzer.Core.Analysis;

/// <summary>
/// Min/max bucket downsampling to keep plotting responsive on large datasets.
/// </summary>
public static class Downsampling
{
    public static (double[] X, double[] Y) MinMax(double[] x, double[] y, int maxPoints)
    {
        var n = x.Length;
        if (n != y.Length)
        {
            throw new ArgumentException("X and Y arrays must have the same length.");
        }

        if (n <= maxPoints || maxPoints < 8)
        {
            return (x, y);
        }

        var bucketCount = (maxPoints - 2) / 2;
        if (bucketCount < 2)
        {
            var step = Math.Max(1, n / maxPoints);
            return (SliceWithStep(x, step), SliceWithStep(y, step));
        }

        var xs = new List<double>(maxPoints + 8);
        var ys = new List<double>(maxPoints + 8);
        xs.Add(x[0]);
        ys.Add(y[0]);
        for (var i = 0; i < bucketCount; i++)
        {
            var start = 1 + (int)((long)i * (n - 2) / bucketCount);
            var end = 1 + (int)((long)(i + 1) * (n - 2) / bucketCount);
            if (end <= start)
            {
                continue;
            }

            var minIdx = start;
            var maxIdx = start;
            for (var j = start + 1; j < end; j++)
            {
                if (y[j] < y[minIdx])
                {
                    minIdx = j;
                }

                if (y[j] > y[maxIdx])
                {
                    maxIdx = j;
                }
            }

            if (minIdx <= maxIdx)
            {
                xs.Add(x[minIdx]);
                ys.Add(y[minIdx]);
                if (maxIdx != minIdx)
                {
                    xs.Add(x[maxIdx]);
                    ys.Add(y[maxIdx]);
                }
            }
            else
            {
                xs.Add(x[maxIdx]);
                ys.Add(y[maxIdx]);
                xs.Add(x[minIdx]);
                ys.Add(y[minIdx]);
            }
        }

        if (xs.Count < 2)
        {
            var step = Math.Max(1, n / maxPoints);
            return (SliceWithStep(x, step), SliceWithStep(y, step));
        }

        xs.Add(x[^1]);
        ys.Add(y[^1]);
        return (xs.ToArray(), ys.ToArray());
    }

    private static double[] SliceWithStep(double[] source, int step)
    {
        var data = new List<double>((source.Length / step) + 1);
        for (var i = 0; i < source.Length; i += step)
        {
            data.Add(source[i]);
        }

        return data.ToArray();
    }
}
