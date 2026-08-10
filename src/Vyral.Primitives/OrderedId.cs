using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace Vyral.Primitives;

public readonly record struct OrderedId(long Value) : IComparable<OrderedId>
{
    public const int SortableStringLength = 20;

    private const long EpochMicroseconds = 1_666_777_777_000_000L;
    private const int NodeBits = 9;
    private const int CounterBits = 4;
    private const int MaxNodes = 1 << NodeBits;
    private const int MaxCounter = 1 << CounterBits;
    private const int ShiftBits = NodeBits + CounterBits;
    private const long CounterMask = MaxCounter - 1;
    private const long NodeMask = MaxNodes - 1;

    private static readonly object Lock = new();
    private static long _lastTimestamp = -1;
    private static int _counter;
    private static int? _nodeId;

    public static OrderedId Create()
    {
        EnsureNodeId();

        lock (Lock)
        {
            var timestamp = CurrentTimestampMicroseconds();
            if (timestamp < _lastTimestamp)
            {
                timestamp = _lastTimestamp;
            }

            if (timestamp == _lastTimestamp)
            {
                _counter++;
                if (_counter >= MaxCounter)
                {
                    timestamp = WaitForNextMicrosecond(_lastTimestamp);
                    _counter = 0;
                }
            }
            else
            {
                _counter = 0;
            }

            _lastTimestamp = timestamp;
            return new OrderedId((timestamp << ShiftBits) | ((long)_nodeId!.Value << CounterBits) | (long)_counter);
        }
    }

    public static string CreateString()
    {
        return Create().ToString();
    }

    public static OrderedId CreateJittered(int maxJitterMicroseconds = 100)
    {
        ApplyJitter(maxJitterMicroseconds);
        return Create();
    }

    public static string CreateJitteredString(int maxJitterMicroseconds = 100)
    {
        return CreateJittered(maxJitterMicroseconds).ToString();
    }

    public static bool TryCreate(out OrderedId orderedId)
    {
        try
        {
            orderedId = Create();
            return true;
        }
        catch
        {
            orderedId = default;
            return false;
        }
    }

    public static bool TryCreateString(out string orderedId)
    {
        if (TryCreate(out var value))
        {
            orderedId = value.ToString();
            return true;
        }

        orderedId = string.Empty;
        return false;
    }

    public static string CreateStringOrFallback(string prefix = "OID-ERR")
    {
        if (TryCreateString(out var orderedId))
        {
            return orderedId;
        }

        return CreateFallbackString(prefix);
    }

    public static OrderedId Parse(string value)
    {
        if (!TryParse(value, out var orderedId))
        {
            throw new FormatException("OrderedId must be a positive decimal Int64 value.");
        }

        return orderedId;
    }

    public static bool TryParse(string? value, out OrderedId orderedId)
    {
        orderedId = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return false;
        }

        orderedId = new OrderedId(parsed);
        return true;
    }

    public static OrderedId Reference(DateTimeOffset timestamp, OrderedIdReferenceDirection direction)
    {
        var microseconds = ToUnixMicroseconds(timestamp) - EpochMicroseconds;
        if (microseconds <= 0)
        {
            return new OrderedId(0);
        }

        var node = direction == OrderedIdReferenceDirection.Before ? 0 : NodeMask;
        var counter = direction == OrderedIdReferenceDirection.Before ? 0 : CounterMask;
        return new OrderedId((microseconds << ShiftBits) | (node << CounterBits) | counter);
    }

    public OrderedIdParts Decompose()
    {
        var timestamp = Value >> ShiftBits;
        var node = (int)((Value >> CounterBits) & NodeMask);
        var sequence = (int)(Value & CounterMask);
        return new OrderedIdParts(
            DateTimeOffset.FromUnixTimeMilliseconds((EpochMicroseconds + timestamp) / 1_000),
            node,
            sequence);
    }

    public int CompareTo(OrderedId other)
    {
        return Value.CompareTo(other.Value);
    }

    public override string ToString()
    {
        return Value.ToString("D20", CultureInfo.InvariantCulture);
    }

    private static void EnsureNodeId()
    {
        if (_nodeId.HasValue)
        {
            return;
        }

        lock (Lock)
        {
            _nodeId ??= DeriveNodeId();
        }
    }

    private static int DeriveNodeId()
    {
        var configuredNode = Environment.GetEnvironmentVariable("VYRAL_ORDERED_ID_NODE");
        if (!string.IsNullOrWhiteSpace(configuredNode))
        {
            if (int.TryParse(configuredNode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nodeId))
            {
                return NormalizeNodeId(nodeId);
            }

            return HashNodeSource(configuredNode);
        }

        var parts = new[]
        {
            Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"),
            Environment.MachineName,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            ReadNetworkAddressSource()
        };

        var source = string.Join("|", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

        if (string.IsNullOrWhiteSpace(source))
        {
            source = Guid.NewGuid().ToString("N");
        }

        return HashNodeSource(source);
    }

    private static int HashNodeSource(string source)
    {
        try
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            return NormalizeNodeId((int)(BitConverter.ToUInt32(hash, 0) % MaxNodes));
        }
        catch
        {
            return MaxNodes - 1;
        }
    }

    private static int NormalizeNodeId(int nodeId)
    {
        var normalized = nodeId % MaxNodes;
        if (normalized < 0)
        {
            normalized += MaxNodes;
        }

        return normalized == MaxNodes - 1 ? MaxNodes - 2 : normalized;
    }

    private static string? ReadNetworkAddressSource()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Select(item => item.GetPhysicalAddress().ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyJitter(int maxJitterMicroseconds)
    {
        if (maxJitterMicroseconds <= 0)
        {
            return;
        }

        var exclusiveUpperBound = maxJitterMicroseconds == int.MaxValue
            ? int.MaxValue
            : maxJitterMicroseconds + 1;
        var jitter = RandomNumberGenerator.GetInt32(exclusiveUpperBound);
        if (jitter <= 0)
        {
            return;
        }

        Thread.Sleep(TimeSpan.FromMicroseconds(jitter));
    }

    private static string CreateFallbackString(string prefix)
    {
        prefix = string.IsNullOrWhiteSpace(prefix) ? "OID-ERR" : prefix.Trim();
        try
        {
            Span<byte> bytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(bytes);
            return $"{prefix}-{Convert.ToHexString(bytes)}";
        }
        catch
        {
            return $"{prefix}-{Guid.NewGuid():N}";
        }
    }

    private static long WaitForNextMicrosecond(long lastTimestamp)
    {
        var timestamp = CurrentTimestampMicroseconds();
        while (timestamp <= lastTimestamp)
        {
            Thread.Yield();
            timestamp = CurrentTimestampMicroseconds();
        }

        return timestamp;
    }

    private static long CurrentTimestampMicroseconds()
    {
        return ToUnixMicroseconds(DateTimeOffset.UtcNow) - EpochMicroseconds;
    }

    private static long ToUnixMicroseconds(DateTimeOffset timestamp)
    {
        return timestamp.ToUnixTimeMilliseconds() * 1_000 + (timestamp.Ticks % TimeSpan.TicksPerMillisecond) / 10;
    }
}

public enum OrderedIdReferenceDirection
{
    Before,
    After
}

public sealed record OrderedIdParts(DateTimeOffset TimestampUtc, int NodeId, int Sequence);
