using System.Diagnostics;
using NightTech.Core.Imaging;

namespace NightTech.Core.Wipe;

/// <summary>A drive that can be overwritten. Only Night Tech Wipe provides a real one.</summary>
public interface IWipeTarget : IDisposable
{
    string Description { get; }
    string? SerialNumber { get; }
    /// <summary>Total size in bytes; a multiple of <see cref="SectorSize"/>.</summary>
    long Length { get; }
    int SectorSize { get; }

    /// <summary>Writes exactly count bytes at offset (both sector-aligned). IOException = bad sector; DeviceGoneException = drive gone.</summary>
    void WriteExactly(long offset, byte[] buffer, int count);

    /// <summary>Reads exactly count bytes at offset (both sector-aligned). Same exceptions as writing.</summary>
    void ReadExactly(long offset, byte[] buffer, int count);

    /// <summary>Makes sure everything written has reached the drive (not just its cache).</summary>
    void Flush();
}

public enum WipePattern { Zeros, Random }

public enum WipeStatus
{
    /// <summary>Every sector was overwritten and, when verifying, read back correctly.</summary>
    Completed,
    /// <summary>Finished, but some sectors couldn't be written or didn't read back correctly.</summary>
    CompletedWithErrors,
    /// <summary>Stopped by the technician. The drive is only partly erased.</summary>
    Cancelled,
    /// <summary>The drive stopped responding or another error stopped the wipe. The drive is only partly erased.</summary>
    Failed,
}

public sealed class WipeOptions
{
    public WipePattern Pattern { get; init; } = WipePattern.Zeros;
    /// <summary>Read every sector back after writing and check it holds the pattern.</summary>
    public bool Verify { get; init; } = true;
    public int BlockSize { get; init; } = 1024 * 1024;
    /// <summary>Seed for the random pattern, so the check pass can regenerate exactly what was written.</summary>
    public ulong Seed { get; init; } = (ulong)Random.Shared.NextInt64();
    /// <summary>Stop as failed after this many bad sectors in a row: the drive is failing and should be destroyed instead.</summary>
    public int MaxBadInARow { get; init; } = 64;
}

public sealed record WipeProgress(string Phase, long BytesDone, long BytesTotal, double BytesPerSecond, long ProblemSectors);

public sealed record WipeProblem(long Offset, long Length, string Kind);

public sealed class WipeResult
{
    public WipeStatus Status { get; set; }
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public long BytesWritten { get; set; }
    public long BytesVerified { get; set; }
    public bool Verified { get; set; }
    public long WriteErrorSectors { get; set; }
    public long VerifyMismatchSectors { get; set; }
    public long VerifyReadErrorSectors { get; set; }
    /// <summary>First problems found (at most 100), for the report.</summary>
    public List<WipeProblem> Problems { get; } = new();
    public string Message { get; set; } = "";

    /// <summary>True only when the whole drive was overwritten and every sector was read back and checked.</summary>
    public bool FullyVerified => Status == WipeStatus.Completed && Verified;

    internal void AddProblem(long offset, long length, string kind)
    {
        if (Problems.Count < 100) Problems.Add(new WipeProblem(offset, length, kind));
    }
}

/// <summary>
/// Overwrites a whole drive with one pass of a pattern, then reads every sector back to check it.
/// Bad sectors are retried one at a time and listed; they never stop the wipe.
/// </summary>
/// <summary>Too many bad sectors in a row: the drive is failing.</summary>
public sealed class TooManyBadSectorsException : IOException
{
    public TooManyBadSectorsException(long offset)
        : base($"Too many bad sectors in a row (near byte {offset:N0}). The drive is failing.") { }
}

public static class WipeEngine
{
    public static WipeResult Run(IWipeTarget target, WipeOptions options, IProgress<WipeProgress>? progress,
        CancellationToken cancel, Func<DateTime>? clock = null)
    {
        clock ??= () => DateTime.Now;
        if (target.SectorSize <= 0 || target.Length <= 0 || target.Length % target.SectorSize != 0)
            throw new ArgumentException("The drive reported an invalid size.");
        int block = Math.Max(target.SectorSize, options.BlockSize / target.SectorSize * target.SectorSize);

        var result = new WipeResult { Started = clock() };
        var buffer = new byte[block];
        var readBack = new byte[block];
        int badInARow = 0;
        try
        {
            // Pass 1: overwrite.
            result.BytesWritten = Pass(target, "Erasing", block, progress, cancel, result, (offset, count) =>
            {
                Fill(buffer, count, offset, options);
                try
                {
                    target.WriteExactly(offset, buffer, count);
                    badInARow = 0;
                }
                catch (IOException ex) when (ex is not DeviceGoneException)
                {
                    // Retry one sector at a time so a single bad sector doesn't leave the rest of the block unwritten.
                    for (long s = offset; s < offset + count; s += target.SectorSize)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var one = new byte[target.SectorSize];
                        Fill(one, target.SectorSize, s, options);
                        try { target.WriteExactly(s, one, target.SectorSize); badInARow = 0; }
                        catch (IOException e2) when (e2 is not DeviceGoneException)
                        {
                            result.WriteErrorSectors++;
                            result.AddProblem(s, target.SectorSize, "write failed");
                            if (++badInARow >= options.MaxBadInARow) throw new TooManyBadSectorsException(s);
                        }
                    }
                }
            });
            target.Flush();

            // Pass 2: read everything back and check it.
            if (options.Verify)
            {
                result.BytesVerified = Pass(target, "Checking", block, progress, cancel, result, (offset, count) =>
                {
                    Fill(buffer, count, offset, options);
                    try
                    {
                        target.ReadExactly(offset, readBack, count);
                        CompareSectors(buffer, readBack, count, offset, target.SectorSize, result);
                        badInARow = 0;
                    }
                    catch (IOException ex) when (ex is not DeviceGoneException)
                    {
                        var one = new byte[target.SectorSize];
                        var want = new byte[target.SectorSize];
                        for (long s = offset; s < offset + count; s += target.SectorSize)
                        {
                            cancel.ThrowIfCancellationRequested();
                            Fill(want, target.SectorSize, s, options);
                            try
                            {
                                target.ReadExactly(s, one, target.SectorSize);
                                CompareSectors(want, one, target.SectorSize, s, target.SectorSize, result);
                                badInARow = 0;
                            }
                            catch (IOException e2) when (e2 is not DeviceGoneException)
                            {
                                result.VerifyReadErrorSectors++;
                                result.AddProblem(s, target.SectorSize, "unreadable when checked");
                                if (++badInARow >= options.MaxBadInARow) throw new TooManyBadSectorsException(s);
                            }
                        }
                    }
                });
                result.Verified = true;
            }

            var problems = result.WriteErrorSectors + result.VerifyMismatchSectors + result.VerifyReadErrorSectors;
            result.Status = problems == 0 ? WipeStatus.Completed : WipeStatus.CompletedWithErrors;
            result.Message = problems == 0
                ? (options.Verify ? "Every sector was overwritten and checked." : "Every sector was overwritten (not checked).")
                : $"{problems:N0} sector(s) couldn't be erased or checked. Treat this drive as not fully erased: destroy it physically.";
        }
        catch (OperationCanceledException)
        {
            result.Status = WipeStatus.Cancelled;
            result.Message = "Stopped before finishing. The drive is only partly erased.";
        }
        catch (DeviceGoneException ex)
        {
            result.Status = WipeStatus.Failed;
            result.Message = "The drive stopped responding: " + ex.Message + " The drive is only partly erased.";
        }
        catch (TooManyBadSectorsException ex)
        {
            result.Status = WipeStatus.Failed;
            result.Message = ex.Message + " The drive is only partly erased: destroy it physically.";
        }
        catch (Exception ex)
        {
            // Once writing has started, any failure must still produce a record that says the drive is partly erased.
            result.Status = WipeStatus.Failed;
            result.Message = "The wipe stopped: " + ex.Message + " The drive is only partly erased.";
        }
        result.Finished = clock();
        return result;
    }

    private static long Pass(IWipeTarget target, string phase, int block, IProgress<WipeProgress>? progress,
        CancellationToken cancel, WipeResult result, Action<long, int> step)
    {
        var sw = Stopwatch.StartNew();
        long lastReport = -1;
        long done = 0;
        for (long offset = 0; offset < target.Length; offset += block)
        {
            cancel.ThrowIfCancellationRequested();
            int count = (int)Math.Min(block, target.Length - offset);
            step(offset, count);
            done = offset + count;
            var ms = sw.ElapsedMilliseconds;
            if (progress is not null && (ms - lastReport >= 250 || done == target.Length))
            {
                lastReport = ms;
                var bps = ms > 0 ? done * 1000.0 / ms : 0;
                progress.Report(new WipeProgress(phase, done, target.Length, bps,
                    result.WriteErrorSectors + result.VerifyMismatchSectors + result.VerifyReadErrorSectors));
            }
        }
        return done;
    }

    private static void CompareSectors(byte[] expected, byte[] actual, int count, long offset, int sector, WipeResult result)
    {
        var e = expected.AsSpan(0, count);
        var a = actual.AsSpan(0, count);
        if (e.SequenceEqual(a)) return;
        for (int i = 0; i < count; i += sector)
        {
            if (!e.Slice(i, sector).SequenceEqual(a.Slice(i, sector)))
            {
                result.VerifyMismatchSectors++;
                result.AddProblem(offset + i, sector, "didn't hold the new data");
            }
        }
    }

    /// <summary>
    /// The pattern for the bytes at a drive offset. Random data comes from SplitMix64 keyed by the seed and the
    /// position on the drive, so the check pass can regenerate exactly what was written without storing it.
    /// </summary>
    public static void Fill(byte[] buffer, int count, long driveOffset, WipeOptions options)
    {
        if (options.Pattern == WipePattern.Zeros)
        {
            Array.Clear(buffer, 0, count);
            return;
        }
        // Offsets and counts are sector-aligned, so always multiples of 8.
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(0, count));
        ulong index = (ulong)(driveOffset / 8);
        for (int i = 0; i < words.Length; i++)
            words[i] = SplitMix64(options.Seed ^ (index + (ulong)i) * 0x9E3779B97F4A7C15UL);
    }

    private static ulong SplitMix64(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
