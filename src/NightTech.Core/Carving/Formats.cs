using System.Text;

namespace NightTech.Core.Carving;

public enum FileKind { Jpeg, Png, Gif, Bmp, Heic, Avif, Mp4, Mov, M4a, ThreeGp, Pdf, Docx, Xlsx, Pptx, Zip }

/// <summary>What a found file is and how long it is.</summary>
internal readonly record struct Measured(FileKind Kind, long Length);

/// <summary>
/// Recognizes each file type by how it starts and follows its structure to where it ends. Anything that doesn't
/// follow the structure is rejected, which keeps random data and damaged leftovers out of the results.
/// </summary>
internal static class Formats
{
    public static string Extension(FileKind k) => k switch
    {
        FileKind.Jpeg => "jpg", FileKind.Png => "png", FileKind.Gif => "gif", FileKind.Bmp => "bmp", FileKind.Heic => "heic", FileKind.Avif => "avif",
        FileKind.Mp4 => "mp4", FileKind.Mov => "mov", FileKind.M4a => "m4a", FileKind.ThreeGp => "3gp", FileKind.Pdf => "pdf",
        FileKind.Docx => "docx", FileKind.Xlsx => "xlsx", FileKind.Pptx => "pptx", _ => "zip",
    };

    public static string Category(FileKind k) => k switch
    {
        FileKind.Jpeg or FileKind.Png or FileKind.Gif or FileKind.Bmp or FileKind.Heic or FileKind.Avif => "Photos",
        FileKind.Mp4 or FileKind.Mov or FileKind.ThreeGp => "Videos",
        FileKind.M4a => "Audio",
        FileKind.Pdf or FileKind.Docx or FileKind.Xlsx or FileKind.Pptx => "Documents",
        _ => "Archives",
    };

    /// <summary>Quick look at the first bytes of a sector: which parser, if any, should look closer.</summary>
    public static Func<ByteSource, long, CarveOptions, Measured?>? Candidate(ReadOnlySpan<byte> s)
    {
        if (s.Length < 16) return null;
        if (s[0] == 0xFF && s[1] == 0xD8 && s[2] == 0xFF && s[3] >= 0xC0 && s[3] != 0xFF) return Jpeg;
        if (s[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return Png;
        if (s[0] == 'G' && s[1] == 'I' && s[2] == 'F' && s[3] == '8' && (s[4] == '7' || s[4] == '9') && s[5] == 'a') return Gif;
        if (s[0] == 'B' && s[1] == 'M' && s[6] == 0 && s[7] == 0 && s[8] == 0 && s[9] == 0) return Bmp;
        if (s[4] == 'f' && s[5] == 't' && s[6] == 'y' && s[7] == 'p' && s[0] == 0 && s[1] == 0) return IsoBmff;
        if (s[0] == '%' && s[1] == 'P' && s[2] == 'D' && s[3] == 'F' && s[4] == '-') return Pdf;
        if (s[0] == 'P' && s[1] == 'K' && s[2] == 3 && s[3] == 4) return Zip;
        return null;
    }

    // ---------------- JPEG ----------------

    public static Measured? Jpeg(ByteSource b, long start, CarveOptions o)
    {
        long limit = Math.Min(b.Length, start + o.MaxPhotoBytes);
        long pos = start + 2;
        bool sawFrame = false, sawScan = false;
        while (pos + 4 <= limit)
        {
            if (b.Byte(pos) != 0xFF) return null;
            while (b.Byte(pos + 1) == 0xFF) pos++; // fill bytes
            int m = b.Byte(pos + 1);
            if (m < 0) return null;
            if (m == 0xD9) return sawFrame && sawScan ? new Measured(FileKind.Jpeg, pos + 2 - start) : null;
            if (m is >= 0xD0 and <= 0xD7 or 0x01) { pos += 2; continue; }
            if (m is 0x00 or 0xD8) return null;
            int len = b.U16BE(pos + 2);
            if (len < 2) return null;
            if (m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) sawFrame = true;
            pos += 2 + len;
            if (m != 0xDA) continue;

            // After a scan header comes compressed data, ended by the next marker that isn't FF00 or a restart marker.
            sawScan = true;
            long marker = EndOfScan(b, pos, limit);
            if (marker < 0) return null;
            pos = marker;
        }
        return null;
    }

    /// <summary>Streams through compressed JPEG data and returns where the next real marker starts, or -1.</summary>
    private static long EndOfScan(ByteSource b, long pos, long limit)
    {
        var buf = new byte[256 * 1024];
        while (pos < limit)
        {
            int n = b.Read(pos, buf.AsSpan(0, (int)Math.Min(buf.Length, limit - pos)));
            if (n <= 0) return -1;
            var span = buf.AsSpan(0, n);
            int i = 0;
            while (true)
            {
                int ff = span[i..].IndexOf((byte)0xFF);
                if (ff < 0) break;
                i += ff;
                int next = i + 1 < n ? span[i + 1] : b.Byte(pos + i + 1);
                if (next < 0) return -1;
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == 0xFF) { i += next == 0xFF ? 1 : 2; if (i >= n) break; continue; }
                return pos + i;
            }
            pos += n;
        }
        return -1;
    }

    // ---------------- PNG ----------------

    public static Measured? Png(ByteSource b, long start, CarveOptions o)
    {
        long limit = Math.Min(b.Length, start + o.MaxPhotoBytes);
        long pos = start + 8;
        bool first = true;
        Span<byte> type = stackalloc byte[4];
        while (pos + 12 <= limit)
        {
            uint len = b.U32BE(pos);
            if (len > int.MaxValue || !b.Bytes(pos + 4, type)) return null;
            foreach (var c in type) if (!(c is >= (byte)'A' and <= (byte)'Z' || c is >= (byte)'a' and <= (byte)'z')) return null;
            if (first && (!type.SequenceEqual("IHDR"u8) || len != 13)) return null;
            first = false;
            pos += 12 + len;
            if (type.SequenceEqual("IEND"u8)) return pos <= limit ? new Measured(FileKind.Png, pos - start) : null;
        }
        return null;
    }

    // ---------------- GIF ----------------

    public static Measured? Gif(ByteSource b, long start, CarveOptions o)
    {
        long limit = Math.Min(b.Length, start + o.MaxPhotoBytes);
        int flags = b.Byte(start + 10);
        if (flags < 0) return null;
        long pos = start + 13;
        if ((flags & 0x80) != 0) pos += 3 * (1 << ((flags & 7) + 1));
        bool sawImage = false;
        while (pos < limit)
        {
            int block = b.Byte(pos);
            if (block == 0x3B) return sawImage ? new Measured(FileKind.Gif, pos + 1 - start) : null;
            if (block == 0x2C)
            {
                int f = b.Byte(pos + 9);
                if (f < 0) return null;
                pos += 10;
                if ((f & 0x80) != 0) pos += 3 * (1 << ((f & 7) + 1));
                pos += 1; // LZW minimum code size
                sawImage = true;
            }
            else if (block == 0x21) pos += 2; // extension introducer and label
            else return null;
            // Data sub-blocks, ended by a zero-length block.
            while (true)
            {
                int size = b.Byte(pos);
                if (size < 0 || pos >= limit) return null;
                pos += 1 + size;
                if (size == 0) break;
            }
        }
        return null;
    }

    // ---------------- BMP ----------------

    public static Measured? Bmp(ByteSource b, long start, CarveOptions o)
    {
        long size = b.U32LE(start + 2);
        long dataOffset = b.U32LE(start + 10);
        long header = b.U32LE(start + 14);
        if (size < 54 || size > o.MaxPhotoBytes || start + size > b.Length) return null;
        if (header is not (12 or 40 or 52 or 56 or 64 or 108 or 124)) return null;
        if (dataOffset < 14 + header || dataOffset >= size) return null;
        long width = header == 12 ? b.U16LE(start + 18) : b.U32LE(start + 18);
        long planes = header == 12 ? b.U16LE(start + 22) : b.U16LE(start + 26);
        if (width is <= 0 or > 50000 || planes != 1) return null;
        return new Measured(FileKind.Bmp, size);
    }

    // ---------------- MP4 / MOV / HEIC / M4A / 3GP (ISO base media) ----------------

    private static readonly HashSet<string> TopLevelBoxes = new(StringComparer.Ordinal)
    {
        "ftyp", "moov", "mdat", "free", "skip", "wide", "meta", "uuid", "pnot", "moof", "mfra", "sidx", "styp", "pdin", "udta",
        "prft", "emsg", "ssix", "idat", "junk", "pict",
    };

    public static Measured? IsoBmff(ByteSource b, long start, CarveOptions o)
    {
        Span<byte> brandBytes = stackalloc byte[4];
        if (!b.Bytes(start + 8, brandBytes)) return null;
        var brand = Encoding.ASCII.GetString(brandBytes);
        var kind = brand switch
        {
            "heic" or "heix" or "mif1" or "msf1" or "heim" or "heis" or "hevc" => FileKind.Heic,
            "avif" or "avis" => FileKind.Avif,
            "qt  " => FileKind.Mov,
            "M4A " or "M4B " => FileKind.M4a,
            _ when brand.StartsWith("3g", StringComparison.Ordinal) => FileKind.ThreeGp,
            _ => FileKind.Mp4,
        };
        bool photo = kind is FileKind.Heic or FileKind.Avif;
        long limit = Math.Min(b.Length, start + (photo ? o.MaxPhotoBytes : o.MaxVideoBytes));
        long pos = start;
        bool sawMovie = false, sawMeta = false, sawData = false;
        Span<byte> type = stackalloc byte[4];
        while (pos + 8 <= limit)
        {
            ulong size = b.U32BE(pos);
            if (!b.Bytes(pos + 4, type)) break;
            var t = Encoding.ASCII.GetString(type);
            if (!TopLevelBoxes.Contains(t)) break;              // the file ended at the previous box
            if (pos == start && t != "ftyp") return null;
            if (size == 1) size = b.U64BE(pos + 8);
            else if (size == 0) break;                          // "runs to the end": length unknown
            if (size < 8 || size > (ulong)(limit - pos)) { if (t == "mdat" && sawMovie) break; return null; }
            if (t is "moov" or "moof") sawMovie = true;
            if (t == "meta") sawMeta = true;
            if (t is "mdat" or "idat") sawData = true;
            pos += (long)size;
        }
        long length = pos - start;
        bool valid = photo ? sawMeta : sawMovie && sawData;
        return valid && length > 16 ? new Measured(kind, length) : null;
    }

    // ---------------- PDF ----------------

    public static Measured? Pdf(ByteSource b, long start, CarveOptions o)
    {
        int v = b.Byte(start + 5);
        if (v is not ('1' or '2') || b.Byte(start + 6) != '.' || b.Byte(start + 7) is < '0' or > '9') return null;
        // Every PDF starts its first object within a few hundred bytes ("1 0 obj"); random data almost never does.
        if (b.Find(" obj"u8, start + 8, start + 4096) < 0) return null;
        long limit = Math.Min(b.Length, start + o.MaxDocumentBytes);
        ReadOnlySpan<byte> eof = "%%EOF"u8;
        long hit = b.Find(eof, start, limit);
        if (hit < 0) return null;
        long end = hit + eof.Length;
        // Edited PDFs add updates, each ending with its own %%EOF: keep going while another one follows closely.
        while (true)
        {
            long more = b.Find(eof, end, Math.Min(limit, end + o.PdfUpdateWindow));
            if (more < 0) break;
            long nextPdf = b.Find("%PDF-"u8, end, more);
            if (nextPdf >= 0) break; // that %%EOF belongs to a different PDF
            end = more + eof.Length;
        }
        if (b.Byte(end) == '\r') end++;
        if (b.Byte(end) == '\n') end++;
        return new Measured(FileKind.Pdf, end - start);
    }

    // ---------------- ZIP, Word, Excel, PowerPoint ----------------

    public static Measured? Zip(ByteSource b, long start, CarveOptions o)
    {
        // The first entry's header must make sense: a known compression method and a real name.
        int method = b.U16LE(start + 8), nameLen = b.U16LE(start + 26);
        if (b.Byte(start + 4) is < 10 or > 63 || method is not (0 or 8 or 9 or 12 or 14 or 93 or 95 or 98 or 99)) return null;
        if (nameLen is < 1 or > 1024) return null;
        for (int i = 0; i < Math.Min(nameLen, 64); i++)
            if (b.Byte(start + 30 + i) is < 0x20 or 0x7F) return null;
        long limit = Math.Min(b.Length, start + o.MaxDocumentBytes);
        ReadOnlySpan<byte> eocdSig = "PK\x05\x06"u8;
        long search = start + 30;
        while (true)
        {
            long eocd = b.Find(eocdSig, search, limit);
            if (eocd < 0) return null;
            long cdSize = b.U32LE(eocd + 12);
            long cdOffset = b.U32LE(eocd + 16);
            int comment = b.U16LE(eocd + 20);
            // The end record says where the file's directory is, relative to the start of the zip: it must point back here.
            if (cdSize >= 0 && cdOffset >= 0 && comment >= 0 && cdOffset + cdSize == eocd - start && b.U32LE(start + cdOffset) == 0x02014B50)
            {
                long end = eocd + 22 + comment;
                return end <= b.Length ? new Measured(OfficeKind(b, start + cdOffset, cdSize), end - start) : null;
            }
            search = eocd + 4;
        }
    }

    /// <summary>Looks at the names in the zip's directory: word/ means a Word file, xl/ Excel, ppt/ PowerPoint.</summary>
    private static FileKind OfficeKind(ByteSource b, long cd, long cdSize)
    {
        long pos = cd, end = cd + Math.Min(cdSize, 4 * 1024 * 1024);
        bool contentTypes = false;
        FileKind kind = FileKind.Zip;
        var name = new byte[512];
        while (pos + 46 <= end && b.U32LE(pos) == 0x02014B50)
        {
            int nameLen = b.U16LE(pos + 28), extra = b.U16LE(pos + 30), comment = b.U16LE(pos + 32);
            if (nameLen < 0 || extra < 0 || comment < 0) break;
            int n = Math.Min(nameLen, name.Length);
            if (!b.Bytes(pos + 46, name.AsSpan(0, n))) break;
            var s = Encoding.UTF8.GetString(name, 0, n);
            if (s == "[Content_Types].xml") contentTypes = true;
            else if (s.StartsWith("word/", StringComparison.Ordinal)) kind = FileKind.Docx;
            else if (s.StartsWith("xl/", StringComparison.Ordinal)) kind = FileKind.Xlsx;
            else if (s.StartsWith("ppt/", StringComparison.Ordinal)) kind = FileKind.Pptx;
            pos += 46 + nameLen + extra + comment;
        }
        return contentTypes ? kind : FileKind.Zip;
    }
}
