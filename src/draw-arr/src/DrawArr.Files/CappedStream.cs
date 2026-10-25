namespace DrawArr.Files;

/// <summary>A read-only view of a stream that stops with an error past a byte limit, in case a file grows after its size was checked.</summary>
internal sealed class CappedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        _read += n;
        // DrawingFile.Load turns this into a failed Result saying the file is too big.
        if (_read > limit)
            throw new InvalidDataException($"The file grew past {limit} bytes while it was being read.");
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
