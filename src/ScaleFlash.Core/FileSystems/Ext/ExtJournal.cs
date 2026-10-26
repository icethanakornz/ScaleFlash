using System.Buffers.Binary;

namespace ScaleFlash.Core.FileSystems.Ext;

internal static class Jbd2
{
    public const uint Magic = 0xC03B3998;
    public const int Descriptor = 1, Commit = 2, SuperblockV1 = 3, SuperblockV2 = 4, Revoke = 5;

    public const uint Incompat64Bit = 0x2;
    public const uint IncompatCsumV2 = 0x8;
    public const uint IncompatCsumV3 = 0x10;
    public const uint IncompatFastCommit = 0x20;

    public const int FlagEscape = 1, FlagSameUuid = 2, FlagLastTag = 8;
}

public sealed partial class ExtFileSystem
{
    /// <summary>
    /// In-memory journal recovery, following the jbd2 algorithm: scan committed transactions starting at
    /// s_start, honour revoke records, and map each journaled block to its newest copy. Nothing is written.
    /// </summary>
    private void ReplayJournal()
    {
        if (_sb.JournalInode == 0) { Warning = "ระบบไฟล์ใช้ journal ภายนอก ข้ามการกู้ journal"; return; }
        var jInode = GetInode(_sb.JournalInode);
        var runs = GetBlockRuns(jInode);
        long jBlocks = jInode.Size / BlockSize;
        if (jBlocks < 2 || runs.Count == 0) throw new ExtFormatException("journal ว่างหรือเสียหาย");

        long Map(long logical)
        {
            foreach (var r in runs)
                if (logical >= r.Logical && logical < r.Logical + r.Count) return r.Physical + (logical - r.Logical);
            throw new ExtFormatException($"journal block {logical} ไม่มีตำแหน่งจริง");
        }

        byte[] ReadJ(long logical) => _dev.Read(Map(logical) * BlockSize, BlockSize);

        var jsb = ReadJ(0);
        if (BE32(jsb, 0) != Jbd2.Magic) throw new ExtFormatException("journal superblock เสียหาย");
        int sbType = (int)BE32(jsb, 4);
        if (sbType is not (Jbd2.SuperblockV1 or Jbd2.SuperblockV2)) throw new ExtFormatException("ชนิด journal ไม่รู้จัก");
        if (BE32(jsb, 12) != BlockSize) throw new ExtFormatException("ขนาดบล็อกของ journal ไม่ตรงกับระบบไฟล์");

        long maxLen = BE32(jsb, 16), first = BE32(jsb, 20);
        uint sequence = BE32(jsb, 24);
        long start = BE32(jsb, 28);
        if (start == 0) return; // journal empty: nothing to replay
        uint incompat = sbType == Jbd2.SuperblockV2 ? BE32(jsb, 40) : 0;
        bool b64 = (incompat & Jbd2.Incompat64Bit) != 0;
        bool csum3 = (incompat & Jbd2.IncompatCsumV3) != 0, csum2 = (incompat & Jbd2.IncompatCsumV2) != 0;
        int tagBytes = csum3 ? 16 : 12 + (csum2 ? 2 : 0) - (b64 ? 0 : 4);
        int tailBytes = csum2 || csum3 ? 4 : 0;
        long last = Math.Min(maxLen, jBlocks);
        if ((incompat & Jbd2.IncompatFastCommit) != 0)
        {
            long fc = BE32(jsb, 0x54);
            last -= fc == 0 ? 256 : fc;
            Warning = "journal มี fast commit ซึ่งยังไม่รองรับ ข้อมูลล่าสุดบางส่วนอาจไม่แสดง";
        }
        if (first < 1 || first >= last || start < first || start >= last) throw new ExtFormatException("ตำแหน่งใน journal ผิดปกติ");

        long Wrap(long x) => x >= last ? x - (last - first) : x;

        var committed = new List<(uint Seq, List<(long Target, long JLogical, bool Escaped)> Tags)>();
        var revoked = new Dictionary<long, uint>();
        var pendingTags = new List<(long, long, bool)>();
        var pendingRevokes = new List<long>();
        long next = start;
        uint expect = sequence;

        for (long guard = 0; guard < last * 2; guard++)
        {
            var blk = ReadJ(next);
            if (BE32(blk, 0) != Jbd2.Magic || BE32(blk, 8) != expect) break;
            int type = (int)BE32(blk, 4);
            if (type == Jbd2.Descriptor)
            {
                int limit = BlockSize - tailBytes;
                long dataPos = Wrap(next + 1);
                int off = 12;
                while (off + tagBytes <= limit)
                {
                    long target = BE32(blk, off);
                    int flags = BinaryPrimitives.ReadUInt16BigEndian(blk.AsSpan(off + 6));
                    if (b64) target |= (long)BE32(blk, off + 8) << 32;
                    pendingTags.Add((target, dataPos, (flags & Jbd2.FlagEscape) != 0));
                    dataPos = Wrap(dataPos + 1);
                    off += tagBytes;
                    if ((flags & Jbd2.FlagSameUuid) == 0) off += 16;
                    if ((flags & Jbd2.FlagLastTag) != 0) break;
                }
                next = dataPos;
            }
            else if (type == Jbd2.Commit)
            {
                committed.Add((expect, pendingTags));
                foreach (long r in pendingRevokes)
                    revoked[r] = revoked.TryGetValue(r, out uint s) && s > expect ? s : expect;
                pendingTags = [];
                pendingRevokes = [];
                expect++;
                next = Wrap(next + 1);
            }
            else if (type == Jbd2.Revoke)
            {
                int count = (int)BE32(blk, 12);
                int recSize = b64 ? 8 : 4;
                for (int off = 16; off + recSize <= Math.Min(count, BlockSize); off += recSize)
                    pendingRevokes.Add(b64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(blk.AsSpan(off)) : BE32(blk, off));
                next = Wrap(next + 1);
            }
            else break;
        }

        foreach (var (seq, tags) in committed)
        {
            foreach (var (target, jLogical, escaped) in tags)
            {
                if (revoked.TryGetValue(target, out uint rs) && seq <= rs) continue;
                if (target < 0 || target >= _sb.BlocksCount) continue; // block 0 (superblock) is often journaled
                _overlay[target] = (Map(jLogical), escaped);
            }
        }
        _overlayKeys = new SortedSet<long>(_overlay.Keys);
        ReplayedTransactions = committed.Count;
    }

    private static uint BE32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
}
