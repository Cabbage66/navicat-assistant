using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace NavicatTextProbe.Services;

internal static class ContentHasher
{
    private const int CharacterChunkSize = 4096;

    public static string Sha256(string text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var byteBuffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(CharacterChunkSize));
        try
        {
            var offset = 0;
            while (offset < text.Length)
            {
                var count = Math.Min(CharacterChunkSize, text.Length - offset);
                if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]))
                {
                    count--;
                }

                var bytesWritten = Encoding.UTF8.GetBytes(
                    text.AsSpan(offset, count),
                    byteBuffer.AsSpan());
                hash.AppendData(byteBuffer, 0, bytesWritten);
                offset += count;
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer);
        }
    }
}
