using System.Collections;
using System.Numerics;
using System.Text;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Storage;

/// <summary>Append-only decoded-sample store with exact BigInteger raw values and a compact disk index.</summary>
public sealed class DiskBackedDecodedSampleStore : IReadOnlyList<DecodedSignalSample>, IFrameSampleLookup, IDisposable
{
    private static readonly byte[] Magic = "CANSMP2\0"u8.ToArray();
    private readonly string _dataPath;
    private readonly string _indexPath;
    private readonly string _frameIndexPath;
    private readonly object _readLock = new();
    private FileStream? _dataWriteStream;
    private FileStream? _indexWriteStream;
    private BinaryWriter? _dataWriter;
    private BinaryWriter? _indexWriter;
    private FileStream? _frameIndexWriteStream;
    private BinaryWriter? _frameIndexWriter;
    private FileStream? _dataReadStream;
    private FileStream? _indexReadStream;
    private BinaryReader? _dataReader;
    private BinaryReader? _indexReader;
    private FileStream? _frameIndexReadStream;
    private BinaryReader? _frameIndexReader;
    private long _activeFrameIndex = long.MinValue;
    private int _activeFrameFirstSample;
    private int _activeFrameSampleCount;
    private int _frameEntryCount;
    private bool _complete;
    private bool _disposed;

    public DiskBackedDecodedSampleStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CANalyser", "sample-cache");
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        _dataPath = Path.Combine(directory, $"{id}.samples");
        _indexPath = Path.Combine(directory, $"{id}.index");
        _frameIndexPath = Path.Combine(directory, $"{id}.frame-index");
        _dataWriteStream = new FileStream(_dataPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        _indexWriteStream = new FileStream(_indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        _frameIndexWriteStream = new FileStream(_frameIndexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        _dataWriter = new BinaryWriter(_dataWriteStream, Encoding.UTF8, true);
        _indexWriter = new BinaryWriter(_indexWriteStream, Encoding.UTF8, true);
        _frameIndexWriter = new BinaryWriter(_frameIndexWriteStream, Encoding.UTF8, true);
        _dataWriter.Write(Magic);
        _dataWriter.Write(1);
    }

    public int Count { get; private set; }

    public DecodedSignalSample this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            Complete();
            lock (_readLock)
            {
                EnsureReaders();
                _indexReadStream!.Position = index * sizeof(long);
                var offset = _indexReader!.ReadInt64();
                _dataReadStream!.Position = offset;
                return Read(_dataReader!);
            }
        }
    }

    public void Append(DecodedSignalSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_complete) throw new InvalidOperationException("The decoded-sample store is already completed.");
        if (Count == int.MaxValue) throw new IOException("The sample-store count exceeds the supported 32-bit list index.");
        if (_activeFrameIndex != sample.FrameIndex)
        {
            FinalizeActiveFrame();
            if (_activeFrameIndex != long.MinValue && sample.FrameIndex < _activeFrameIndex)
                throw new InvalidOperationException("Decoded samples must be appended in nondecreasing frame-index order.");
            _activeFrameIndex = sample.FrameIndex;
            _activeFrameFirstSample = Count;
            _activeFrameSampleCount = 0;
        }

        _indexWriter!.Write(_dataWriteStream!.Position);
        Write(_dataWriter!, sample);
        _activeFrameSampleCount++;
        Count++;
    }

    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_complete) return;
        FinalizeActiveFrame();
        _dataWriter!.Flush();
        _indexWriter!.Flush();
        _frameIndexWriter!.Flush();
        _dataWriter.Dispose();
        _indexWriter.Dispose();
        _frameIndexWriter.Dispose();
        _dataWriteStream!.Dispose();
        _indexWriteStream!.Dispose();
        _frameIndexWriteStream!.Dispose();
        _dataWriter = null;
        _indexWriter = null;
        _frameIndexWriter = null;
        _dataWriteStream = null;
        _indexWriteStream = null;
        _frameIndexWriteStream = null;
        _complete = true;
    }

    public IEnumerator<DecodedSignalSample> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Complete();
        return Enumerate().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dataWriter?.Dispose();
        _indexWriter?.Dispose();
        _frameIndexWriter?.Dispose();
        _dataWriteStream?.Dispose();
        _indexWriteStream?.Dispose();
        _frameIndexWriteStream?.Dispose();
        _dataReader?.Dispose();
        _indexReader?.Dispose();
        _frameIndexReader?.Dispose();
        _dataReadStream?.Dispose();
        _indexReadStream?.Dispose();
        _frameIndexReadStream?.Dispose();
        TryDelete(_dataPath);
        TryDelete(_indexPath);
        TryDelete(_frameIndexPath);
    }

    public bool TryGetFrameSummary(long frameIndex, out string messageName, out int sampleCount)
    {
        if (!TryFindFrameRange(frameIndex, out var first, out sampleCount))
        {
            messageName = string.Empty;
            return false;
        }

        messageName = this[first].MessageName;
        return true;
    }

    public IReadOnlyList<DecodedSignalSample> GetFrameSamples(long frameIndex)
    {
        if (!TryFindFrameRange(frameIndex, out var first, out var count)) return [];
        var samples = new DecodedSignalSample[count];
        for (var i = 0; i < count; i++) samples[i] = this[first + i];
        return samples;
    }

    private IEnumerable<DecodedSignalSample> Enumerate()
    {
        using var stream = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, false);
        Validate(reader);
        for (var i = 0; i < Count; i++) yield return Read(reader);
    }

    private void EnsureReaders()
    {
        if (_dataReader is not null) return;
        _dataReadStream = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        _indexReadStream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        _frameIndexReadStream = new FileStream(_frameIndexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        _dataReader = new BinaryReader(_dataReadStream, Encoding.UTF8, true);
        _indexReader = new BinaryReader(_indexReadStream, Encoding.UTF8, true);
        _frameIndexReader = new BinaryReader(_frameIndexReadStream, Encoding.UTF8, true);
        Validate(_dataReader);
    }

    private void FinalizeActiveFrame()
    {
        if (_activeFrameIndex == long.MinValue || _activeFrameSampleCount == 0) return;
        _frameIndexWriter!.Write(_activeFrameIndex);
        _frameIndexWriter.Write(_activeFrameFirstSample);
        _frameIndexWriter.Write(_activeFrameSampleCount);
        _frameEntryCount++;
        _activeFrameSampleCount = 0;
    }

    private bool TryFindFrameRange(long frameIndex, out int first, out int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Complete();
        lock (_readLock)
        {
            EnsureReaders();
            var low = 0;
            var high = _frameEntryCount - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                _frameIndexReadStream!.Position = middle * 16L;
                var candidate = _frameIndexReader!.ReadInt64();
                var candidateFirst = _frameIndexReader.ReadInt32();
                var candidateCount = _frameIndexReader.ReadInt32();
                if (candidate == frameIndex)
                {
                    first = candidateFirst;
                    count = candidateCount;
                    return true;
                }

                if (candidate < frameIndex) low = middle + 1; else high = middle - 1;
            }
        }

        first = 0;
        count = 0;
        return false;
    }

    private static void Validate(BinaryReader reader)
    {
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadInt32() != 1)
            throw new InvalidDataException("Unsupported or corrupt CANalyser sample-store format.");
    }

    private static void Write(BinaryWriter writer, DecodedSignalSample sample)
    {
        writer.Write(sample.TimestampNanoseconds);
        writer.Write(sample.FrameIndex);
        writer.Write(sample.SourceLineNumber);
        writer.Write(sample.Identity.Channel);
        writer.Write((byte)sample.Identity.FrameFormat);
        writer.Write(sample.Identity.IsExtended);
        writer.Write(sample.Identity.FrameId);
        writer.Write(sample.Identity.MessageName);
        writer.Write(sample.Identity.SignalName);
        writer.Write(sample.Value);
        var raw = sample.RawValue.ToByteArray();
        writer.Write(raw.Length);
        writer.Write(raw);
        writer.Write(sample.Unit);
        writer.Write((byte)sample.Quality);
    }

    private static DecodedSignalSample Read(BinaryReader reader)
    {
        var timestamp = reader.ReadInt64();
        var frameIndex = reader.ReadInt64();
        var sourceLine = reader.ReadInt64();
        var channel = reader.ReadString();
        var format = (CanFrameFormat)reader.ReadByte();
        var extended = reader.ReadBoolean();
        var frameId = reader.ReadUInt32();
        var message = reader.ReadString();
        var signal = reader.ReadString();
        var value = reader.ReadDouble();
        var rawLength = reader.ReadInt32();
        if (rawLength is < 0 or > 1024) throw new InvalidDataException("Invalid raw-value length in sample store.");
        var rawBytes = reader.ReadBytes(rawLength);
        if (rawBytes.Length != rawLength) throw new EndOfStreamException("Truncated CANalyser sample store.");
        var unit = reader.ReadString();
        var quality = (DecodeQuality)reader.ReadByte();
        return new DecodedSignalSample(timestamp, frameIndex, sourceLine,
            new SignalIdentity(channel, format, extended, frameId, message, signal),
            value, new BigInteger(rawBytes), unit, quality);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
