using ScaleFlash.Core.Native;

namespace ScaleFlash.Core.IO;

/// <summary>
/// Byte-addressable, cached, thread-safe read access to a window (e.g. one partition) of a block device.
/// Hides the sector-alignment rules of raw disks: callers may read any offset and length.
/// </summary>
public sealed class DeviceReader : IDisposable
{
    private const int PageSize = 64 * 1024;
    private const int BulkSize = 1 << 20;

    private readonly IBlockDevice _device;
    private readonly long _base;
    private readonly int _cachePages;
    private readonly Dictionary<long, LinkedListNode<(long Page, byte[] Data)>> _map = [];
    private readonly LinkedList<(long Page, byte[] Data)> _lru = new();
    private readonly object _gate = new();
    private readonly bool _ownsDevice;
    private AlignedBuffer? _bounce;

    public DeviceReader(IBlockDevice device, long baseOffset = 0, long? length = null, int cacheMegabytes = 16, bool ownsDevice = false)
    {
        _device = device;
        _base = baseOffset;
        Length = length ?? device.Length - baseOffset;
        _cachePages = Math.Max(4, cacheMegabytes * 1024 * 1024 / PageSize);
        _ownsDevice = ownsDevice;
    }

    public long Length { get; }
    public IBlockDevice Device => _device;

    /// <summary>A reader over a sub-range (partition) of the same device, sharing nothing but the device.</summary>
    public DeviceReader Slice(long offset, long length) => new(_device, _base + offset, length);

    public byte[] Read(long offset, int count)
    {
        var b = new byte[count];
        Read(offset, b);
        return b;
    }

    /// <summary>Reads <c>dest.Length</c> bytes at <paramref name="offset"/>; bytes past the device end read as zero.</summary>
    public void Read(long offset, Span<byte> dest)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        lock (_gate)
        {
            long abs = _base + offset;
            while (dest.Length > 0)
            {
                long page = abs / PageSize;
                int within = (int)(abs - page * PageSize);

                // Large aligned middle section: bypass the cache.
                if (within == 0 && dest.Length >= BulkSize)
                {
                    int n = ReadBulk(abs, dest[..BulkSize]);
                    abs += n;
                    dest = dest[n..];
                    continue;
                }

                byte[] data = GetPage(page);
                int take = Math.Min(dest.Length, PageSize - within);
                data.AsSpan(within, take).CopyTo(dest);
                abs += take;
                dest = dest[take..];
            }
        }
    }

    private int ReadBulk(long abs, Span<byte> dest)
    {
        _bounce ??= new AlignedBuffer(BulkSize);
        long devLen = _device.Length;
        int available = (int)Math.Clamp(devLen - abs, 0, dest.Length);
        int aligned = available / Math.Max(1, _device.SectorSize) * Math.Max(1, _device.SectorSize);
        if (aligned > 0)
        {
            _device.Read(_bounce.Slice(0, aligned), abs);
            _bounce.Slice(0, aligned).CopyTo(dest);
        }
        if (aligned < dest.Length)
        {
            // tail that is not a whole sector (only for plain files) or past the end
            if (aligned < available)
            {
                byte[] tail = GetPage((abs + aligned) / PageSize);
                int within = (int)((abs + aligned) % PageSize);
                int take = Math.Min(available - aligned, PageSize - within);
                tail.AsSpan(within, take).CopyTo(dest[aligned..]);
                return aligned + take;
            }
            dest[aligned..].Clear();
        }
        return dest.Length;
    }

    private byte[] GetPage(long page)
    {
        if (_map.TryGetValue(page, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Data;
        }

        var data = new byte[PageSize];
        long start = page * PageSize;
        long devLen = _device.Length;
        if (start < devLen)
        {
            int sector = Math.Max(1, _device.SectorSize);
            int len = (int)Math.Min(PageSize, devLen - start);
            int alignedLen = (len + sector - 1) / sector * sector;
            if (start + alignedLen > devLen) alignedLen = len / sector * sector;
            _bounce ??= new AlignedBuffer(BulkSize);
            if (alignedLen > 0)
            {
                _device.Read(_bounce.Slice(0, alignedLen), start);
                _bounce.Slice(0, alignedLen).CopyTo(data);
            }
        }

        var newNode = _lru.AddFirst((page, data));
        _map[page] = newNode;
        if (_map.Count > _cachePages)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Page);
        }
        return data;
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _bounce?.Dispose();
            _bounce = null;
            if (_ownsDevice) _device.Dispose();
        }
    }
}
