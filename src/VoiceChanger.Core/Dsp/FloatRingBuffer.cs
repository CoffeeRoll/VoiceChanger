namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer of samples.
/// <para>
/// Exactly one thread may call <see cref="Write"/> and exactly one (other) thread may call
/// <see cref="Read"/>, <see cref="Peek"/> and <see cref="Skip"/>. <see cref="Count"/> may be read
/// from either side; it is a snapshot that can only grow for the reader and only shrink for the writer.
/// </para>
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private long _writePosition;
    private long _readPosition;

    public FloatRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new float[capacity];
    }

    public int Capacity => _buffer.Length;

    /// <summary>Number of samples that can currently be read.</summary>
    public int Count => (int)(Volatile.Read(ref _writePosition) - Volatile.Read(ref _readPosition));

    /// <summary>Number of samples that can currently be written.</summary>
    public int Free => Capacity - Count;

    /// <summary>Writes as many samples as fit and returns how many were written.</summary>
    public int Write(ReadOnlySpan<float> source)
    {
        long write = Volatile.Read(ref _writePosition);
        long read = Volatile.Read(ref _readPosition);
        int count = Math.Min(source.Length, Capacity - (int)(write - read));
        if (count <= 0)
        {
            return 0;
        }

        int start = (int)(write % Capacity);
        int first = Math.Min(count, Capacity - start);
        source[..first].CopyTo(_buffer.AsSpan(start, first));
        source.Slice(first, count - first).CopyTo(_buffer);

        Volatile.Write(ref _writePosition, write + count);
        return count;
    }

    /// <summary>Reads up to <c>destination.Length</c> samples and returns how many were read.</summary>
    public int Read(Span<float> destination)
    {
        int count = Peek(0, destination);
        if (count > 0)
        {
            Volatile.Write(ref _readPosition, Volatile.Read(ref _readPosition) + count);
        }

        return count;
    }

    /// <summary>Copies samples starting <paramref name="offset"/> samples after the read position without consuming them.</summary>
    public int Peek(int offset, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        long read = Volatile.Read(ref _readPosition);
        long write = Volatile.Read(ref _writePosition);
        int count = Math.Min(destination.Length, (int)(write - read) - offset);
        if (count <= 0)
        {
            return 0;
        }

        int start = (int)((read + offset) % Capacity);
        int first = Math.Min(count, Capacity - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
        return count;
    }

    /// <summary>Discards up to <paramref name="count"/> samples and returns how many were discarded.</summary>
    public int Skip(int count)
    {
        long read = Volatile.Read(ref _readPosition);
        long write = Volatile.Read(ref _writePosition);
        count = Math.Clamp(count, 0, (int)(write - read));
        Volatile.Write(ref _readPosition, read + count);
        return count;
    }

    /// <summary>Empties the buffer. Only call while neither side is active.</summary>
    public void Clear()
    {
        Volatile.Write(ref _readPosition, 0);
        Volatile.Write(ref _writePosition, 0);
    }
}
