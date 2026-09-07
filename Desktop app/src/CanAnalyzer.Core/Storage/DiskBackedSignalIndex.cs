using System.Buffers.Binary;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Storage;

/// <summary>
/// One shared file of signal-specific blocks. Only summaries, one small pending block per signal,
/// and one offset per 256 samples remain in memory; full series are still loaded on demand.
/// </summary>
internal sealed class DiskBackedSignalIndex(string path) : ISignalSampleLookup, IDisposable
{
    private const int PointBytes = 24;
    private const int BlockPoints = 256;
    private readonly Dictionary<SignalIdentity, SignalBuffer> _signals = [];
    private FileStream? _writer = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
    private bool _disposed;

    public void Append(DecodedSignalSample sample)
    {
        if (!_signals.TryGetValue(sample.Identity, out var signal))
            _signals.Add(sample.Identity, signal = new SignalBuffer(sample));
        signal.Append(sample);
        if (signal.PendingCount == BlockPoints) Flush(signal);
    }

    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is null) return;
        foreach (var signal in _signals.Values)
        {
            Flush(signal);
            signal.Pending = [];
        }
        _writer.Dispose();
        _writer = null;
    }

    public IReadOnlyList<SignalSampleSummary> GetSignalSummaries()
    {
        Complete();
        return _signals.Values.Select(signal => new SignalSampleSummary(
            signal.Count, signal.Minimum, signal.Maximum, signal.Latest)).ToArray();
    }

    public IEnumerable<SignalSeriesPoint> ReadSignalSeries(SignalIdentity identity)
    {
        Complete();
        if (!_signals.TryGetValue(identity, out var signal)) yield break;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PointBytes * BlockPoints);
        using var reader = new BinaryReader(stream);
        foreach (var (offset, count) in signal.Blocks)
        {
            stream.Position = offset;
            for (var index = 0; index < count; index++)
                yield return new SignalSeriesPoint(reader.ReadInt64(), reader.ReadInt64(), reader.ReadDouble());
        }
    }

    public IEnumerable<SignalSeriesPoint> ReadSignalSeriesSampled(SignalIdentity identity, int maximumPoints)
    {
        Complete();
        if (maximumPoints < 2) throw new ArgumentOutOfRangeException(nameof(maximumPoints));
        if (!_signals.TryGetValue(identity, out var signal) || signal.Count == 0) yield break;
        if (signal.Count <= maximumPoints)
        {
            foreach (var point in ReadSignalSeries(identity)) yield return point;
            yield break;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PointBytes * BlockPoints, FileOptions.RandomAccess);
        using var reader = new BinaryReader(stream);
        for (var outputIndex = 0; outputIndex < maximumPoints; outputIndex++)
        {
            var sourceIndex = (long)outputIndex * (signal.Count - 1L) / (maximumPoints - 1L);
            var blockIndex = (int)(sourceIndex / BlockPoints);
            var pointIndex = (int)(sourceIndex % BlockPoints);
            var block = signal.Blocks[blockIndex];
            stream.Position = block.Offset + (pointIndex * PointBytes);
            yield return new SignalSeriesPoint(reader.ReadInt64(), reader.ReadInt64(), reader.ReadDouble());
        }
    }

    private void Flush(SignalBuffer signal)
    {
        if (signal.PendingCount == 0) return;
        signal.Blocks.Add((_writer!.Position, signal.PendingCount));
        _writer.Write(signal.Pending.AsSpan(0, signal.PendingCount * PointBytes));
        signal.PendingCount = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer?.Dispose();
        _signals.Clear();
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class SignalBuffer(DecodedSignalSample first)
    {
        public byte[] Pending = new byte[PointBytes * BlockPoints];
        public int PendingCount;
        public readonly List<(long Offset, int Count)> Blocks = [];
        public int Count;
        public double Minimum = first.Value;
        public double Maximum = first.Value;
        public DecodedSignalSample Latest = first;

        public void Append(DecodedSignalSample sample)
        {
            var target = Pending.AsSpan(PendingCount * PointBytes, PointBytes);
            BinaryPrimitives.WriteInt64LittleEndian(target, sample.TimestampNanoseconds);
            BinaryPrimitives.WriteInt64LittleEndian(target[8..], sample.FrameIndex);
            BinaryPrimitives.WriteDoubleLittleEndian(target[16..], sample.Value);
            PendingCount++;
            Count++;
            if (sample.Value < Minimum) Minimum = sample.Value;
            if (sample.Value > Maximum) Maximum = sample.Value;
            if (sample.TimestampNanoseconds > Latest.TimestampNanoseconds ||
                (sample.TimestampNanoseconds == Latest.TimestampNanoseconds && sample.FrameIndex >= Latest.FrameIndex))
                Latest = sample;
        }
    }
}
