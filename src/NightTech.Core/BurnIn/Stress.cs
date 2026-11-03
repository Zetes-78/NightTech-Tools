using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NightTech.Core.BurnIn;

/// <summary>
/// Processor stress that checks its own answers. Every worker repeats the same fixed calculation (hashing, whole-number
/// and decimal math) and compares the result with the answer worked out once before the test. A stable processor gets
/// the same answer every time; any difference means the processor, memory or power isn't stable under load (often heat).
/// </summary>
public sealed class CpuStress
{
    private readonly byte[] _input = new byte[64 * 1024];
    private readonly Work _expected;
    private long _iterations;
    private long _errors;
    private string _firstError = "";

    public long Iterations => Interlocked.Read(ref _iterations);
    public long Errors => Interlocked.Read(ref _errors);
    public string FirstError => _firstError;

    public CpuStress()
    {
        ulong x = 0x9E3779B97F4A7C15;
        for (int i = 0; i < _input.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; _input[i] = (byte)x; }
        _expected = Compute(_input);
    }

    private readonly record struct Work(ulong Hash, ulong Integer, double Real);

    private static Work Compute(byte[] input)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        for (int round = 0; round < 7; round++) SHA256.HashData(digest, digest);
        ulong hash = BitConverter.ToUInt64(digest);

        // Whole-number work: a small matrix multiply and a prime count.
        ulong acc = 0;
        Span<ulong> a = stackalloc ulong[64], b = stackalloc ulong[64];
        for (int i = 0; i < 64; i++) { a[i] = (ulong)(i * 2654435761u); b[i] = (ulong)(i * 40503u + 7); }
        for (int rep = 0; rep < 40; rep++)
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 8; j++)
                {
                    ulong s = 0;
                    for (int k = 0; k < 8; k++) s += a[i * 8 + k] * b[k * 8 + j];
                    acc = (acc ^ s) * 0x100000001B3;
                }
        int primes = 0;
        for (int n = 2; n < 6000; n++)
        {
            bool p = true;
            for (int d = 2; d * d <= n; d++) if (n % d == 0) { p = false; break; }
            if (p) primes++;
        }
        acc += (ulong)primes;

        // Decimal work: square roots, multiplies and divides (all exactly reproducible on the same processor).
        double r = 0;
        for (int i = 1; i < 20000; i++) r += Math.Sqrt(i) * 1.000001 / (i + 0.5);
        return new Work(hash, acc, r);
    }

    /// <summary>Runs <paramref name="threads"/> workers until <paramref name="duration"/> passes or it's cancelled.</summary>
    public void Run(int threads, TimeSpan duration, CancellationToken ct)
    {
        var until = Stopwatch.StartNew();
        var workers = new Thread[Math.Max(1, threads)];
        for (int t = 0; t < workers.Length; t++)
        {
            workers[t] = new Thread(() =>
            {
                var local = (byte[])_input.Clone();
                while (!ct.IsCancellationRequested && until.Elapsed < duration)
                {
                    var w = Compute(local);
                    if (w != _expected)
                    {
                        if (Interlocked.Increment(ref _errors) == 1)
                            _firstError = $"A calculation gave a wrong answer after {until.Elapsed:hh\\:mm\\:ss} under load.";
                    }
                    Interlocked.Increment(ref _iterations);
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "burn-in cpu " + t };
            workers[t].Start();
        }
        foreach (var w in workers) w.Join();
    }

    /// <summary>For tests: checks the reference answer is reproducible.</summary>
    public bool SelfCheck() => Compute(_input) == _expected;
}

public sealed class MemoryResult
{
    public long BytesTested { get; set; }
    public int PassesDone { get; set; }
    public long Errors { get; set; }
    public List<string> ErrorSamples { get; set; } = new();
    public string Note { get; set; } = "";
}

/// <summary>
/// Memory test: fills as much free memory as it safely can with patterns, waits, then checks every value. Patterns:
/// each cell's own address (catches address and wiring faults), its inverse, a checkerboard and its opposite, and
/// seeded random data. Memory Windows itself is using can't be tested from inside Windows; for that, the Windows Memory
/// Diagnostic (built in) tests at restart.
/// </summary>
public static class MemoryTest
{
    public const int BlockLongs = 4 * 1024 * 1024; // 32 MB blocks
    public const long BlockBytes = BlockLongs * 8L;

    /// <summary>How much to test: most of the free memory, always leaving room for Windows (at least 1.5 GB, or a quarter).</summary>
    public static long SafeBytes(long availableBytes)
    {
        long reserve = Math.Max(1536L * 1024 * 1024, availableBytes / 4);
        long bytes = availableBytes - reserve;
        return bytes < BlockBytes ? 0 : bytes / BlockBytes * BlockBytes;
    }

    public static MemoryResult Run(long bytes, int passes, CancellationToken ct, IProgress<(int Pass, double Fraction)>? progress = null)
    {
        var r = new MemoryResult();
        var blocks = new List<long[]>();
        try
        {
            for (long got = 0; got + BlockBytes <= bytes; got += BlockBytes)
            {
                ct.ThrowIfCancellationRequested();
                blocks.Add(GC.AllocateUninitializedArray<long>(BlockLongs));
            }
        }
        catch (OutOfMemoryException) { r.Note = "Windows ran short of memory, so a little less was tested."; }
        if (blocks.Count == 0) { r.Note = "There wasn't enough free memory to test. Close other programs and try again."; return r; }
        r.BytesTested = blocks.Count * BlockBytes;

        try
        {
            // Stopping (time up or the Stop button) ends the current pass early; only complete passes count.
            for (int pass = 0; pass < passes && !ct.IsCancellationRequested; pass++)
            {
                for (int pattern = 0; pattern < Patterns; pattern++)
                {
                    ulong seed = (ulong)(pass * 31 + pattern + 1) * 0x9E3779B97F4A7C15;
                    for (int b = 0; b < blocks.Count; b++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Fill(blocks[b], b, pattern, seed);
                        progress?.Report((pass + 1, ((pattern * 2.0) * blocks.Count + b) / (Patterns * 2.0 * blocks.Count)));
                    }
                    for (int b = 0; b < blocks.Count; b++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Verify(blocks[b], b, pattern, seed, r);
                        progress?.Report((pass + 1, ((pattern * 2.0 + 1) * blocks.Count + b) / (Patterns * 2.0 * blocks.Count)));
                    }
                }
                r.PassesDone = pass + 1;
            }
        }
        catch (OperationCanceledException) { }
        finally { blocks.Clear(); GC.Collect(); }
        return r;
    }

    public const int Patterns = 5;

    private static ulong Expected(int block, int i, int pattern, ulong seed, ref ulong rnd) => pattern switch
    {
        0 => ((ulong)block << 32) | (uint)i,
        1 => ~(((ulong)block << 32) | (uint)i),
        2 => (i & 1) == 0 ? 0xAAAAAAAAAAAAAAAA : 0x5555555555555555,
        3 => (i & 1) == 0 ? 0x5555555555555555 : 0xAAAAAAAAAAAAAAAA,
        _ => Next(ref rnd),
    };

    private static ulong Next(ref ulong x) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; return x; }

    private static void Fill(long[] a, int block, int pattern, ulong seed)
    {
        ulong rnd = seed ^ ((ulong)block * 0xBF58476D1CE4E5B9) | 1;
        for (int i = 0; i < a.Length; i++) a[i] = (long)Expected(block, i, pattern, seed, ref rnd);
    }

    private static void Verify(long[] a, int block, int pattern, ulong seed, MemoryResult r)
    {
        ulong rnd = seed ^ ((ulong)block * 0xBF58476D1CE4E5B9) | 1;
        for (int i = 0; i < a.Length; i++)
        {
            ulong want = Expected(block, i, pattern, seed, ref rnd);
            ulong got = (ulong)a[i];
            if (got != want)
            {
                r.Errors++;
                if (r.ErrorSamples.Count < 10)
                    r.ErrorSamples.Add($"block {block}, offset {(long)i * 8:N0}: expected {want:X16}, read {got:X16} (bits {want ^ got:X16} differ)");
            }
        }
    }

    /// <summary>For tests: corrupts one value so the checker can be shown to catch it.</summary>
    internal static void Corrupt(long[] a, int index) => a[index] ^= 1L << 17;

    internal static MemoryResult VerifyOnly(long[] a, int block, int pattern, ulong seed)
    {
        var r = new MemoryResult(); Verify(a, block, pattern, seed, r); return r;
    }

    internal static void FillOnly(long[] a, int block, int pattern, ulong seed) => Fill(a, block, pattern, seed);
}

public sealed class DriveResult
{
    public string Folder { get; set; } = "";
    public long BytesTested { get; set; }
    public double WriteMBps { get; set; }
    public double ReadMBps { get; set; }
    public long Errors { get; set; }
    public bool Uncached { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// Drive test: writes a temporary file of known data, reads it back and checks every byte, then deletes it. On Windows
/// it bypasses Windows' memory cache so the speeds and the check are of the drive itself. Only ever touches its own
/// temporary file, and always leaves plenty of free space.
/// </summary>
public static class DriveTest
{
    public const int Chunk = 8 * 1024 * 1024;
    public const string FileName = "NightTech-BurnIn-test.tmp";

    /// <summary>Test size: 2 GB by default, never more than a tenth of the free space, and never leaving less than 5 GB free.</summary>
    public static long SafeBytes(long freeBytes, long wanted = 2L * 1024 * 1024 * 1024)
    {
        long bytes = Math.Min(wanted, Math.Min(freeBytes / 10, freeBytes - 5L * 1024 * 1024 * 1024));
        return bytes < Chunk ? 0 : bytes / Chunk * Chunk;
    }

    public static unsafe DriveResult Run(string folder, long bytes, CancellationToken ct, IProgress<double>? progress = null)
    {
        var r = new DriveResult { Folder = folder };
        bytes = bytes / Chunk * Chunk;
        if (bytes <= 0) { r.Note = "Not enough free space to test this drive safely."; return r; }
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        bool direct = OperatingSystem.IsWindows();
        const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING
        var opts = FileOptions.WriteThrough | FileOptions.SequentialScan | (direct ? NoBuffering : 0);
        void* mem = NativeMemory.AlignedAlloc(Chunk, 4096);
        void* expect = NativeMemory.AlignedAlloc(Chunk, 4096);
        try
        {
            var buf = new Span<byte>(mem, Chunk);
            var want = new Span<byte>(expect, Chunk);
            long chunks = bytes / Chunk;
            var sw = Stopwatch.StartNew();
            using (var h = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, opts, bytes))
            {
                for (long c = 0; c < chunks; c++)
                {
                    ct.ThrowIfCancellationRequested();
                    Pattern(c, buf);
                    RandomAccess.Write(h, buf, c * Chunk);
                    progress?.Report((c + 1) / (double)chunks / 2);
                }
                if (!direct) RandomAccess.FlushToDisk(h);
            }
            r.WriteMBps = bytes / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            r.BytesTested = bytes;

            sw.Restart();
            using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, opts))
            {
                for (long c = 0; c < chunks; c++)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = RandomAccess.Read(h, buf, c * Chunk);
                    Pattern(c, want);
                    if (n != Chunk || !buf.SequenceEqual(want))
                    {
                        long bad = 0;
                        for (int i = 0; i < Chunk; i++) if (i >= n || buf[i] != want[i]) bad++;
                        r.Errors += bad;
                    }
                    progress?.Report(0.5 + (c + 1) / (double)chunks / 2);
                }
            }
            r.ReadMBps = bytes / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            r.Uncached = direct;
            if (!direct) r.Note = "Read speed may include the system's memory cache.";
        }
        finally
        {
            NativeMemory.AlignedFree(mem);
            NativeMemory.AlignedFree(expect);
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return r;
    }

    private static void Pattern(long chunk, Span<byte> dest)
    {
        var longs = MemoryMarshal.Cast<byte, ulong>(dest);
        ulong x = 0x2545F4914F6CDD1D ^ ((ulong)chunk * 0x9E3779B97F4A7C15) | 1;
        for (int i = 0; i < longs.Length; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; longs[i] = x; }
    }
}
