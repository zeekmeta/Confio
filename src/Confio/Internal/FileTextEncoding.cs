using System;
using System.IO;
using System.Text;
using Confio.Formats;

namespace Confio.Internal;

// 格式层使用无 BOM 的 UTF-8；磁盘编码只在此边界转换，不影响密文载荷或密钥文件。
internal static class FileTextEncoding
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, true, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, true, true);
    private static readonly UTF32Encoding Utf32Le = new(false, true, true);
    private static readonly UTF32Encoding Utf32Be = new(true, true, true);

    internal static byte[] Decode(byte[] bytes, Encoding configured, FileFormat format)
    {
        var (encoding, offset) = ReadPreamble(bytes, configured);
        format.ValidateEncoding(encoding);
        try
        {
            if (encoding.CodePage != Utf8.CodePage)
                return Encoding.Convert(encoding, Utf8, bytes, offset, bytes.Length - offset);

            _ = Utf8.GetCharCount(bytes, offset, bytes.Length - offset);
            return offset == 0 ? bytes : bytes.AsSpan(offset).ToArray();
        }
        catch (DecoderFallbackException)
        {
            // 编码器异常可能含原始字节或字符，不携带原异常。
            throw new InvalidDataException($"The configuration file contains invalid {encoding.WebName} data.");
        }
    }

    internal static byte[] Encode(FileDocument document, Encoding encoding)
    {
        try
        {
            var utf8 = document.Encode();
            var content = encoding.CodePage == Utf8.CodePage ? utf8 : Encoding.Convert(Utf8, encoding, utf8);
            var preamble = encoding.GetPreamble();
            if (preamble.Length == 0) return content;
            var bytes = new byte[preamble.Length + content.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(content, 0, bytes, preamble.Length, content.Length);
            return bytes;
        }
        catch (Exception exception) when (exception is EncoderFallbackException or DecoderFallbackException)
        {
            throw new InvalidDataException($"The configuration content cannot be written losslessly using {encoding.WebName}.");
        }
    }

    private static (Encoding Encoding, int Offset) ReadPreamble(byte[] bytes, Encoding configured)
    {
        // UTF-32 LE 与 UTF-16 LE 前缀重叠，先识别较长的标记。
        if (bytes.AsSpan() is [0xff, 0xfe, 0, 0, ..]) return (Utf32Le, 4);
        if (bytes.AsSpan() is [0, 0, 0xfe, 0xff, ..]) return (Utf32Be, 4);
        if (bytes.AsSpan() is [0xef, 0xbb, 0xbf, ..]) return (Utf8, 3);
        if (bytes.AsSpan() is [0xff, 0xfe, ..]) return (Utf16Le, 2);
        if (bytes.AsSpan() is [0xfe, 0xff, ..]) return (Utf16Be, 2);
        return (configured, 0);
    }
}
