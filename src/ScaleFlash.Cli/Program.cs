using System.Diagnostics;
using System.Text;
using ScaleFlash.Core.Devices;
using ScaleFlash.Core.Engine;
using ScaleFlash.Core.Imaging;
using ScaleFlash.Core.IO;
using ScaleFlash.Core.Native;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

try
{
    return args[0] switch
    {
        "list" => List(args),
        "info" => Info(args),
        "write" => Write(args),
        "backup" => Backup(args),
        "bench" => Bench(args),
        _ when args.Length >= 2 && InspectCommands.Run(args) is { } code => code,
        _ => Usage($"Unknown command '{args[0]}'"),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        ScaleFlash CLI

          list [--all]                          list card readers / removable disks
          info <image>                          show partition table and file systems of an image
          write <image> --disk N [options]      write an image to a card (needs Administrator)
          write <image> --file PATH [options]   write an image to a file (for testing)
              --no-skip        always write every chunk (disable skip-identical)
              --verify         read back and compare after writing
              --chunk MB       I/O chunk size in MiB (default 4)
              --yes            do not ask for confirmation
              --allow-fixed    allow fixed USB disks;  --allow-virtual  allow VHDs (testing)
          backup --disk N <out> [--whole] [--verify]   read a card into an image file
          bench --disk N [--size MB] [--yes]    DESTRUCTIVE write-speed test of chunk sizes
        """ + InspectCommands.Help);
}

static int Usage(string message)
{
    Console.Error.WriteLine(message);
    PrintHelp();
    return 2;
}

static string? Opt(string[] a, string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
static bool Flag(string[] a, string name) => a.Contains(name);

static int List(string[] a)
{
    bool all = Flag(a, "--all");
    var disks = DiskEnumerator.Enumerate();
    foreach (var d in disks)
    {
        var e = d.GetEligibility(allowFixedUsb: Flag(a, "--allow-fixed"), allowVirtual: Flag(a, "--allow-virtual"));
        if (!all && !d.IsUsbLike && e != DiskEligibility.Eligible) continue;
        string media = d.MediaPresent ? Format.Bytes(d.Size) : "no media";
        string letters = string.Join(",", d.DriveLetters);
        string layout = d.Layout is { } l ? $"{l.SchemeName} {string.Join(", ", l.Partitions.Select(p => $"{p.TypeName} {Format.Bytes(p.Length)}"))}" : "";
        Console.WriteLine($"#{d.Number,-2} {d.Model,-28} {d.Bus,-8} {media,-10} {(d.RemovableMedia ? "removable" : "fixed"),-9} {e,-14} {letters,-6} {layout}");
    }
    return 0;
}

static int Info(string[] a)
{
    if (a.Length < 2) return Usage("info needs an image path");
    var info = ImageInfo.Analyze(a[1]);
    Console.WriteLine($"File:        {info.Path}");
    Console.WriteLine($"Size:        {Format.Bytes(info.Size)} ({info.Size:N0} bytes, {info.Size / 512:N0} sectors)");
    Console.WriteLine($"Partitions:  {info.Layout.SchemeName}");
    foreach (var p in info.Partitions)
    {
        string used = p.FileSystem.UsedBytes is { } u ? $", used {Format.Bytes(u)} ({p.FileSystem.UsedFraction:P0})" : "";
        string rec = p.FileSystem.NeedsRecovery ? ", journal needs recovery" : "";
        Console.WriteLine($"  P{p.Partition.Index + 1}: {p.Partition.TypeName}, start {p.Partition.StartOffset / 512}, size {Format.Bytes(p.Partition.Length)}, fs {p.FileSystem.Type}{used}{rec}");
    }
    if (info.Bootloader is not null) Console.WriteLine($"Bootloader:  {info.Bootloader}");
    foreach (var w in info.Warnings) Console.WriteLine($"Warning:     {w}");
    return 0;
}

static FlashOptions ReadOptions(string[] a) => new()
{
    ChunkSize = (int.TryParse(Opt(a, "--chunk"), out int mb) ? mb : 4) << 20,
    SkipIdenticalChunks = !Flag(a, "--no-skip"),
    Verify = Flag(a, "--verify"),
};

static int Write(string[] a)
{
    if (a.Length < 2) return Usage("write needs an image path");
    string image = a[1];
    var options = ReadOptions(a);
    var progress = new ConsoleProgress();
    FlashResult result;

    if (Opt(a, "--file") is { } file)
    {
        using var src = FileBlockDevice.OpenRead(image);
        using var dst = File.Exists(file) ? FileBlockDevice.OpenReadWrite(file) : FileBlockDevice.Create(file, src.Length);
        result = FlashEngine.Run(src, src.Length, dst, options, progress, CancelOnCtrlC());
    }
    else if (int.TryParse(Opt(a, "--disk"), out int n))
    {
        var disk = DiskEnumerator.Probe(n) ?? throw new InvalidOperationException($"Disk {n} not found");
        bool allowFixed = Flag(a, "--allow-fixed"), allowVirtual = Flag(a, "--allow-virtual");
        var e = disk.GetEligibility(allowFixed, allowVirtual);
        if (e != DiskEligibility.Eligible) throw new InvalidOperationException(CardJobs.Describe(e));
        if (!Confirm(a, $"ERASE everything on #{disk.Number} {disk.Model} ({Format.Bytes(disk.Size)}) and write {Path.GetFileName(image)}?"))
            return 3;
        var req = new WriteRequest(image, DiskIdentity.Of(disk), options) { AllowFixedUsb = allowFixed, AllowVirtual = allowVirtual };
        result = CardJobs.WriteImage(req, progress, CancelOnCtrlC());
    }
    else return Usage("write needs --disk N or --file PATH");

    return PrintResult(result);
}

static int Backup(string[] a)
{
    if (!int.TryParse(Opt(a, "--disk"), out int n)) return Usage("backup needs --disk N");
    string? output = a.Skip(1).FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal) && x != n.ToString());
    if (output is null) return Usage("backup needs an output path");
    var disk = DiskEnumerator.Probe(n) ?? throw new InvalidOperationException($"Disk {n} not found");
    if (File.Exists(output) && !Confirm(a, $"Overwrite {output}?")) return 3;
    var req = new BackupRequest(DiskIdentity.Of(disk), output, Flag(a, "--whole"), Flag(a, "--verify"));
    return PrintResult(CardJobs.Backup(req, new ConsoleProgress(), CancelOnCtrlC()));
}

/// <summary>Writes pseudo-random data with several chunk sizes and reports the sustained speed of each.</summary>
static int Bench(string[] a)
{
    if (!int.TryParse(Opt(a, "--disk"), out int n)) return Usage("bench needs --disk N");
    int sizeMb = int.TryParse(Opt(a, "--size"), out int s) ? s : 256;
    var disk = DiskEnumerator.Probe(n) ?? throw new InvalidOperationException($"Disk {n} not found");
    bool allowVirtual = Flag(a, "--allow-virtual");
    var e = disk.GetEligibility(Flag(a, "--allow-fixed"), allowVirtual);
    if (e != DiskEligibility.Eligible) throw new InvalidOperationException(CardJobs.Describe(e));
    if (!Confirm(a, $"Benchmark OVERWRITES the first {sizeMb * 5} MB of #{disk.Number} {disk.Model}. Continue?")) return 3;

    using var locks = VolumeManager.LockAndDismount(n);
    using var dev = DiskBlockDevice.Open(n, writable: true);
    using var buf = new AlignedBuffer(16 << 20);
    new Random(1).NextBytes(buf.Span);
    long bytes = (long)sizeMb << 20;
    long region = 0;
    foreach (int chunkMb in new[] { 1, 2, 4, 8, 16 })
    {
        int chunk = chunkMb << 20;
        var sw = Stopwatch.StartNew();
        for (long off = 0; off < bytes; off += chunk)
            dev.Write(buf.Slice(0, chunk), region + off);
        dev.Flush();
        sw.Stop();
        Console.WriteLine($"write chunk {chunkMb,2} MiB: {bytes / sw.Elapsed.TotalSeconds / 1e6,6:0.0} MB/s");
        region += bytes;
    }
    var rsw = Stopwatch.StartNew();
    for (long off = 0; off < bytes; off += 4 << 20) dev.Read(buf.Slice(0, 4 << 20), off);
    Console.WriteLine($"read  chunk  4 MiB: {bytes / rsw.Elapsed.TotalSeconds / 1e6,6:0.0} MB/s");
    Console.WriteLine("The card now holds test data; write an image to it before use.");
    return 0;
}

static bool Confirm(string[] a, string question)
{
    if (Flag(a, "--yes")) return true;
    Console.Write(question + " Type YES to continue: ");
    return Console.ReadLine()?.Trim() == "YES";
}

static CancellationToken CancelOnCtrlC()
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return cts.Token;
}

static int PrintResult(FlashResult r)
{
    Console.WriteLine();
    if (r.Success)
    {
        Console.WriteLine($"Done: {Format.Bytes(r.TotalBytes)} in {Format.Duration(r.TotalDuration)} " +
                          $"(write {Format.Duration(r.WriteDuration)} @ {Format.Speed(r.WriteBytesPerSecond)}, " +
                          $"written {Format.Bytes(r.BytesWritten)}, skipped {Format.Bytes(r.BytesSkipped)}" +
                          (r.BytesVerified > 0 ? $", verified {Format.Bytes(r.BytesVerified)} in {Format.Duration(r.VerifyDuration)}" : "") + ")");
        return 0;
    }
    Console.WriteLine(r.Cancelled ? "Cancelled." : $"FAILED: {r.ErrorMessage}");
    return 1;
}

sealed class ConsoleProgress : IProgress<FlashProgress>
{
    private FlashPhase _last = (FlashPhase)(-1);

    public void Report(FlashProgress p)
    {
        if (p.Phase != _last && _last >= 0) Console.WriteLine();
        _last = p.Phase;
        int width = 30, filled = (int)(p.Fraction * width);
        string bar = new string('#', filled) + new string('.', width - filled);
        string eta = p.Remaining is { } r ? $" ETA {Format.Duration(r)}" : "";
        string extra = p.Phase == FlashPhase.Writing ? $" written {Format.Bytes(p.BytesWritten)} skipped {Format.Bytes(p.BytesSkipped)}" : "";
        string msg = p.Message is null ? "" : "  " + p.Message;
        Console.Write($"\r{p.Phase,-10} [{bar}] {p.Fraction * 100,5:0.0}% {Format.Speed(p.BytesPerSecond),11}{eta}{extra}{msg}   ");
    }
}
