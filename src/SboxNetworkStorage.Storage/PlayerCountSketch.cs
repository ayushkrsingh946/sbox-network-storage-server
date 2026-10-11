using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SboxNetworkStorage.Storage;

/// <summary>1024-register HyperLogLog: ~3.25% relative error. Only merged registers persist, never player IDs/hashes.</summary>
public static class PlayerCountSketch
{
    public const int Size = 1024;
    public static void Add(byte[] sketch, string projectId, string playerId)
    {
        if (sketch.Length != Size) throw new ArgumentException("Invalid player sketch size.");
        if (playerId.Length is < 1 or > 20 || !playerId.All(char.IsAsciiDigit)) return;
        if (projectId.Length > 128) throw new ArgumentException("Project ID exceeds sketch bounds.");
        Span<byte> input = stackalloc byte[4 * 128 + 21];
        var length = Encoding.UTF8.GetBytes(projectId.AsSpan(), input);
        input[length++] = 0;
        length += Encoding.UTF8.GetBytes(playerId.AsSpan(), input[length..]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input[..length], digest);
        var hash = BinaryPrimitives.ReadUInt64BigEndian(digest);
        var index = (int)(hash >> 54);
        var rank = (byte)Math.Min(55, BitOperations.LeadingZeroCount(hash << 10) + 1);
        sketch[index] = Math.Max(sketch[index], rank);
    }
    public static void Merge(byte[] target, byte[] source)
    {
        if (target.Length != Size || source.Length != Size || source.Any(rank => rank > 55)) throw new ArgumentException("Invalid player sketch.");
        for (var i = 0; i < Size; i++) target[i] = Math.Max(target[i], source[i]);
    }
    public static long Estimate(byte[] sketch)
    {
        if (sketch.Length != Size) throw new ArgumentException("Invalid player sketch size.");
        var sum = 0d;
        var zeros = 0;
        foreach (var rank in sketch) { sum += Math.Pow(2, -rank); if (rank == 0) zeros++; }
        var estimate = 0.7213 / (1 + 1.079 / Size) * Size * Size / sum;
        if (estimate <= 2.5 * Size && zeros > 0) estimate = Size * Math.Log((double)Size / zeros);
        return (long)Math.Round(estimate);
    }
}
