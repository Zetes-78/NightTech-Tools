using System.IO.Compression;
using System.Text;
using NightTech.Core.Carving;

namespace NightTech.Core.Tests;

public class CarverTests
{
    // ---------- Building blocks: real file structures, made by hand ----------

    private static byte[] Seg(byte marker, byte[] body) =>
        new byte[] { 0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)((body.Length + 2) & 0xFF) }.Concat(body).ToArray();

    /// <summary>A structurally valid baseline JPEG. Compressed data includes stuffed FF00 bytes and a restart marker.</summary>
    internal static byte[] Jpeg(int dataBytes = 3000, int seed = 1, byte[]? app1 = null)
    {
        var rnd = new Random(seed);
        var s = new List<byte> { 0xFF, 0xD8 };
        s.AddRange(Seg(0xE0, Encoding.ASCII.GetBytes("JFIF\0").Concat(new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 }).ToArray()));
        if (app1 is not null) s.AddRange(Seg(0xE1, app1));
        s.AddRange(Seg(0xDB, new byte[65]));
        s.AddRange(Seg(0xC0, new byte[] { 8, 0, 16, 0, 16, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 }));
        s.AddRange(Seg(0xC4, new byte[29]));
        s.AddRange(Seg(0xDA, new byte[] { 3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0 }));
        for (int i = 0; i < dataBytes; i++)
        {
            var v = (byte)rnd.Next(256);
            s.Add(v);
            if (v == 0xFF) s.Add(0x00);                     // byte stuffing
            if (i == dataBytes / 2) { s.Add(0xFF); s.Add(0xD3); } // a restart marker
        }
        s.Add(0xFF); s.Add(0xD9);
        return s.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data) =>
        new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }
            .Concat(Encoding.ASCII.GetBytes(type)).Concat(data).Concat(new byte[4]).ToArray();

    private static byte[] Png() =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Chunk("IHDR", new byte[13])).Concat(Chunk("IDAT", Enumerable.Range(0, 1500).Select(i => (byte)i).ToArray()))
            .Concat(Chunk("IEND", Array.Empty<byte>())).ToArray();

    private static byte[] Gif()
    {
        var s = new List<byte>(Encoding.ASCII.GetBytes("GIF89a")) { 1, 0, 1, 0, 0x80, 0, 0 };
        s.AddRange(new byte[6]);                                               // global color table (2 colors)
        s.AddRange(new byte[] { 0x21, 0xF9, 4, 0, 0, 0, 0, 0 });               // graphic control extension
        s.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0 });            // image descriptor
        s.Add(2);                                                             // LZW minimum code size
        for (int i = 0; i < 6; i++) { s.Add(200); s.AddRange(Enumerable.Repeat((byte)0x3B, 200)); } // data that contains 0x3B
        s.Add(0); s.Add(0x3B);
        return s.ToArray();
    }

    private static byte[] Bmp()
    {
        int pixels = 16 * 16 * 3, size = 54 + pixels;
        var b = new byte[size];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(b, 2);
        BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(16).CopyTo(b, 18);
        BitConverter.GetBytes(16).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)24).CopyTo(b, 28);
        for (int i = 54; i < size; i++) b[i] = (byte)(i * 7);
        return b;
    }

    private static byte[] Box(string type, byte[] body) =>
        BitConverter.GetBytes(body.Length + 8).Reverse().Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();

    private static byte[] Mp4(string brand = "isom", bool photo = false) =>
        Box("ftyp", Encoding.ASCII.GetBytes(brand).Concat(new byte[4]).Concat(Encoding.ASCII.GetBytes(brand)).ToArray())
            .Concat(photo ? Box("meta", new byte[200]) : Box("moov", new byte[300]))
            .Concat(Box("mdat", Enumerable.Range(0, 4000).Select(i => (byte)(i * 13)).ToArray())).ToArray();

    private static byte[] Pdf() => Encoding.ASCII.GetBytes(
        "%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n" + new string('x', 2000) + "\ntrailer << >>\n%%EOF\n" +
        "2 0 obj << /Edited true >> endobj\n" + new string('y', 500) + "\n%%EOF\n");

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(name, CompressionLevel.NoCompression).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    // ---------- A fake drive image with files at sector boundaries between random data ----------

    private sealed record Placed(long Offset, byte[] Data, FileKind Kind);

    private static (byte[] Image, List<Placed> Files) BuildImage(params (byte[] Data, FileKind Kind)[] files)
    {
        var rnd = new Random(42);
        var image = new List<byte>();
        var placed = new List<Placed>();
        void Noise(int sectors)
        {
            var buf = new byte[sectors * 512];
            rnd.NextBytes(buf);
            image.AddRange(buf);
        }
        Noise(3);
        foreach (var (data, kind) in files)
        {
            placed.Add(new Placed(image.Count, data, kind));
            image.AddRange(data);
            Noise(1);
            while (image.Count % 512 != 0) image.Add((byte)rnd.Next(256));
            Noise(2);
        }
        return (image.ToArray(), placed);
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "ntcarve-" + Guid.NewGuid().ToString("N"));

    private static CarveReport Carve(byte[] image, string dir, CarveOptions? o = null, CancellationToken ct = default) =>
        Carver.Run(new MemoryImageReader(image), dir, o ?? new CarveOptions { MinBytes = 0 }, null, ct);

    [Fact]
    public void Finds_every_kind_exactly_and_sorts_them_into_folders()
    {
        var docx = Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<w:document/>"));
        var xlsx = Zip(("[Content_Types].xml", "<Types/>"), ("xl/workbook.xml", "<workbook/>"));
        var plain = Zip(("notes.txt", "hello"), ("more.txt", new string('z', 3000)));
        var (image, files) = BuildImage(
            (Jpeg(), FileKind.Jpeg), (Png(), FileKind.Png), (Gif(), FileKind.Gif), (Bmp(), FileKind.Bmp),
            (Mp4(), FileKind.Mp4), (Mp4("qt  "), FileKind.Mov), (Mp4("heic", photo: true), FileKind.Heic),
            (Pdf(), FileKind.Pdf), (docx, FileKind.Docx), (xlsx, FileKind.Xlsx), (plain, FileKind.Zip));
        var dir = TempDir();
        try
        {
            var r = Carve(image, dir);
            Assert.Equal(CarveStatus.Completed, r.Status);
            Assert.Equal(files.Count, r.Files.Count);
            foreach (var f in files)
            {
                var got = Assert.Single(r.Files, x => x.Offset == f.Offset);
                Assert.Equal(f.Kind, got.Kind);
                Assert.Equal(f.Data.Length, got.Length);
                Assert.Equal(f.Data, File.ReadAllBytes(Path.Combine(dir, got.RelativePath)));
            }
            Assert.True(File.Exists(Path.Combine(dir, "Photos", $"jpg-{files[0].Offset / 512:D10}.jpg")));
            Assert.Contains(r.Files, x => x.RelativePath.StartsWith("Videos"));
            Assert.Contains(r.Files, x => x.RelativePath.StartsWith("Documents") && x.RelativePath.EndsWith(".docx"));
            Assert.Equal(1, r.Counts["PDF"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Pdf_with_an_edit_keeps_the_last_update()
    {
        var pdf = Pdf();
        var (image, files) = BuildImage((pdf, FileKind.Pdf));
        var dir = TempDir();
        try
        {
            var got = Assert.Single(Carve(image, dir).Files);
            Assert.Equal(pdf.Length, got.Length);
            Assert.EndsWith("%%EOF\n", Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dir, got.RelativePath))));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_thumbnail_inside_a_photo_isnt_found_twice()
    {
        // EXIF data commonly holds a small JPEG thumbnail; it must not come out as a second photo.
        var thumb = Jpeg(400, seed: 9);
        var exif = Encoding.ASCII.GetBytes("Exif\0\0").Concat(new byte[512 - 6]).Concat(thumb).ToArray();
        var photo = Jpeg(5000, seed: 3, app1: exif);
        var (image, _) = BuildImage((photo, FileKind.Jpeg));
        var dir = TempDir();
        try
        {
            var got = Assert.Single(Carve(image, dir).Files);
            Assert.Equal(photo.Length, got.Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Broken_leftovers_and_random_data_are_not_reported()
    {
        var cut = Jpeg(3000).Take(1500).ToArray();                         // the rest of the photo was overwritten
        var pngNoEnd = Png().Take(500).ToArray();
        var fakeZip = Encoding.ASCII.GetBytes("PK\x03\x04").Concat(new byte[2000]).ToArray();
        var fakeMp4 = Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0")).Concat(new byte[500]).ToArray(); // no movie data
        var (image, files) = BuildImage((cut, FileKind.Jpeg), (pngNoEnd, FileKind.Png), (fakeZip, FileKind.Zip), (fakeMp4, FileKind.Mp4), (Png(), FileKind.Png));
        var dir = TempDir();
        try
        {
            var r = Carve(image, dir);
            var got = Assert.Single(r.Files);                              // only the real PNG at the end
            Assert.Equal(files[^1].Offset, got.Offset);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Only_the_chosen_kinds_and_sizes_are_kept()
    {
        var (image, _) = BuildImage((Jpeg(), FileKind.Jpeg), (Pdf(), FileKind.Pdf), (Gif(), FileKind.Gif));
        var dir = TempDir();
        try
        {
            var r = Carve(image, dir, new CarveOptions { Kinds = new() { FileKind.Pdf, FileKind.Gif }, MinBytes = 0 });
            Assert.Equal(new[] { FileKind.Pdf, FileKind.Gif }, r.Files.Select(f => f.Kind).ToArray());
            r = Carve(image, TempDir(), new CarveOptions { MinBytes = 2000 });    // the 1.3 KB GIF counts as an icon
            Assert.DoesNotContain(r.Files, f => f.Kind == FileKind.Gif);
            Assert.Contains(r.Files, f => f.Kind == FileKind.Jpeg);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Stop_keeps_what_was_found_and_says_so()
    {
        var (image, _) = BuildImage((Jpeg(), FileKind.Jpeg));
        var dir = TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            var r = Carve(image, dir, ct: cts.Token);
            Assert.Equal(CarveStatus.Cancelled, r.Status);
            Assert.Contains("Stopped", r.ToText());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Remembered_searches_give_the_same_answers_as_fresh_ones()
    {
        var rnd = new Random(11);
        var data = new byte[3_000_000];
        rnd.NextBytes(data);
        foreach (var at in new[] { 10, 700_000, 700_003, 1_048_570, 1_048_574, 2_500_000 }) "%%EOF"u8.CopyTo(data.AsSpan(at));
        var cached = new ByteSource(new MemoryImageReader(data));
        long Naive(long from, long limit)
        {
            limit = Math.Min(limit, data.Length);
            for (long i = from; i < limit; i++)
                if (i + 5 <= data.Length && data.AsSpan((int)i, 5).SequenceEqual("%%EOF"u8)) return i;
            return -1;
        }
        long from = 0;
        for (int q = 0; q < 400; q++)
        {
            from = Math.Max(0, from + rnd.Next(-20_000, 40_000)); // mostly forward, sometimes back a little
            if (q % 50 == 0) from = rnd.Next(data.Length);        // and now and then anywhere
            long limit = from + rnd.Next(1, 1_500_000);
            Assert.Equal(Naive(from, limit), cached.Find("%%EOF"u8, from, limit));
        }
    }

    [Fact]
    public void Report_round_trips_and_states_the_limits()
    {
        var (image, _) = BuildImage((Jpeg(), FileKind.Jpeg), (Png(), FileKind.Png));
        var dir = TempDir();
        try
        {
            var r = Carve(image, dir);
            r.Ticket = "NT-0007";
            var (json, text) = Carver.SaveReport(r, dir);
            var back = CarveReport.FromJson(File.ReadAllText(json))!;
            Assert.Equal("Night Tech Recovery", back.Tool);
            Assert.Equal("file-recovery", back.Kind);
            Assert.Equal(2, back.Files.Count);
            Assert.Equal(FileKind.Jpeg, back.Files[0].Kind);
            Assert.Contains("\"kind\": \"Jpeg\"", File.ReadAllText(json));
            Assert.Contains("not guaranteed", File.ReadAllText(text));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
