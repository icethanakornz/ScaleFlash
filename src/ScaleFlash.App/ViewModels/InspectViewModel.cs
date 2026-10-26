using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ScaleFlash.App.Services;
using ScaleFlash.Core.Devices;
using ScaleFlash.Core.Engine;
using ScaleFlash.Core.FileSystems.Ext;
using ScaleFlash.Core.Inspection;
using ScaleFlash.Core.IO;

namespace ScaleFlash.App.ViewModels;

/// <summary>Something that can be inspected: a USB stick / card, or an image file.</summary>
public sealed record SourceItem(string Title, string Subtitle, int? DiskNumber, string? FilePath, bool IsUsb)
{
    public string Key => DiskNumber is { } n ? $"disk:{n}" : "file:" + FilePath;
    public override string ToString() => Title;
}

public sealed record PartitionCard(
    string Title,
    string Details,
    string? State,
    StatusKind StateKind,
    double UsedPercent,
    bool HasUsage,
    InspectedPartition Partition);

public sealed record FileRow(string Name, string Path, ExtFileType Type, long Size, string SizeText, string Modified, string Permissions, string? LinkTarget)
{
    public bool IsDirectory => Type == ExtFileType.Directory;
    public string Icon => Type switch
    {
        ExtFileType.Directory => "Folder24",
        ExtFileType.Symlink => "Link24",
        _ => "Document24",
    };
}

public partial class InspectViewModel : ObservableObject
{
    private readonly DiskWatcher _watcher = DiskWatcher.Shared;
    private DeviceInspection? _inspection;
    private ExtFileSystem? _fs;
    private CancellationTokenSource? _workCts;
    private readonly List<SourceItem> _fileSources = [];
    private bool _suppressOpen;

    public InspectViewModel()
    {
        _watcher.Changed += OnDisksChanged;
        _watcher.BusyChanged += OnBusyChanged;
        OnDisksChanged(_watcher.Current);
    }

    public bool IsElevated => SystemHelpers.IsElevated;

    /// <summary>Image path suggested for comparisons (the one selected on the write page).</summary>
    public Func<string?>? DefaultImagePath { get; set; }

    public ObservableCollection<SourceItem> Sources { get; } = [];
    public ObservableCollection<PartitionCard> Partitions { get; } = [];
    public ObservableCollection<VersionFile> VersionFiles { get; } = [];
    public ObservableCollection<FileRow> Entries { get; } = [];
    public ObservableCollection<FileDifference> Differences { get; } = [];
    public ObservableCollection<PartitionCard> BrowsablePartitions { get; } = [];

    [ObservableProperty] private SourceItem? _selectedSource;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] private string _deviceTitle = "";
    [ObservableProperty] private string _deviceSubtitle = "";
    [ObservableProperty] private string? _osName;
    [ObservableProperty] private string? _hostname;
    [ObservableProperty] private string? _kernels;
    [ObservableProperty] private string? _bootloader;
    [ObservableProperty] private string? _scaleVersion;
    [ObservableProperty] private bool _hasSystemInfo;

    [ObservableProperty] private PartitionCard? _browsePartition;
    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private FileRow? _selectedEntry;
    [ObservableProperty] private string? _browserStatus;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _busyProgress;
    [ObservableProperty] private bool _busyIndeterminate;
    [ObservableProperty] private string? _busyText;

    [ObservableProperty] private string? _compareSummary;
    [ObservableProperty] private StatusKind _compareKind;
    [ObservableProperty] private int _selectedTab;

    [ObservableProperty] private bool _canHideFromWindows;
    [ObservableProperty] private string? _hideStatus;

    // ------------------------------------------------------------ sources

    private void OnDisksChanged(IReadOnlyList<DiskInfo> all)
    {
        var settings = AppSettings.Current;
        var devices = all.Where(d => !d.IsSystemDisk && d.MediaPresent &&
                                     (d.IsCardLike || d.IsSmallUsb || (settings.ShowFixedUsbDisks && d.IsUsbLike)))
            .Select(d => new SourceItem(
                d.Model + (d.DriveLetters.Any() ? $" ({string.Join(" ", d.DriveLetters)})" : ""),
                $"ดิสก์ #{d.Number} · {Format.Bytes(d.Size)} · {(d.LooksLikeCardReader ? "การ์ด" : "USB")}",
                d.Number, null, !d.LooksLikeCardReader))
            .ToList();

        string? keep = SelectedSource?.Key;
        var wanted = devices.Concat(_fileSources).ToList();
        if (!wanted.Select(w => w.Key + w.Title).SequenceEqual(Sources.Select(s => s.Key + s.Title)))
        {
            Sources.Clear();
            foreach (var s in wanted) Sources.Add(s);
            var again = Sources.FirstOrDefault(s => s.Key == keep);
            if (again is not null)
            {
                _suppressOpen = true;
                SelectedSource = again;
                _suppressOpen = false;
            }
            else if (keep is not null && keep.StartsWith("disk:", StringComparison.Ordinal))
            {
                CloseInspection();
                SelectedSource = null;
                Error = "อุปกรณ์ถูกถอดออกแล้ว";
            }
        }
    }

    private void OnBusyChanged(int disk, bool busy)
    {
        if (busy && SelectedSource?.DiskNumber == disk)
        {
            CloseInspection();
            Error = "อุปกรณ์นี้กำลังถูกเขียนอยู่ เปิดดูใหม่ได้เมื่อเขียนเสร็จ";
        }
    }

    public void SelectDisk(int diskNumber)
    {
        var s = Sources.FirstOrDefault(x => x.DiskNumber == diskNumber);
        if (s is not null) SelectedSource = s;
    }

    [RelayCommand]
    private void OpenImageFile()
    {
        var dlg = new OpenFileDialog { Title = "เปิดไฟล์ image เพื่อตรวจสอบ", Filter = "ไฟล์ทั้งหมด (*.*)|*.*" };
        if (DefaultImagePath?.Invoke() is { } p && Path.GetDirectoryName(p) is { } dir && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() != true) return;
        OpenFile(dlg.FileName);
    }

    public void OpenFile(string path)
    {
        var item = _fileSources.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new SourceItem(Path.GetFileName(path), "ไฟล์ image · " + Path.GetDirectoryName(path), null, path, false);
            _fileSources.Add(item);
            Sources.Add(item);
        }
        SelectedSource = item;
    }

    partial void OnSelectedSourceChanged(SourceItem? value)
    {
        if (value is not null && !_suppressOpen) _ = OpenSourceAsync(value);
    }

    [RelayCommand]
    private Task ReloadAsync() => SelectedSource is { } s ? OpenSourceAsync(s) : Task.CompletedTask;

    private async Task OpenSourceAsync(SourceItem source)
    {
        CloseInspection();
        Error = null;
        if (source.DiskNumber is { } n)
        {
            if (!IsElevated) { Error = "ต้องเปิดโปรแกรมแบบ Administrator จึงจะอ่าน USB / การ์ดได้"; return; }
            if (_watcher.IsBusy(n)) { Error = "อุปกรณ์นี้กำลังถูกเขียนอยู่"; return; }
        }

        IsLoading = true;
        try
        {
            var ins = await Task.Run(() =>
            {
                IBlockDevice dev = source.DiskNumber is { } d
                    ? DiskBlockDevice.Open(d, writable: false)
                    : FileBlockDevice.OpenRead(source.FilePath!);
                return DeviceInspection.Open(dev, source.Title);
            });
            if (SelectedSource != source) { ins.Dispose(); return; }
            _inspection = ins;
            Populate(source, ins);
            IsOpen = true;
        }
        catch (Exception ex)
        {
            Error = ex is DeviceIoException io ? ErrorText.ForIo(io, isSource: true) : "อ่านไม่สำเร็จ: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Populate(SourceItem source, DeviceInspection ins)
    {
        DeviceTitle = source.Title;
        DeviceSubtitle = $"{Format.Bytes(ins.Size)} · {ins.Layout.SchemeName} · {ins.Partitions.Count} พาร์ทิชัน";
        Partitions.Clear();
        BrowsablePartitions.Clear();
        foreach (var p in ins.Partitions)
        {
            var card = MakeCard(p);
            Partitions.Add(card);
            if (p.Ext is not null) BrowsablePartitions.Add(card);
        }

        var si = ins.System;
        OsName = si?.OsName;
        Hostname = si?.Hostname;
        Kernels = si is { Kernels.Count: > 0 } ? string.Join(", ", si.Kernels) : null;
        Bootloader = ins.Bootloader;
        VersionFiles.Clear();
        ScaleVersion = null;
        if (si is not null)
        {
            foreach (var v in si.VersionFiles) VersionFiles.Add(v);
            var sd = si.VersionFiles.FirstOrDefault(v => v.Path.EndsWith(".sdVersion", StringComparison.OrdinalIgnoreCase));
            if (sd is not null)
            {
                var lines = sd.Content.Split('\n').Select(l => l.Trim()).ToList();
                string? ver = lines.FirstOrDefault(l => l.StartsWith("Version=", StringComparison.OrdinalIgnoreCase))?[8..];
                string? date = lines.FirstOrDefault(l => l.StartsWith("Date=", StringComparison.OrdinalIgnoreCase))?[5..];
                if (ver is not null) ScaleVersion = date is null ? ver : $"{ver} ({date})";
            }
        }
        HasSystemInfo = OsName is not null || Hostname is not null || Kernels is not null || Bootloader is not null || VersionFiles.Count > 0;

        BrowsePartition = BrowsablePartitions.FirstOrDefault(c => c.Partition == ins.MainPartition) ?? BrowsablePartitions.FirstOrDefault();
        UpdateHideState();
    }

    private static PartitionCard MakeCard(InspectedPartition p)
    {
        string title = $"พาร์ทิชัน {p.Partition.Index + 1} · {p.Partition.TypeName}";
        if (p.Ext is { } fs)
        {
            var sb = fs.Superblock;
            string details = $"{sb.TypeName}{(sb.VolumeName.Length > 0 ? $" \"{sb.VolumeName}\"" : "")} · ใช้ {Format.Bytes(sb.UsedBytes)} จาก {Format.Bytes(sb.TotalBytes)}" +
                             (sb.WriteTime is { } w ? $" · เขียนล่าสุด {w.LocalDateTime:dd/MM/yyyy HH:mm}" : "") +
                             $" · mount แล้ว {sb.MountCount:N0} ครั้ง";
            string? state;
            StatusKind kind;
            if (sb.HasErrors) { state = "ระบบไฟล์เคยมี error — ควรเขียน image ใหม่"; kind = StatusKind.Error; }
            else if (fs.Warning is not null) { state = fs.Warning; kind = StatusKind.Warning; }
            else if (sb.NeedsRecovery) { state = $"ไม่ได้ปิดเครื่องอย่างถูกต้อง — มีข้อมูลค้างใน journal ({fs.ReplayedTransactions} รายการ) โปรแกรมรวมให้แล้วตอนอ่าน (ไม่ได้แก้ไขอุปกรณ์)"; kind = StatusKind.Warning; }
            else { state = "ปิดเครื่องเรียบร้อย ระบบไฟล์ปกติ"; kind = StatusKind.Ok; }
            double used = sb.TotalBytes > 0 ? 100.0 * sb.UsedBytes / sb.TotalBytes : 0;
            return new PartitionCard(title, details, state, kind, used, true, p);
        }
        string fsName = p.FileSystem.Type;
        string d2 = $"{fsName} · {Format.Bytes(p.Partition.Length)}";
        if (p.FileSystem.UsedBytes is { } u) d2 += $" · ใช้ {Format.Bytes(u)}";
        return new PartitionCard(title, d2, p.Error, p.Error is null ? StatusKind.Neutral : StatusKind.Error,
            p.FileSystem.UsedFraction * 100 ?? 0, p.FileSystem.UsedFraction is not null, p);
    }

    private void CloseInspection()
    {
        _workCts?.Cancel();
        _inspection?.Dispose();
        _inspection = null;
        _fs = null;
        IsOpen = false;
        Partitions.Clear();
        BrowsablePartitions.Clear();
        Entries.Clear();
        VersionFiles.Clear();
        Differences.Clear();
        CompareSummary = null;
        HideStatus = null;
        CanHideFromWindows = false;
    }

    // ------------------------------------------------------------ browsing

    partial void OnBrowsePartitionChanged(PartitionCard? value)
    {
        _fs = value?.Partition.Ext;
        _ = NavigateAsync("/");
    }

    [RelayCommand]
    private Task UpAsync()
    {
        if (CurrentPath == "/") return Task.CompletedTask;
        string parent = CurrentPath.TrimEnd('/');
        parent = parent[..parent.LastIndexOf('/')];
        return NavigateAsync(parent.Length == 0 ? "/" : parent);
    }

    [RelayCommand]
    private Task GoToAsync(string path) => NavigateAsync(string.IsNullOrWhiteSpace(path) ? "/" : path.Trim());

    public async Task NavigateAsync(string path)
    {
        Entries.Clear();
        if (_fs is not { } fs) return;
        try
        {
            var rows = await Task.Run(() =>
            {
                var dir = fs.Lookup(path) ?? throw new DirectoryNotFoundException("ไม่พบโฟลเดอร์ " + path);
                if (dir.Type != ExtFileType.Directory) throw new DirectoryNotFoundException(path + " ไม่ใช่โฟลเดอร์");
                string basePath = path.TrimEnd('/');
                return fs.ListDirectory(dir).Select(e =>
                    {
                        var i = fs.GetInode(e.Inode);
                        string? link = i.Type == ExtFileType.Symlink ? fs.ReadSymlink(i) : null;
                        return new FileRow(e.Name, basePath + "/" + e.Name, i.Type, i.Size,
                            i.Type == ExtFileType.File ? Format.Bytes(i.Size) : i.Type == ExtFileType.Symlink ? "→ " + link : "",
                            i.Mtime.LocalDateTime.ToString("dd/MM/yyyy HH:mm"), i.Permissions, link);
                    })
                    .OrderBy(r => r.IsDirectory ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
            CurrentPath = path.Length == 0 ? "/" : path;
            foreach (var r in rows) Entries.Add(r);
            int dirs = rows.Count(r => r.IsDirectory);
            BrowserStatus = $"{rows.Count - dirs} ไฟล์ · {dirs} โฟลเดอร์ · รวม {Format.Bytes(rows.Where(r => r.Type == ExtFileType.File).Sum(r => r.Size))}";
        }
        catch (Exception ex)
        {
            BrowserStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenEntryAsync(FileRow? row)
    {
        row ??= SelectedEntry;
        if (row is null) return;
        if (row.IsDirectory) await NavigateAsync(row.Path);
        else if (row.Type == ExtFileType.Symlink && row.LinkTarget is { } t && _fs?.Lookup(row.Path) is { Type: ExtFileType.Directory })
            await NavigateAsync(row.Path);
        else await PreviewAsync(row);
    }

    [RelayCommand]
    private async Task PreviewAsync(FileRow? row)
    {
        row ??= SelectedEntry;
        if (row is null || _fs is not { } fs) return;
        try
        {
            string text = await Task.Run(() =>
            {
                var inode = fs.Lookup(row.Path) ?? throw new FileNotFoundException(row.Path);
                if (inode.Type != ExtFileType.File) return row.LinkTarget is { } l ? "ลิงก์ไปที่: " + l : "(ไม่ใช่ไฟล์ปกติ)";
                const int max = 512 * 1024;
                var buf = new byte[Math.Min(inode.Size, max)];
                fs.ReadFile(inode, fs.GetBlockRuns(inode), 0, buf);
                string suffix = inode.Size > max ? $"\n\n… (แสดง {Format.Bytes(max)} แรกจาก {Format.Bytes(inode.Size)})" : "";
                bool binary = buf.AsSpan(0, Math.Min(buf.Length, 8192)).Contains((byte)0);
                return (binary ? HexDump(buf, 4096) : Encoding.UTF8.GetString(buf)) + suffix;
            });
            await Dialogs.ShowTextAsync(row.Path, text);
        }
        catch (Exception ex)
        {
            BrowserStatus = "เปิดไฟล์ไม่ได้: " + ex.Message;
        }
    }

    private static string HexDump(byte[] data, int max)
    {
        var sb = new StringBuilder("(ไฟล์ binary — แสดงแบบ hex)\n\n");
        for (int i = 0; i < Math.Min(data.Length, max); i += 16)
        {
            var line = data.AsSpan(i, Math.Min(16, data.Length - i));
            sb.Append($"{i:X8}  ");
            for (int j = 0; j < 16; j++) sb.Append(j < line.Length ? $"{line[j]:X2} " : "   ");
            sb.Append(' ');
            foreach (byte b in line) sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [RelayCommand]
    private async Task ExtractAsync()
    {
        if (_fs is not { } fs) return;
        string source = SelectedEntry?.Path ?? CurrentPath;
        var dlg = new OpenFolderDialog { Title = $"เลือกโฟลเดอร์ปลายทางสำหรับ {source}" };
        if (dlg.ShowDialog() != true) return;
        string dest = dlg.FolderName;

        var cts = BeginWork($"กำลังคัดลอก {source} …", indeterminate: true);
        var progress = new Progress<(long Bytes, string Path)>(p => BusyText = $"กำลังคัดลอก {Format.Bytes(p.Bytes)} · {p.Path}");
        try
        {
            var (files, bytes, skipped) = await Task.Run(() => ExtTree.Extract(fs, source, dest, progress, cts.Token));
            BrowserStatus = $"คัดลอกแล้ว {files} ไฟล์ ({Format.Bytes(bytes)}) ไปที่ {dest}" + (skipped > 0 ? $" · ข้ามไฟล์พิเศษ {skipped} รายการ" : "");
            SystemHelpers.OpenFolder(dest);
        }
        catch (OperationCanceledException) { BrowserStatus = "ยกเลิกการคัดลอกแล้ว"; }
        catch (Exception ex) { BrowserStatus = "คัดลอกไม่สำเร็จ: " + ex.Message; }
        finally { EndWork(); }
    }

    // ------------------------------------------------------------ compare

    private string? PickImagePath(string title)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = "ไฟล์ทั้งหมด (*.*)|*.*" };
        if (DefaultImagePath?.Invoke() is { } p && File.Exists(p))
        {
            dlg.InitialDirectory = Path.GetDirectoryName(p);
            dlg.FileName = Path.GetFileName(p);
        }
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    [RelayCommand]
    private Task CompareFilesAsync() =>
        PickImagePath("เลือกไฟล์ image ต้นฉบับที่จะเทียบ") is { } path ? CompareFilesWithAsync(path) : Task.CompletedTask;

    public async Task CompareFilesWithAsync(string imagePath)
    {
        if (_inspection?.MainPartition?.Ext is not { } device) return;
        var cts = BeginWork("กำลังเปิดไฟล์ image…", indeterminate: true);
        DeviceInspection? image = null;
        var progress = new Progress<CompareProgress>(p =>
        {
            BusyIndeterminate = false;
            BusyProgress = p.Fraction * 100;
            BusyText = $"กำลังเทียบรายไฟล์ {p.Fraction:P0} · {Format.Bytes(p.BytesDone)} / {Format.Bytes(p.BytesTotal)}";
        });
        try
        {
            image = await Task.Run(() => DeviceInspection.Open(FileBlockDevice.OpenRead(imagePath), Path.GetFileName(imagePath)));
            if (image.MainPartition?.Ext is not { } reference)
            {
                CompareSummary = "ไม่พบระบบไฟล์ Linux (ext2/3/4) ในไฟล์ image นี้";
                CompareKind = StatusKind.Error;
                return;
            }
            BusyText = "กำลังอ่านรายการไฟล์ทั้งสองฝั่ง…";
            var r = await Task.Run(() => ExtTreeComparer.Compare(device, reference, progress, cts.Token));
            Differences.Clear();
            foreach (var d in r.Differences) Differences.Add(d);
            if (r.Differences.Count == 0)
            {
                CompareSummary = $"ไฟล์ทุกไฟล์ตรงกับ {image.DisplayName} ({r.FilesCompared:N0} ไฟล์)";
                CompareKind = StatusKind.Ok;
            }
            else
            {
                CompareSummary = $"ต่างจาก {image.DisplayName}: เปลี่ยน {r.Count(DiffKind.Modified)} · มีเพิ่ม {r.Count(DiffKind.Added)} · หายไป {r.Count(DiffKind.Removed)}" +
                                 (r.Count(DiffKind.TypeChanged) > 0 ? $" · ชนิดเปลี่ยน {r.Count(DiffKind.TypeChanged)}" : "");
                CompareKind = StatusKind.Warning;
            }
            SelectedTab = 1;
        }
        catch (OperationCanceledException) { CompareSummary = "ยกเลิกการเทียบแล้ว"; CompareKind = StatusKind.Neutral; }
        catch (Exception ex) { CompareSummary = "เทียบไม่สำเร็จ: " + ex.Message; CompareKind = StatusKind.Error; }
        finally
        {
            image?.Dispose();
            EndWork();
        }
    }

    [RelayCommand]
    private async Task CompareRawAsync()
    {
        if (_inspection is not { } ins) return;
        if (PickImagePath("เลือกไฟล์ image ต้นฉบับที่จะเทียบทุกไบต์") is not { } imagePath) return;
        var cts = BeginWork("กำลังเทียบทุกไบต์…", indeterminate: false);
        var progress = new Progress<CompareProgress>(pr =>
        {
            BusyProgress = pr.Fraction * 100;
            BusyText = $"กำลังเทียบทุกไบต์ {pr.Fraction:P0} · {Format.Bytes(pr.BytesDone)}";
        });
        try
        {
            var r = await Task.Run(() =>
            {
                using var image = FileBlockDevice.OpenRead(imagePath);
                return RawComparer.Compare(ins.Device, image, progress, cts.Token);
            });
            if (r.Identical)
            {
                CompareSummary = $"ข้อมูลตรงกับ {Path.GetFileName(imagePath)} ทุกไบต์ ({Format.Bytes(r.BytesCompared)})";
                CompareKind = StatusKind.Ok;
            }
            else
            {
                CompareSummary = (r.LengthMismatch ? "อุปกรณ์เล็กกว่าไฟล์ image · " : "") +
                                 $"ต่างจาก {Path.GetFileName(imagePath)} {Format.Bytes(r.DifferentBytes)} ({r.DifferentChunks:N0} ช่วง 4 MB) เริ่มที่ตำแหน่ง {Format.Bytes(r.FirstDifference ?? 0)}" +
                                 " — กด \"เทียบรายไฟล์\" เพื่อดูว่าไฟล์ไหนเปลี่ยน";
                CompareKind = StatusKind.Warning;
            }
        }
        catch (OperationCanceledException) { CompareSummary = "ยกเลิกการเทียบแล้ว"; CompareKind = StatusKind.Neutral; }
        catch (Exception ex) { CompareSummary = "เทียบไม่สำเร็จ: " + ex.Message; CompareKind = StatusKind.Error; }
        finally { EndWork(); }
    }

    [RelayCommand]
    private void SaveReport()
    {
        if (Differences.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "บันทึกรายงานการเทียบ",
            Filter = "CSV (*.csv)|*.csv|ข้อความ (*.txt)|*.txt",
            FileName = $"compare_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("สถานะ,ไฟล์,รายละเอียด\n");
        foreach (var d in Differences) sb.Append($"{d.KindText},\"{d.Path.Replace("\"", "\"\"")}\",\"{d.Detail}\"\n");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        BrowserStatus = "บันทึกรายงานแล้ว: " + dlg.FileName;
    }

    // ------------------------------------------------------------ busy + hide

    private CancellationTokenSource BeginWork(string text, bool indeterminate)
    {
        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        IsBusy = true;
        BusyIndeterminate = indeterminate;
        BusyProgress = 0;
        BusyText = text;
        return _workCts;
    }

    private void EndWork()
    {
        IsBusy = false;
        BusyText = null;
    }

    [RelayCommand]
    private void CancelWork() => _workCts?.Cancel();

    private void UpdateHideState()
    {
        CanHideFromWindows = false;
        if (SelectedSource?.DiskNumber is not { } n || !IsElevated) return;
        var disk = _watcher.Current.FirstOrDefault(d => d.Number == n);
        CanHideFromWindows = disk is not null && disk.DriveLetters.Any() &&
                             _inspection?.Partitions.Any(p => p.Ext is not null) == true;
    }

    [RelayCommand]
    private async Task HideFromWindowsAsync()
    {
        if (SelectedSource?.DiskNumber is not { } n) return;
        var removed = await Task.Run(() => VolumeManager.HideUnreadableVolumes(n));
        HideStatus = removed.Count > 0
            ? $"ซ่อนไดรฟ์ {string.Join(", ", removed)} แล้ว — Windows จะไม่ถามให้ Format ไดรฟ์นี้อีก (ข้อมูลไม่ถูกแก้ไข)"
            : "ไม่มีไดรฟ์ที่ต้องซ่อน (Windows อ่านพาร์ทิชันที่มีตัวอักษรไดรฟ์ได้อยู่แล้ว)";
        CanHideFromWindows = false;
        _watcher.RefreshNow();
    }
}
