using System.Collections;
using System.Text;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Storage;

/// <summary>
/// Append-only binary frame store. Payloads and strings remain on disk; enumeration is sequential and the
/// compact 8-byte offset index enables direct access without retaining frame objects in memory.
/// </summary>
public sealed class DiskBackedFrameStore : IReadOnlyList<RawCanFrame>, IDisposable
{
    private const int FormatVersion = 1;
    private static readonly byte[] Magic = "CANFRM2\0"u8.ToArray();
    private readonly string _dataPath;
    private readonly string _indexPath;
    private readonly object _readLock = new();
    private FileStream? _dataWriterStream;
    private FileStream? _indexWriterStream;
    private BinaryWriter? _dataWriter;
    private BinaryWriter? _indexWriter;
    private FileStream? _dataReaderStream;
    private FileStream? _indexReaderStream;
    private BinaryReader? _dataReader;
    private BinaryReader? _indexReader;
    private bool _completed;
    private bool _disposed;

    public DiskBackedFrameStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CANalyser", "frame-cache");
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        _dataPath = Path.Combine(directory, $"{id}.frames");
        _indexPath = Path.Combine(directory, $"{id}.index");
        _dataWriterStream = new FileStream(_dataPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        _indexWriterStream = new FileStream(_indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        _dataWriter = new BinaryWriter(_dataWriterStream, Encoding.UTF8, leaveOpen: true);
        _indexWriter = new BinaryWriter(_indexWriterStream, Encoding.UTF8, leaveOpen: true);
        _dataWriter.Write(Magic);
        _dataWriter.Write(FormatVersion);
    }

    public int Count { get; private set; }

    public string BackingFilePath => _dataPath;

    public RawCanFrame this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            Complete();
            lock (_readLock)
            {
                EnsureReaders();
                _indexReaderStream!.Position = index * sizeof(long);
                var offset = _indexReader!.ReadInt64();
                _dataReaderStream!.Position = offset;
                return ReadFrame(_dataReader!);
            }
        }
    }

    public void Append(RawCanFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The frame store is already completed.");
        if (Count == int.MaxValue) throw new IOException("The frame-store count exceeds the supported 32-bit list index.");
        _indexWriter!.Write(_dataWriterStream!.Position);
        WriteFrame(_dataWriter!, frame);
        Count++;
    }

    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) return;
        _dataWriter!.Flush();
        _indexWriter!.Flush();
        _dataWriter.Dispose();
        _indexWriter.Dispose();
        _dataWriterStream!.Dispose();
        _indexWriterStream!.Dispose();
        _dataWriter = null;
        _indexWriter = null;
        _dataWriterStream = null;
        _indexWriterStream = null;
        _completed = true;
    }

    public IEnumerator<RawCanFrame> GetEnumerator()
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
        _dataWriterStream?.Dispose();
        _indexWriterStream?.Dispose();
        _dataReader?.Dispose();
        _indexReader?.Dispose();
        _dataReaderStream?.Dispose();
        _indexReaderStream?.Dispose();
        TryDelete(_dataPath);
        TryDelete(_indexPath);
    }

    private IEnumerable<RawCanFrame> Enumerate()
    {
        using var stream = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        ValidateHeader(reader);
        for (var i = 0; i < Count; i++) yield return ReadFrame(reader);
    }

    private void EnsureReaders()
    {
        if (_dataReader is not null) return;
        _dataReaderStream = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        _indexReaderStream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        _dataReader = new BinaryReader(_dataReaderStream, Encoding.UTF8, leaveOpen: true);
        _indexReader = new BinaryReader(_indexReaderStream, Encoding.UTF8, leaveOpen: true);
        ValidateHeader(_dataReader);
    }

    private static void ValidateHeader(BinaryReader reader)
    {
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
            throw new InvalidDataException("Unsupported or corrupt CANalyser frame-store format.");
    }

    private static void WriteFrame(BinaryWriter writer, RawCanFrame frame)
    {
        writer.Write(frame.TimestampNanoseconds);
        writer.Write(frame.Id);
        writer.Write(frame.Dlc);
        writer.Write(frame.FrameIndex);
        writer.Write(frame.SourceLineNumber);
        writer.Write((byte)frame.FrameFormat);
        writer.Write((byte)frame.Direction);
        writer.Write((byte)frame.Kind);
        var flags = (byte)((frame.IsExtended ? 1 : 0) | (frame.BitRateSwitch ? 2 : 0) | (frame.ErrorStateIndicator ? 4 : 0));
        writer.Write(flags);
        writer.Write(frame.Type ?? string.Empty);
        writer.Write(frame.Channel ?? string.Empty);
        writer.Write((byte)frame.Data.Length);
        writer.Write(frame.Data);
    }

    private static RawCanFrame ReadFrame(BinaryReader reader)
    {
        var timestamp = reader.ReadInt64();
        var id = reader.ReadUInt32();
        var dlc = reader.ReadByte();
        var frameIndex = reader.ReadInt64();
        var line = reader.ReadInt64();
        var format = (CanFrameFormat)reader.ReadByte();
        var direction = (CanFrameDirection)reader.ReadByte();
        var kind = (CanFrameKind)reader.ReadByte();
        var flags = reader.ReadByte();
        var type = reader.ReadString();
        var channel = reader.ReadString();
        var length = reader.ReadByte();
        var data = reader.ReadBytes(length);
        if (data.Length != length) throw new EndOfStreamException("Truncated CANalyser frame store.");
        return new RawCanFrame(timestamp, id, dlc, data, type, channel, (flags & 1) != 0,
            frameIndex, line, format, direction, kind, (flags & 2) != 0, (flags & 4) != 0);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
