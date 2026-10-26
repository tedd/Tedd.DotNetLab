using System.Buffers.Binary;
using System.Text.Json;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Transport.Local;

public static class Frames
{
    public const int MaximumBytes = 1048576;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellation)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json);
        if (bytes.Length > MaximumBytes) throw new LabException("resource_limit", "Control frame exceeds 1 MiB.");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellation);
        await stream.WriteAsync(bytes, cancellation);
        await stream.FlushAsync(cancellation);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellation);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumBytes) throw new LabException("resource_limit", "Invalid control frame length.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellation);
        return JsonSerializer.Deserialize<T>(bytes, Wire.Json) ?? throw new LabException("invalid_request", "Null control frame.");
    }
}
