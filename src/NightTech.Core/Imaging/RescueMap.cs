using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTech.Core.Imaging;

public enum BlockState : byte
{
    /// <summary>Not read yet.</summary>
    Untried = 0,
    /// <summary>Every sector in the block was copied.</summary>
    Good = 1,
    /// <summary>A read error happened here (or near here) during the fast pass; needs a sector-by-sector pass.</summary>
    Skipped = 2,
    /// <summary>Sector-by-sector pass done; one or more sectors in this block are unreadable.</summary>
    Bad = 3,
}

/// <summary>
/// Tracks which parts of the source drive have been copied, so imaging can be paused,
/// survive a crash or unplugged drive, and resume without re-reading good areas.
/// State is kept per block; unreadable sectors are kept individually.
/// </summary>
public sealed class RescueMap
{
    public const int FormatVersion = 1;

    private readonly BlockState[] _states;
    private readonly SortedSet<long> _badSectors = new();

    public long SourceLength { get; }
    public int SectorSize { get; }
    public int BlockSize { get; }
    public int BlockCount => _states.Length;
    public string SourceDescription { get; set; } = "";
    public string? SourceSerial { get; set; }
    public string? ImageSha256 { get; set; }
    public int RetryPassesDone { get; set; }

    public long BytesGood { get; private set; }
    public long BytesBad { get; private set; }
    public long BytesPending { get; private set; }

    public IReadOnlyCollection<long> BadSectors => _badSectors;

    public RescueMap(long sourceLength, int sectorSize, int blockSize)
    {
        if (sectorSize <= 0) throw new ArgumentOutOfRangeException(nameof(sectorSize));
        if (blockSize <= 0 || blockSize % sectorSize != 0)
            throw new ArgumentException("Block size must be a positive multiple of the sector size.", nameof(blockSize));
        if (sourceLength <= 0 || sourceLength % sectorSize != 0)
            throw new ArgumentException("Source length must be a positive multiple of the sector size.", nameof(sourceLength));

        SourceLength = sourceLength;
        SectorSize = sectorSize;
        BlockSize = blockSize;
        long count = (sourceLength + blockSize - 1) / blockSize;
        if (count > int.MaxValue) throw new ArgumentException("Drive too large for this block size.");
        _states = new BlockState[count];
        BytesPending = sourceLength;
    }

    public BlockState GetState(int block) => _states[block];

    public long BlockOffset(int block) => (long)block * BlockSize;

    public int BlockLength(int block) => (int)Math.Min(BlockSize, SourceLength - BlockOffset(block));

    public int BlockOfSector(long lba) => (int)(lba * SectorSize / BlockSize);

    public bool IsComplete => BytesPending == 0;

    public int BadSectorsInBlock(int block)
    {
        long first = BlockOffset(block) / SectorSize;
        long last = first + BlockLength(block) / SectorSize - 1;
        return _badSectors.GetViewBetween(first, last).Count;
    }

    /// <summary>Whole block copied (from Untried or Skipped).</summary>
    public void MarkBlockGood(int block)
    {
        var s = _states[block];
        if (s != BlockState.Untried && s != BlockState.Skipped)
            throw new InvalidOperationException($"Block {block} is {s}, not pending.");
        int len = BlockLength(block);
        BytesPending -= len;
        BytesGood += len;
        _states[block] = BlockState.Good;
    }

    /// <summary>Block needs the slow sector-by-sector pass.</summary>
    public void MarkBlockSkipped(int block)
    {
        if (_states[block] != BlockState.Untried)
            throw new InvalidOperationException($"Block {block} is {_states[block]}, not untried.");
        _states[block] = BlockState.Skipped;
    }

    /// <summary>During the sector pass of a skipped block: one sector copied.</summary>
    public void MarkSectorGood(long lba)
    {
        EnsureSkipped(lba);
        BytesPending -= SectorSize;
        BytesGood += SectorSize;
    }

    /// <summary>During the sector pass of a skipped block: one sector unreadable.</summary>
    public void MarkSectorBad(long lba)
    {
        EnsureSkipped(lba);
        if (_badSectors.Add(lba))
        {
            BytesPending -= SectorSize;
            BytesBad += SectorSize;
        }
    }

    /// <summary>Sector pass for a skipped block is finished.</summary>
    public void FinishSkippedBlock(int block)
    {
        if (_states[block] != BlockState.Skipped)
            throw new InvalidOperationException($"Block {block} is {_states[block]}, not skipped.");
        _states[block] = BadSectorsInBlock(block) > 0 ? BlockState.Bad : BlockState.Good;
    }

    /// <summary>A previously unreadable sector was read on a retry.</summary>
    public void RecoverBadSector(long lba)
    {
        if (!_badSectors.Remove(lba))
            throw new InvalidOperationException($"Sector {lba} is not marked bad.");
        BytesBad -= SectorSize;
        BytesGood += SectorSize;
        int block = BlockOfSector(lba);
        if (_states[block] == BlockState.Bad && BadSectorsInBlock(block) == 0)
            _states[block] = BlockState.Good;
    }

    private void EnsureSkipped(long lba)
    {
        if (lba < 0 || lba * SectorSize >= SourceLength) throw new ArgumentOutOfRangeException(nameof(lba));
        int block = BlockOfSector(lba);
        if (_states[block] != BlockState.Skipped)
            throw new InvalidOperationException($"Sector {lba} is in block {block}, which is {_states[block]}.");
    }

    // ---------------- Save / load ----------------

    private sealed class MapFile
    {
        public string Tool { get; set; } = "Night Tech Recovery";
        public int Version { get; set; } = FormatVersion;
        public string SourceDescription { get; set; } = "";
        public string? SourceSerial { get; set; }
        public long SourceLength { get; set; }
        public int SectorSize { get; set; }
        public int BlockSize { get; set; }
        /// <summary>Runs of [firstBlock, count, state].</summary>
        public List<long[]> Runs { get; set; } = new();
        public List<long> BadSectors { get; set; } = new();
        public int RetryPassesDone { get; set; }
        public string? ImageSha256 { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public void Save(string path)
    {
        var f = new MapFile
        {
            SourceDescription = SourceDescription,
            SourceSerial = SourceSerial,
            SourceLength = SourceLength,
            SectorSize = SectorSize,
            BlockSize = BlockSize,
            BadSectors = _badSectors.ToList(),
            RetryPassesDone = RetryPassesDone,
            ImageSha256 = ImageSha256,
            SavedUtc = DateTime.UtcNow,
        };
        int i = 0;
        while (i < _states.Length)
        {
            int j = i + 1;
            while (j < _states.Length && _states[j] == _states[i]) j++;
            f.Runs.Add(new long[] { i, j - i, (long)_states[i] });
            i = j;
        }

        // Write to a temp file then swap, so a crash never leaves a half-written map.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    public static RescueMap Load(string path)
    {
        var f = JsonSerializer.Deserialize<MapFile>(File.ReadAllText(path), JsonOpts)
                ?? throw new InvalidDataException("The map file is empty.");
        if (f.Version != FormatVersion)
            throw new InvalidDataException($"Unsupported map file version {f.Version}.");

        var map = new RescueMap(f.SourceLength, f.SectorSize, f.BlockSize)
        {
            SourceDescription = f.SourceDescription,
            SourceSerial = f.SourceSerial,
            RetryPassesDone = f.RetryPassesDone,
            ImageSha256 = f.ImageSha256,
        };

        long covered = 0;
        foreach (var run in f.Runs)
        {
            if (run.Length != 3) throw new InvalidDataException("Bad run in map file.");
            long start = run[0], count = run[1];
            var state = (BlockState)run[2];
            if (start < 0 || count <= 0 || start + count > map._states.Length || !Enum.IsDefined(state))
                throw new InvalidDataException("Bad run in map file.");
            for (long b = start; b < start + count; b++) map._states[b] = state;
            covered += count;
        }
        if (covered != map._states.Length) throw new InvalidDataException("Map file doesn't cover the whole drive.");

        // Rebuild counters. Bad sectors inside still-skipped blocks are dropped:
        // that block's sector pass will redo them.
        map.BytesGood = map.BytesBad = map.BytesPending = 0;
        foreach (var lba in f.BadSectors)
        {
            if (lba < 0 || lba * map.SectorSize >= map.SourceLength) throw new InvalidDataException("Bad sector out of range.");
            var st = map._states[map.BlockOfSector(lba)];
            if (st == BlockState.Bad) map._badSectors.Add(lba);
        }
        for (int b = 0; b < map._states.Length; b++)
        {
            int len = map.BlockLength(b);
            switch (map._states[b])
            {
                case BlockState.Good: map.BytesGood += len; break;
                case BlockState.Untried:
                case BlockState.Skipped: map.BytesPending += len; break;
                case BlockState.Bad:
                    int bad = map.BadSectorsInBlock(b);
                    if (bad == 0) { map._states[b] = BlockState.Good; map.BytesGood += len; }
                    else { map.BytesBad += (long)bad * map.SectorSize; map.BytesGood += len - (long)bad * map.SectorSize; }
                    break;
            }
        }
        return map;
    }
}
