using System.Text;

namespace Xbank.DevAgent.Api;

internal static class UnicodeDocumentText
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(false, false, true);
    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(true, false, true);
    private static readonly Encoding Utf32LittleEndian = new UTF32Encoding(false, false, true);
    private static readonly Encoding Utf32BigEndian = new UTF32Encoding(true, false, true);

    internal static string Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> content = bytes;
        Encoding encoding = Utf8;
        // UTF-32 LE starts with the UTF-16 LE marker, so check four-byte markers first.
        if (content.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            encoding = Utf32LittleEndian;
            content = content[4..];
        }
        else if (content.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            encoding = Utf32BigEndian;
            content = content[4..];
        }
        else if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            content = content[3..];
        }
        else if (content.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            encoding = Utf16LittleEndian;
            content = content[2..];
        }
        else if (content.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            encoding = Utf16BigEndian;
            content = content[2..];
        }
        else if (content.Length >= 2)
        {
            // Without a marker, infer endianness only from a consistent zero-byte lane.
            // This covers ASCII and mixed-language Unicode documents without guessing
            // legacy code pages or treating arbitrary invalid UTF-8 as UTF-16.
            if (content.Length >= 4 && content.Length % 4 == 0 && ZeroFraction(content, 4, 2) >= 0.8 && ZeroFraction(content, 4, 3) >= 0.8)
                encoding = Utf32LittleEndian;
            else if (content.Length >= 4 && content.Length % 4 == 0 && ZeroFraction(content, 4, 0) >= 0.8 && ZeroFraction(content, 4, 1) >= 0.8)
                encoding = Utf32BigEndian;
            else if (content.Length % 2 == 0)
            {
                double even = ZeroFraction(content, 2, 0);
                double odd = ZeroFraction(content, 2, 1);
                if (odd >= 0.2 && odd > even * 2) encoding = Utf16LittleEndian;
                else if (even >= 0.2 && even > odd * 2) encoding = Utf16BigEndian;
            }
        }

        try
        {
            return encoding.GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("Attachment text has an invalid or unrecognized encoding. Save it as UTF-8 or UTF-16/UTF-32 with a byte-order mark.", exception);
        }
    }

    private static double ZeroFraction(ReadOnlySpan<byte> bytes, int stride, int lane)
    {
        int zeros = 0;
        for (int index = lane; index < bytes.Length; index += stride)
            if (bytes[index] == 0) zeros++;
        return (double)zeros / (bytes.Length / stride);
    }
}
