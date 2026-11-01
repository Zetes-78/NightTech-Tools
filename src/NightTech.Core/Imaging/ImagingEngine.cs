using System.Diagnostics;
using System.Security.Cryptography;
using NightTech.Core.Logging;

namespace NightTech.Core.Imaging;

public sealed class ImagingOptions
{
    /// <summary>Read size for the fast first pass. Must be a multiple of the sector size.</summary>
    public int BlockSize { get; init; } = 1024 * 1024;

    /// <summary>After an error, skip ahead up to this many blocks (doubling each time) to get past damaged areas quickly.</summary>
    public int MaxSkipBlocks { get; init; } = 64;

    /// <summary>How many times to retry each unreadable sector at the end.</summary>
    public int RetryPasses { get; init; } = 1;

    /// <summary>How often to flush the image and save the map while working.</summary>
    public TimeSpan SaveInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Compute the SHA-256 of the finished image.</summary>
    public bool HashImage { get; init; } = true;
}

public enum ImagingPhase { Starting, FastCopy, Scraping, Retrying, Hashing, Done }

public enum ImagingStatus
{
    /// <summary>Every sector was copied.</summary>
    Completed,
    /// <summary>Finished, but some sectors could not be read (they are zero-filled in the image).</summary>
    CompletedWithBadSectors,
    /// <summary>Stopped by the user. Resume later with the same image and map.</summary>
    Cancelled,
    /// <summary>The drive disappeared or stopped responding. Resume after reconnecting it.</summary>
    DeviceLost,
}

public sealed record ImagingProgress(
    ImagingPhase Phase,
    long BytesGood,
    long BytesBad,
    long BytesPending,
    long SourceLength,
    long CurrentOffset,
    double BytesPerSecond,
    int BadSectorCount)
{
    public double PercentDone => SourceLength == 0 ? 0 : 100.0 * (SourceLength - BytesPending) / SourceLength;
}

public sealed record ImagingResult(
    ImagingStatus Status,
    RescueMap Map,
    string ImagePath,
    string MapPath,
    DateTime StartedLocal,
    DateTime FinishedLocal,
    string? ImageSha256,
    string? Message);

/// <summary>
/// Copies a whole drive to an image file, reading only. Works in passes, like professional tools:
/// 1) fast copy in big blocks, skipping past damaged areas; 2) sector-by-sector "scraping" of the
/// areas that failed; 3) retries of the sectors that still failed. Unreadable sectors stay zero in
/// the image and are listed in the map and report. Progress is saved so the job can resume.
/// </summary>
public sealed class ImagingEngine
{
    private readonly AppLog? _log;

    public ImagingEngine(AppLog? log = null) => _log = log;

    public ImagingResult Run(
        ISourceDevice source,
        string imagePath,
        string mapPath,
        ImagingOptions? options = null,
        IProgress<ImagingProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new ImagingOptions();
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length <= 0 || source.SectorSize <= 0 || source.Length % source.SectorSize != 0)
            throw new ArgumentException("The source reports an invalid size or sector size.");
        if (options.BlockSize % source.SectorSize != 0)
            throw new ArgumentException("Block size must be a multiple of the drive's sector size.");
        if (string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(mapPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The image and the map must be different files.");

        var started = DateTime.Now;
        RescueMap map;
        bool resuming = File.Exists(mapPath);
        if (resuming)
        {
            map = RescueMap.Load(mapPath);
            if (map.SourceLength != source.Length || map.SectorSize != source.SectorSize)
                throw new InvalidOperationException(
                    "This map belongs to a different drive (size or sector size doesn't match). Pick the right drive or start a new case.");
            if (!string.IsNullOrEmpty(map.SourceSerial) && !string.IsNullOrEmpty(source.SerialNumber)
                && !string.Equals(map.SourceSerial.Trim(), source.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"This map belongs to drive serial {map.SourceSerial}, but the selected drive is {source.SerialNumber}.");
            if (!File.Exists(imagePath) && map.BytesGood + map.BytesBad > 0)
                throw new InvalidOperationException("The map says work was done, but the image file is missing.");
            _log?.Info($"Resuming: {map.BytesGood:N0} bytes already copied, {map.BadSectors.Count} bad sectors so far.");
        }
        else
        {
            map = new RescueMap(source.Length, source.SectorSize, options.BlockSize)
            {
                SourceDescription = source.Description,
                SourceSerial = source.SerialNumber,
            };
            _log?.Info($"New image of {source.Description} ({source.Length:N0} bytes, {source.SectorSize}-byte sectors).");
        }

        using var image = new FileStream(imagePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 0, FileOptions.RandomAccess);
        if (image.Length != source.Length)
        {
            if (resuming && image.Length != 0)
                throw new InvalidOperationException("The image file size doesn't match the drive. Start a new case.");
            image.SetLength(source.Length);
        }

        var run = new RunState(source, image, map, mapPath, options, progress, ct, _log);
        ImagingStatus status;
        string? message = null;
        try
        {
            run.Report(ImagingPhase.Starting, 0, force: true);
            run.FastPass();
            run.ScrapePass();
            run.RetryPass();
            status = map.BadSectors.Count == 0 ? ImagingStatus.Completed : ImagingStatus.CompletedWithBadSectors;
        }
        catch (OperationCanceledException)
        {
            status = ImagingStatus.Cancelled;
            message = "Stopped. Your progress is saved; start the same case again to resume.";
            _log?.Warn("Imaging cancelled by user.");
        }
        catch (DeviceGoneException ex)
        {
            status = ImagingStatus.DeviceLost;
            message = "The drive stopped responding or was unplugged. Progress is saved. Reconnect it and resume. " +
                      "If it keeps disappearing, stop and send it to a recovery lab.";
            _log?.Error("Device lost.", ex);
        }
        finally
        {
            run.Checkpoint();
        }

        string? sha = null;
        if ((status == ImagingStatus.Completed || status == ImagingStatus.CompletedWithBadSectors) && options.HashImage)
        {
            sha = run.HashImage();
            map.ImageSha256 = sha;
            map.Save(mapPath);
            _log?.Info($"Image SHA-256: {sha}");
        }
        run.Report(ImagingPhase.Done, 0, force: true);
        _log?.Info($"Imaging finished: {status}. Good {map.BytesGood:N0}, bad {map.BytesBad:N0}, pending {map.BytesPending:N0}.");
        return new ImagingResult(status, map, imagePath, mapPath, started, DateTime.Now, sha, message);
    }

    /// <summary>Per-run working state, so the engine itself stays stateless and reusable.</summary>
    private sealed class RunState
    {
        private readonly ISourceDevice _src;
        private readonly FileStream _image;
        private readonly RescueMap _map;
        private readonly string _mapPath;
        private readonly ImagingOptions _opt;
        private readonly IProgress<ImagingProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly AppLog? _log;
        private readonly byte[] _blockBuf;
        private readonly byte[] _sectorBuf;
        private readonly Stopwatch _sinceSave = Stopwatch.StartNew();
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private readonly Stopwatch _rateClock = Stopwatch.StartNew();
        private long _rateBaseDone;
        private double _rate;

        public RunState(ISourceDevice src, FileStream image, RescueMap map, string mapPath, ImagingOptions opt,
            IProgress<ImagingProgress>? progress, CancellationToken ct, AppLog? log)
        {
            _src = src; _image = image; _map = map; _mapPath = mapPath; _opt = opt;
            _progress = progress; _ct = ct; _log = log;
            _blockBuf = new byte[map.BlockSize];
            _sectorBuf = new byte[map.SectorSize];
            _rateBaseDone = map.SourceLength - map.BytesPending;
        }

        public void FastPass()
        {
            int skipRemaining = 0, nextSkip = 0;
            for (int b = 0; b < _map.BlockCount; b++)
            {
                if (_map.GetState(b) != BlockState.Untried) continue;
                _ct.ThrowIfCancellationRequested();
                if (skipRemaining > 0)
                {
                    _map.MarkBlockSkipped(b);
                    skipRemaining--;
                    continue;
                }

                long off = _map.BlockOffset(b);
                int len = _map.BlockLength(b);
                if (TryRead(off, _blockBuf, len))
                {
                    Write(off, _blockBuf, len);
                    _map.MarkBlockGood(b);
                    nextSkip = 0;
                }
                else
                {
                    _map.MarkBlockSkipped(b);
                    nextSkip = Math.Min(nextSkip == 0 ? 1 : nextSkip * 2, Math.Max(0, _opt.MaxSkipBlocks));
                    skipRemaining = nextSkip;
                    _log?.Warn($"Read error near byte {off:N0}; skipping ahead {skipRemaining} block(s) for now.");
                }
                Tick(ImagingPhase.FastCopy, off);
            }
        }

        public void ScrapePass()
        {
            int ss = _map.SectorSize;
            for (int b = 0; b < _map.BlockCount; b++)
            {
                if (_map.GetState(b) != BlockState.Skipped) continue;
                _ct.ThrowIfCancellationRequested();
                long off = _map.BlockOffset(b);
                int len = _map.BlockLength(b);

                // Areas skipped only to get past damage are often fine: try the whole block first.
                if (TryRead(off, _blockBuf, len))
                {
                    Write(off, _blockBuf, len);
                    _map.MarkBlockGood(b);
                    Tick(ImagingPhase.Scraping, off);
                    continue;
                }

                for (long pos = off; pos < off + len; pos += ss)
                {
                    _ct.ThrowIfCancellationRequested();
                    long lba = pos / ss;
                    if (TryRead(pos, _sectorBuf, ss))
                    {
                        Write(pos, _sectorBuf, ss);
                        _map.MarkSectorGood(lba);
                    }
                    else
                    {
                        _map.MarkSectorBad(lba);
                    }
                    Tick(ImagingPhase.Scraping, pos);
                }
                _map.FinishSkippedBlock(b);
                if (_map.GetState(b) == BlockState.Bad)
                    _log?.Warn($"{_map.BadSectorsInBlock(b)} unreadable sector(s) in block at byte {off:N0}.");
            }
        }

        public void RetryPass()
        {
            int ss = _map.SectorSize;
            while (_map.RetryPassesDone < _opt.RetryPasses && _map.BadSectors.Count > 0)
            {
                foreach (long lba in _map.BadSectors.ToList())
                {
                    _ct.ThrowIfCancellationRequested();
                    long pos = lba * ss;
                    if (TryRead(pos, _sectorBuf, ss))
                    {
                        Write(pos, _sectorBuf, ss);
                        _map.RecoverBadSector(lba);
                        _log?.Info($"Sector {lba} read on retry.");
                    }
                    Tick(ImagingPhase.Retrying, pos);
                }
                _map.RetryPassesDone++;
                Checkpoint();
            }
        }

        public string HashImage()
        {
            _image.Flush(true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[4 * 1024 * 1024];
            long total = _image.Length, done = 0;
            _image.Position = 0;
            int n;
            while ((n = _image.Read(buf, 0, buf.Length)) > 0)
            {
                sha.AppendData(buf, 0, n);
                done += n;
                if (_sinceReport.ElapsedMilliseconds >= 250)
                {
                    _sinceReport.Restart();
                    _progress?.Report(new ImagingProgress(ImagingPhase.Hashing, _map.BytesGood, _map.BytesBad,
                        _map.BytesPending, total, done, 0, _map.BadSectors.Count));
                }
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        public void Checkpoint()
        {
            try
            {
                _image.Flush(true); // data on disk before the map claims it
                _map.Save(_mapPath);
                _sinceSave.Restart();
            }
            catch (Exception ex)
            {
                _log?.Error("Could not save progress.", ex);
                throw;
            }
        }

        public void Report(ImagingPhase phase, long offset, bool force = false)
        {
            if (!force && _sinceReport.ElapsedMilliseconds < 200) return;
            _sinceReport.Restart();
            long done = _map.SourceLength - _map.BytesPending;
            double secs = _rateClock.Elapsed.TotalSeconds;
            if (secs >= 2)
            {
                _rate = (done - _rateBaseDone) / secs;
                _rateBaseDone = done;
                _rateClock.Restart();
            }
            _progress?.Report(new ImagingProgress(phase, _map.BytesGood, _map.BytesBad, _map.BytesPending,
                _map.SourceLength, offset, _rate, _map.BadSectors.Count));
        }

        private void Tick(ImagingPhase phase, long offset)
        {
            Report(phase, offset);
            if (_sinceSave.Elapsed >= _opt.SaveInterval) Checkpoint();
        }

        private bool TryRead(long offset, byte[] buffer, int count)
        {
            try
            {
                _src.ReadExactly(offset, buffer, count);
                return true;
            }
            catch (DeviceGoneException) { throw; }
            catch (IOException) { return false; }
        }

        private void Write(long offset, byte[] buffer, int count)
        {
            _image.Position = offset;
            _image.Write(buffer, 0, count);
        }
    }
}
