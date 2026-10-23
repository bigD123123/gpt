using System.Buffers.Binary;
using System.Globalization;

namespace TrainerStudio.Core;

/// <summary>
/// Data types exposed by the beginner scanner. The scanner keeps the source
/// bytes, so an address can later be reinterpreted in advanced mode.
/// </summary>
public enum ScanDataType
{
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    Boolean
}

public enum ScanCondition
{
    ExactValue,
    UnknownInitialValue,
    Increased,
    Decreased,
    Changed,
    Unchanged
}

/// <summary>
/// Native alignment is the fast, beginner-friendly mode used by the owned
/// test target. EveryByte is reserved for a later advanced view and makes the
/// scanner inspect unaligned values as well.
/// </summary>
public enum ScanAlignment
{
    Native,
    EveryByte
}

public sealed record TargetIdentity(
    string TargetId,
    string DisplayName,
    string Version,
    string Architecture,
    string ModuleName,
    string BuildFingerprint);

public sealed record MemoryRegion(long BaseAddress, byte[] Bytes, string Name)
{
    public long EndAddress => checked(BaseAddress + Bytes.LongLength);
}

/// <summary>
/// A target must be explicitly selected and supplied by the application.
/// The scanner does not enumerate processes or acquire access itself.
/// </summary>
public interface IApprovedMemoryTarget
{
    TargetIdentity Identity { get; }

    /// <summary>
    /// Resolves a module name to its base address in this approved target.
    /// Profiles store module-relative offsets instead of raw session addresses.
    /// </summary>
    bool TryResolveModuleBase(string moduleName, out long baseAddress);

    IReadOnlyList<MemoryRegion> Snapshot();

    bool TryRead(long address, ScanDataType type, out ScanValue value);

    bool TryWrite(long address, ScanValue value, out string error);
}

/// <summary>
/// Optional bounded streaming source. Chunks are non-overlapping and ordered
/// within each contiguous range; separate ranges may be priority-ordered.
/// Consecutive chunks from one readable region use
/// the same name, so scanners can inspect values spanning a chunk boundary.
/// </summary>
public interface IStreamingMemoryTarget
{
    IEnumerable<MemoryRegion> EnumerateMemory(CancellationToken cancellationToken);
}

/// <summary>
/// Identifies one lifetime of a target. A restarted process must receive a new
/// session id even if its executable, profile identity or PID is unchanged.
/// </summary>
public interface ISessionMemoryTarget
{
    string SessionId { get; }

    bool IsAlive { get; }
}

public sealed record TargetModule(string Name, long BaseAddress, long Size, string? Path = null)
{
    public long EndAddress => checked(BaseAddress + Size);
}

public interface IModuleMemoryTarget
{
    IReadOnlyList<TargetModule> Modules { get; }
}

/// <summary>
/// Reports whether an enumerated snapshot stopped at a configured byte budget
/// or skipped unreadable blocks. This is distinct from the candidate cap.
/// </summary>
public interface ISnapshotDiagnosticsTarget
{
    bool WasSnapshotTruncated { get; }

    long SnapshotBytesRead { get; }

    int SkippedReadCount { get; }
}

public readonly record struct ScanValue(ScanDataType Type, byte[] Bytes)
{
    public int Size => ValueCodec.SizeOf(Type);

    public string Display => ValueCodec.Format(this);

    public static ScanValue Parse(ScanDataType type, string text) => ValueCodec.Parse(type, text);

    public double AsDouble() => ValueCodec.AsDouble(this);

    public bool NumericallyEquals(ScanValue other) =>
        Type == other.Type && ValueCodec.NumericallyEquals(this, other);
}

public static class ValueCodec
{
    public static int SizeOf(ScanDataType type) => type switch
    {
        ScanDataType.Int8 or ScanDataType.UInt8 or ScanDataType.Boolean => 1,
        ScanDataType.Int16 or ScanDataType.UInt16 => 2,
        ScanDataType.Int32 or ScanDataType.UInt32 or ScanDataType.Float => 4,
        ScanDataType.Int64 or ScanDataType.UInt64 or ScanDataType.Double => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static bool TryRead(ReadOnlySpan<byte> source, ScanDataType type, out ScanValue value)
    {
        var size = SizeOf(type);
        if (source.Length < size)
        {
            value = default;
            return false;
        }

        value = new ScanValue(type, source[..size].ToArray());
        return true;
    }

    public static ScanValue Parse(ScanDataType type, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("Skriv en værdi før søgningen startes.");
        }

        var bytes = new byte[SizeOf(type)];
        switch (type)
        {
            case ScanDataType.Int8:
                bytes[0] = unchecked((byte)sbyte.Parse(text, CultureInfo.InvariantCulture));
                break;
            case ScanDataType.UInt8:
                bytes[0] = byte.Parse(text, CultureInfo.InvariantCulture);
                break;
            case ScanDataType.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(bytes, short.Parse(text, CultureInfo.InvariantCulture));
                break;
            case ScanDataType.UInt16:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, ushort.Parse(text, CultureInfo.InvariantCulture));
                break;
            case ScanDataType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, int.Parse(text, CultureInfo.InvariantCulture));
                break;
            case ScanDataType.UInt32:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.Parse(text, CultureInfo.InvariantCulture));
                break;
            case ScanDataType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(bytes, long.Parse(text, CultureInfo.InvariantCulture));
                break;
            case ScanDataType.UInt64:
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, ulong.Parse(text, CultureInfo.InvariantCulture));
                break;
            case ScanDataType.Float:
                var single = float.Parse(text, CultureInfo.InvariantCulture);
                if (!float.IsFinite(single))
                {
                    throw new FormatException("Float-værdien skal være et endeligt tal.");
                }

                BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(single));
                break;
            case ScanDataType.Double:
                var @double = double.Parse(text, CultureInfo.InvariantCulture);
                if (!double.IsFinite(@double))
                {
                    throw new FormatException("Double-værdien skal være et endeligt tal.");
                }

                BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(@double));
                break;
            case ScanDataType.Boolean:
                bytes[0] = bool.Parse(text) ? (byte)1 : (byte)0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        return new ScanValue(type, bytes);
    }

    public static string Format(ScanValue value)
    {
        var bytes = value.Bytes.AsSpan();
        return value.Type switch
        {
            ScanDataType.Int8 => unchecked((sbyte)bytes[0]).ToString(CultureInfo.InvariantCulture),
            ScanDataType.UInt8 => bytes[0].ToString(CultureInfo.InvariantCulture),
            ScanDataType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ScanDataType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ScanDataType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ScanDataType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ScanDataType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ScanDataType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            ScanDataType.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)).ToString("G9", CultureInfo.InvariantCulture),
            ScanDataType.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes)).ToString("G17", CultureInfo.InvariantCulture),
            ScanDataType.Boolean => (bytes[0] != 0).ToString(),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public static double AsDouble(ScanValue value)
    {
        var bytes = value.Bytes.AsSpan();
        return value.Type switch
        {
            ScanDataType.Int8 => unchecked((sbyte)bytes[0]),
            ScanDataType.UInt8 => bytes[0],
            ScanDataType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            ScanDataType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            ScanDataType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            ScanDataType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            ScanDataType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            ScanDataType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            ScanDataType.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            ScanDataType.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
            ScanDataType.Boolean => bytes[0] != 0 ? 1 : 0,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public static bool NumericallyEquals(ScanValue left, ScanValue right)
    {
        if (left.Type != right.Type)
        {
            return false;
        }

        return left.Type is ScanDataType.Float or ScanDataType.Double
            ? left.AsDouble().Equals(right.AsDouble())
            : left.Bytes.AsSpan().SequenceEqual(right.Bytes);
    }

    /// <summary>
    /// Compares values without converting integer values through double, which
    /// would lose precision for large Int64 and UInt64 values.
    /// </summary>
    public static bool TryCompare(ScanValue left, ScanValue right, out int comparison)
    {
        comparison = 0;
        if (left.Type != right.Type)
        {
            return false;
        }

        var leftBytes = left.Bytes.AsSpan();
        var rightBytes = right.Bytes.AsSpan();
        switch (left.Type)
        {
            case ScanDataType.Int8:
                comparison = unchecked((sbyte)leftBytes[0]).CompareTo(unchecked((sbyte)rightBytes[0]));
                return true;
            case ScanDataType.UInt8:
                comparison = leftBytes[0].CompareTo(rightBytes[0]);
                return true;
            case ScanDataType.Int16:
                comparison = BinaryPrimitives.ReadInt16LittleEndian(leftBytes).CompareTo(BinaryPrimitives.ReadInt16LittleEndian(rightBytes));
                return true;
            case ScanDataType.UInt16:
                comparison = BinaryPrimitives.ReadUInt16LittleEndian(leftBytes).CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(rightBytes));
                return true;
            case ScanDataType.Int32:
                comparison = BinaryPrimitives.ReadInt32LittleEndian(leftBytes).CompareTo(BinaryPrimitives.ReadInt32LittleEndian(rightBytes));
                return true;
            case ScanDataType.UInt32:
                comparison = BinaryPrimitives.ReadUInt32LittleEndian(leftBytes).CompareTo(BinaryPrimitives.ReadUInt32LittleEndian(rightBytes));
                return true;
            case ScanDataType.Int64:
                comparison = BinaryPrimitives.ReadInt64LittleEndian(leftBytes).CompareTo(BinaryPrimitives.ReadInt64LittleEndian(rightBytes));
                return true;
            case ScanDataType.UInt64:
                comparison = BinaryPrimitives.ReadUInt64LittleEndian(leftBytes).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(rightBytes));
                return true;
            case ScanDataType.Float:
            {
                var leftValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(leftBytes));
                var rightValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(rightBytes));
                if (!float.IsFinite(leftValue) || !float.IsFinite(rightValue))
                {
                    return false;
                }

                comparison = leftValue.CompareTo(rightValue);
                return true;
            }
            case ScanDataType.Double:
            {
                var leftValue = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(leftBytes));
                var rightValue = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(rightBytes));
                if (!double.IsFinite(leftValue) || !double.IsFinite(rightValue))
                {
                    return false;
                }

                comparison = leftValue.CompareTo(rightValue);
                return true;
            }
            case ScanDataType.Boolean:
                comparison = (leftBytes[0] != 0).CompareTo(rightBytes[0] != 0);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Adds two values of the same type with checked integer arithmetic. This
    /// is used by the declarative “add value” trainer action.
    /// </summary>
    public static ScanValue Add(ScanValue left, ScanValue delta)
    {
        if (left.Type != delta.Type)
        {
            throw new ArgumentException("Værdierne har forskellige datatyper.");
        }

        var leftBytes = left.Bytes.AsSpan();
        var deltaBytes = delta.Bytes.AsSpan();
        var bytes = new byte[SizeOf(left.Type)];
        switch (left.Type)
        {
            case ScanDataType.Int8:
                bytes[0] = unchecked((byte)checked((sbyte)(unchecked((sbyte)leftBytes[0]) + unchecked((sbyte)deltaBytes[0]))));
                break;
            case ScanDataType.UInt8:
                bytes[0] = checked((byte)(leftBytes[0] + deltaBytes[0]));
                break;
            case ScanDataType.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(bytes, checked((short)(BinaryPrimitives.ReadInt16LittleEndian(leftBytes) + BinaryPrimitives.ReadInt16LittleEndian(deltaBytes))));
                break;
            case ScanDataType.UInt16:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(leftBytes) + BinaryPrimitives.ReadUInt16LittleEndian(deltaBytes))));
                break;
            case ScanDataType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, checked(BinaryPrimitives.ReadInt32LittleEndian(leftBytes) + BinaryPrimitives.ReadInt32LittleEndian(deltaBytes)));
                break;
            case ScanDataType.UInt32:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked(BinaryPrimitives.ReadUInt32LittleEndian(leftBytes) + BinaryPrimitives.ReadUInt32LittleEndian(deltaBytes)));
                break;
            case ScanDataType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(bytes, checked(BinaryPrimitives.ReadInt64LittleEndian(leftBytes) + BinaryPrimitives.ReadInt64LittleEndian(deltaBytes)));
                break;
            case ScanDataType.UInt64:
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, checked(BinaryPrimitives.ReadUInt64LittleEndian(leftBytes) + BinaryPrimitives.ReadUInt64LittleEndian(deltaBytes)));
                break;
            case ScanDataType.Float:
            {
                var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(leftBytes)) +
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(deltaBytes));
                if (!float.IsFinite(value))
                {
                    throw new OverflowException("Float-resultatet er ikke et endeligt tal.");
                }

                BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
                break;
            }
            case ScanDataType.Double:
            {
                var value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(leftBytes)) +
                    BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(deltaBytes));
                if (!double.IsFinite(value))
                {
                    throw new OverflowException("Double-resultatet er ikke et endeligt tal.");
                }

                BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
                break;
            }
            case ScanDataType.Boolean:
                throw new InvalidOperationException("Sand/falsk kan ikke øges eller reduceres.");
            default:
                throw new ArgumentOutOfRangeException(nameof(left));
        }

        return new ScanValue(left.Type, bytes);
    }

    /// <summary>
    /// Returns whether a slider value lands on its configured step from the
    /// minimum. Integer arithmetic stays exact, including UInt64 values.
    /// </summary>
    public static bool IsAlignedToStep(ScanValue value, ScanValue minimum, ScanValue step)
    {
        if (value.Type != minimum.Type || value.Type != step.Type || value.Type == ScanDataType.Boolean)
        {
            return false;
        }

        if (value.Type is ScanDataType.Float or ScanDataType.Double)
        {
            var valueAsDouble = value.AsDouble();
            var minimumAsDouble = minimum.AsDouble();
            var stepAsDouble = step.AsDouble();
            if (!double.IsFinite(valueAsDouble) || !double.IsFinite(minimumAsDouble) || !double.IsFinite(stepAsDouble) || stepAsDouble <= 0)
            {
                return false;
            }

            var quotient = (valueAsDouble - minimumAsDouble) / stepAsDouble;
            var nearest = Math.Round(quotient);
            // Float inputs carry single-precision rounding already. Decimal
            // steps such as 0.1 must not reject 0.3 merely for binary rounding.
            var tolerance = value.Type == ScanDataType.Float
                ? Math.Min(1e-4, 4 * 1.1920928955078125e-7 * Math.Max(1d, Math.Abs(quotient)))
                : Math.Min(1e-4, 1e-9 * Math.Max(1d, Math.Abs(quotient)));
            return double.IsFinite(quotient) && Math.Abs(quotient - nearest) <= tolerance;
        }

        var stepAsDecimal = AsDecimal(step);
        if (stepAsDecimal <= 0m)
        {
            return false;
        }

        var remainder = (AsDecimal(value) - AsDecimal(minimum)) % stepAsDecimal;
        return remainder == 0m;
    }

    private static decimal AsDecimal(ScanValue value)
    {
        var bytes = value.Bytes.AsSpan();
        return value.Type switch
        {
            ScanDataType.Int8 => unchecked((sbyte)bytes[0]),
            ScanDataType.UInt8 => bytes[0],
            ScanDataType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            ScanDataType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            ScanDataType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            ScanDataType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            ScanDataType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            ScanDataType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            _ => throw new ArgumentException("Værdien er ikke et heltal.", nameof(value))
        };
    }
}
