using System.Buffers.Binary;
using System.Text;
using ScaleFlash.Core.IO;

namespace ScaleFlash.Core.FileSystems.Ext;

/// <summary>
/// Read-only ext2/ext3/ext4 reader. Supports extents and indirect blocks, htree directories, symlinks,
/// sparse and pre-allocated files, 64-bit and flex_bg layouts, and replays the journal in memory when
/// the file system was not unmounted cleanly (as happens when a scale is switched off), so what you
/// see matches what Linux would see after mounting — without ever writing to the device.
/// </summary>
public sealed partial class ExtFileSystem
{
    public const uint RootInode = 2;

    private readonly DeviceReader _dev;
    private ExtSuperblock _sb;
    private long[] _inodeTables = [];
    private readonly Dictionary<long, (long JournalPhysical, bool Escaped)> _overlay = [];
    private SortedSet<long> _overlayKeys = [];

    private ExtFileSystem(DeviceReader dev, ExtSuperblock sb)
    {
        _dev = dev;
        _sb = sb;
    }

    public ExtSuperblock Superblock => _sb;
    public int BlockSize => _sb.BlockSize;

    /// <summary>Number of blocks taken from the journal (0 when the file system was clean).</summary>
    public int ReplayedBlocks => _overlay.Count;
    public int ReplayedTransactions { get; private set; }
    public string? Warning { get; private set; }

    public static bool Probe(DeviceReader dev)
    {
        if (dev.Length < 2048) return false;
        var m = dev.Read(1024 + 0x38, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(m) == ExtSuperblock.Magic;
    }

    /// <param name="partition">A reader positioned on the partition (offset 0 = start of the file system).</param>
    public static ExtFileSystem Open(DeviceReader partition, bool replayJournal = true)
    {
        var sb = ExtSuperblock.Parse(partition.Read(1024, 1024));
        if ((sb.FeatureIncompat & ExtSuperblock.IncompatJournalDev) != 0)
            throw new ExtFormatException("นี่คืออุปกรณ์ journal ภายนอก ไม่ใช่ระบบไฟล์");
        if ((sb.FeatureIncompat & ExtSuperblock.IncompatMetaBg) != 0)
            throw new ExtFormatException("ยังไม่รองรับระบบไฟล์แบบ meta_bg");
        if ((sb.FeatureRoCompat & ExtSuperblock.RoCompatBigalloc) != 0)
            throw new ExtFormatException("ยังไม่รองรับระบบไฟล์แบบ bigalloc");

        var fs = new ExtFileSystem(partition, sb);
        fs.LoadGroupDescriptors();
        if (replayJournal && sb.HasJournal && sb.NeedsRecovery)
        {
            try
            {
                fs.ReplayJournal();
                if (fs._overlay.Count > 0)
                {
                    // Superblock and descriptors may themselves have newer copies in the journal.
                    fs._sb = ExtSuperblock.Parse(fs.ReadBytes(1024, 1024));
                    fs.LoadGroupDescriptors();
                }
            }
            catch (Exception ex) when (ex is ExtFormatException or IOException)
            {
                fs._overlay.Clear();
                fs._overlayKeys = [];
                fs.Warning = "อ่าน journal ไม่ได้ ข้อมูลที่แสดงอาจไม่ใช่ล่าสุด: " + ex.Message;
            }
        }
        return fs;
    }

    // ---------------------------------------------------------------- blocks

    /// <summary>Reads one file-system block, taking journal-replayed copies into account.</summary>
    public byte[] ReadBlock(long block)
    {
        if (_overlay.TryGetValue(block, out var o))
        {
            var data = _dev.Read(o.JournalPhysical * BlockSize, BlockSize);
            if (o.Escaped) BinaryPrimitives.WriteUInt32BigEndian(data, Jbd2.Magic);
            return data;
        }
        return _dev.Read(block * BlockSize, BlockSize);
    }

    /// <summary>Reads a run of contiguous blocks into <paramref name="dest"/> (overlay-aware).</summary>
    private void ReadBlocks(long firstBlock, Span<byte> dest)
    {
        long count = dest.Length / BlockSize;
        if (_overlayKeys.Count == 0 || _overlayKeys.GetViewBetween(firstBlock, firstBlock + count - 1).Count == 0)
        {
            _dev.Read(firstBlock * BlockSize, dest);
            return;
        }
        for (long i = 0; i < count; i++)
            ReadBlock(firstBlock + i).CopyTo(dest.Slice((int)(i * BlockSize), BlockSize));
    }

    /// <summary>Byte-granular read through the block overlay (used for the superblock).</summary>
    private byte[] ReadBytes(long offset, int count)
    {
        var result = new byte[count];
        long block = offset / BlockSize;
        int within = (int)(offset % BlockSize), done = 0;
        while (done < count)
        {
            var b = ReadBlock(block++);
            int take = Math.Min(count - done, BlockSize - within);
            b.AsSpan(within, take).CopyTo(result.AsSpan(done));
            done += take;
            within = 0;
        }
        return result;
    }

    private void LoadGroupDescriptors()
    {
        long groups = _sb.GroupCount;
        if (groups <= 0 || groups > 10_000_000) throw new ExtFormatException("จำนวน block group ผิดปกติ");
        int ds = _sb.DescSize;
        long gdtBlock = _sb.FirstDataBlock + 1;
        long bytes = groups * ds;
        int blocks = (int)((bytes + BlockSize - 1) / BlockSize);
        var gdt = new byte[blocks * BlockSize];
        for (int i = 0; i < blocks; i++) ReadBlock(gdtBlock + i).CopyTo(gdt, i * BlockSize);
        _inodeTables = new long[groups];
        for (long g = 0; g < groups; g++)
        {
            var d = gdt.AsSpan((int)(g * ds), ds);
            long table = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
            if (_sb.Is64Bit && ds >= 64) table |= (long)BinaryPrimitives.ReadUInt32LittleEndian(d[0x28..]) << 32;
            _inodeTables[g] = table;
        }
    }

    // ---------------------------------------------------------------- inodes

    public ExtInode GetInode(uint number)
    {
        if (number == 0 || number > _sb.InodesCount) throw new ExtFormatException($"inode {number} อยู่นอกช่วง");
        long group = (number - 1) / _sb.InodesPerGroup;
        long index = (number - 1) % _sb.InodesPerGroup;
        long byteOffset = index * _sb.InodeSize;
        long block = _inodeTables[group] + byteOffset / BlockSize;
        int within = (int)(byteOffset % BlockSize);
        var data = ReadBlock(block);
        return new ExtInode(number, data.AsSpan(within, _sb.InodeSize), _sb.InodeSize);
    }

    public ExtInode Root => GetInode(RootInode);

    // ---------------------------------------------------------------- block mapping

    /// <summary>A run of file blocks: logical block, physical block (−1 = hole), length, uninitialized.</summary>
    public readonly record struct BlockRun(long Logical, long Physical, long Count, bool Uninitialized);

    /// <summary>All data runs of an inode, ordered by logical block. Holes are not reported.</summary>
    public List<BlockRun> GetBlockRuns(ExtInode inode)
    {
        var runs = new List<BlockRun>();
        if (inode.HasInlineData) return runs;
        long maxBlocks = (inode.Size + BlockSize - 1) / BlockSize;
        if (inode.UsesExtents) WalkExtents(inode.BlockArea, runs, 0);
        else WalkIndirect(inode, runs, maxBlocks);
        runs.Sort((a, b) => a.Logical.CompareTo(b.Logical));
        return runs;
    }

    private void WalkExtents(ReadOnlySpan<byte> node, List<BlockRun> runs, int level)
    {
        if (level > 8) throw new ExtFormatException("extent tree ลึกผิดปกติ");
        if (BinaryPrimitives.ReadUInt16LittleEndian(node) != 0xF30A) throw new ExtFormatException("extent header เสียหาย");
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(node[2..]);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(node[6..]);
        for (int i = 0; i < entries; i++)
        {
            var e = node.Slice(12 + i * 12, 12);
            if (depth == 0)
            {
                long logical = BinaryPrimitives.ReadUInt32LittleEndian(e);
                int len = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
                long phys = (long)BinaryPrimitives.ReadUInt16LittleEndian(e[6..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                bool uninit = len > 32768;
                if (uninit) len -= 32768;
                if (len > 0) runs.Add(new BlockRun(logical, phys, len, uninit));
            }
            else
            {
                long leaf = (long)BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                WalkExtents(ReadBlock(leaf), runs, level + 1);
            }
        }
    }

    private void WalkIndirect(ExtInode inode, List<BlockRun> runs, long maxBlocks)
    {
        var area = inode.BlockArea;
        long logical = 0;
        for (int i = 0; i < 12 && logical < maxBlocks; i++, logical++)
            AddBlock(runs, logical, BinaryPrimitives.ReadUInt32LittleEndian(area.AsSpan(i * 4)));
        long perBlock = BlockSize / 4;
        long span = 1;
        for (int level = 1; level <= 3 && logical < maxBlocks; level++)
        {
            span *= perBlock;
            uint root = BinaryPrimitives.ReadUInt32LittleEndian(area.AsSpan((11 + level) * 4));
            if (root != 0) WalkIndirectBlock(root, level, logical, runs, maxBlocks);
            logical += span;
        }
    }

    private void WalkIndirectBlock(long block, int level, long firstLogical, List<BlockRun> runs, long maxBlocks)
    {
        var data = ReadBlock(block);
        long perBlock = BlockSize / 4;
        long childSpan = 1;
        for (int i = 1; i < level; i++) childSpan *= perBlock;
        for (int i = 0; i < perBlock; i++)
        {
            long logical = firstLogical + i * childSpan;
            if (logical >= maxBlocks) break;
            uint ptr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
            if (ptr == 0) continue;
            if (level == 1) AddBlock(runs, logical, ptr);
            else WalkIndirectBlock(ptr, level - 1, logical, runs, maxBlocks);
        }
    }

    private static void AddBlock(List<BlockRun> runs, long logical, uint physical)
    {
        if (physical == 0) return;
        if (runs.Count > 0)
        {
            var last = runs[^1];
            if (last.Logical + last.Count == logical && last.Physical + last.Count == physical && !last.Uninitialized)
            {
                runs[^1] = last with { Count = last.Count + 1 };
                return;
            }
        }
        runs.Add(new BlockRun(logical, physical, 1, false));
    }

    // ---------------------------------------------------------------- file data

    /// <summary>Reads file content at <paramref name="offset"/>; returns the number of bytes read (0 at end of file).</summary>
    public int ReadFile(ExtInode inode, IReadOnlyList<BlockRun> runs, long offset, Span<byte> buffer)
    {
        if (offset >= inode.Size) return 0;
        int toRead = (int)Math.Min(buffer.Length, inode.Size - offset);
        var dest = buffer[..toRead];

        if (inode.HasInlineData)
        {
            var inline = inode.BlockArea;
            dest.Clear();
            if (offset < inline.Length) inline.AsSpan((int)offset, (int)Math.Min(inline.Length - offset, toRead)).CopyTo(dest);
            return toRead;
        }

        dest.Clear(); // holes and uninitialized extents read as zeros
        long end = offset + toRead;
        // binary search for the first run that may overlap
        int lo = 0, hi = runs.Count - 1, start = runs.Count;
        long firstBlock = offset / BlockSize;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (runs[mid].Logical + runs[mid].Count > firstBlock) { start = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        for (int r = start; r < runs.Count; r++)
        {
            var run = runs[r];
            long runStart = run.Logical * BlockSize, runEnd = (run.Logical + run.Count) * BlockSize;
            if (runStart >= end) break;
            if (run.Uninitialized) continue;
            long from = Math.Max(runStart, offset), to = Math.Min(runEnd, end);
            if (to <= from) continue;

            // read whole blocks covering [from, to)
            long b0 = from / BlockSize, b1 = (to + BlockSize - 1) / BlockSize;
            long phys0 = run.Physical + (b0 - run.Logical);
            int within = (int)(from - b0 * BlockSize);
            int len = (int)(to - from);
            if (within == 0 && len % BlockSize == 0)
            {
                ReadBlocks(phys0, dest.Slice((int)(from - offset), len));
            }
            else
            {
                var tmp = new byte[(b1 - b0) * BlockSize];
                ReadBlocks(phys0, tmp);
                tmp.AsSpan(within, len).CopyTo(dest[(int)(from - offset)..]);
            }
        }
        return toRead;
    }

    public byte[] ReadAllBytes(ExtInode inode, long maxBytes = 64L << 20)
    {
        if (inode.Size > maxBytes) throw new InvalidOperationException("ไฟล์ใหญ่เกินไป");
        var data = new byte[inode.Size];
        ReadFile(inode, GetBlockRuns(inode), 0, data);
        return data;
    }

    public Stream OpenRead(ExtInode inode) => new ExtFileStream(this, inode);

    public string ReadSymlink(ExtInode inode)
    {
        if (inode.Type != ExtFileType.Symlink) throw new InvalidOperationException("not a symlink");
        if (inode.Size < 60 && !inode.UsesExtents && !inode.HasInlineData)
            return Encoding.UTF8.GetString(inode.BlockArea, 0, (int)inode.Size);
        return Encoding.UTF8.GetString(ReadAllBytes(inode, 4096));
    }

    // ---------------------------------------------------------------- directories

    public List<ExtDirEntry> ListDirectory(ExtInode dir)
    {
        if (dir.Type != ExtFileType.Directory) throw new InvalidOperationException("not a directory");
        if (dir.HasInlineData) throw new ExtFormatException("ยังไม่รองรับโฟลเดอร์แบบ inline_data");
        var result = new List<ExtDirEntry>();
        var runs = GetBlockRuns(dir);
        long blocks = (dir.Size + BlockSize - 1) / BlockSize;
        var block = new byte[BlockSize];
        for (long b = 0; b < blocks; b++)
        {
            ReadFile(dir, runs, b * BlockSize, block);
            int off = 0;
            while (off + 8 <= BlockSize)
            {
                uint ino = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(off));
                int recLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(off + 4));
                if (recLen == 0 && BlockSize >= 65536) recLen = 65536;
                if (recLen < 8 || off + recLen > BlockSize) break; // corrupt or end
                int nameLen = block[off + 6];
                byte fileType = block[off + 7];
                if (!_sb.HasFiletype) { nameLen |= fileType << 8; fileType = 0; }
                if (ino != 0 && nameLen > 0 && 8 + nameLen <= recLen)
                {
                    string name = DecodeName(block.AsSpan(off + 8, nameLen));
                    if (name is not ("." or ".."))
                        result.Add(new ExtDirEntry(name, ino, fileType switch
                        {
                            1 => ExtFileType.File,
                            2 => ExtFileType.Directory,
                            3 => ExtFileType.CharDevice,
                            4 => ExtFileType.BlockDevice,
                            5 => ExtFileType.Fifo,
                            6 => ExtFileType.Socket,
                            7 => ExtFileType.Symlink,
                            _ => ExtFileType.Unknown,
                        }));
                }
                off += recLen;
            }
        }
        return result;
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Linux file names are raw bytes. Most are UTF-8; older software (like some scale data) wrote
    /// Latin-1 names such as "Räucherlachs", which would otherwise show as "R�ucherlachs".
    /// </summary>
    internal static string DecodeName(ReadOnlySpan<byte> raw)
    {
        try { return StrictUtf8.GetString(raw); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(raw); }
    }

    /// <summary>Resolves an absolute path ("/etc/hostname"). Symlinks in the middle are followed.</summary>
    public ExtInode? Lookup(string path, bool followLast = true) => Lookup(path, followLast, 0);

    private ExtInode? Lookup(string path, bool followLast, int depth)
    {
        if (depth > 16) return null;
        var current = Root;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<ExtInode> { current };
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                current = stack[^1];
                continue;
            }
            if (current.Type != ExtFileType.Directory) return null;
            var entry = ListDirectory(current).FirstOrDefault(e => e.Name == part);
            if (entry is null) return null;
            var next = GetInode(entry.Inode);
            bool last = i == parts.Length - 1;
            if (next.Type == ExtFileType.Symlink && (!last || followLast))
            {
                string target = ReadSymlink(next);
                string parent = "/" + string.Join('/', parts.Take(i));
                string resolved = target.StartsWith('/') ? target : parent.TrimEnd('/') + "/" + target;
                string rest = string.Join('/', parts.Skip(i + 1));
                return Lookup(rest.Length > 0 ? resolved + "/" + rest : resolved, followLast, depth + 1);
            }
            current = next;
            stack.Add(current);
        }
        return current;
    }

    private sealed class ExtFileStream(ExtFileSystem fs, ExtInode inode) : Stream
    {
        private readonly List<BlockRun> _runs = fs.GetBlockRuns(inode);
        private long _pos;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inode.Size;
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = fs.ReadFile(inode, _runs, _pos, buffer);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => inode.Size + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
