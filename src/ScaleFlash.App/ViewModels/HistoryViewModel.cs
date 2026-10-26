using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScaleFlash.App.Services;
using ScaleFlash.Core.Engine;
using ScaleFlash.Core.History;

namespace ScaleFlash.App.ViewModels;

public sealed record HistoryRow(
    string Time,
    string Kind,
    string File,
    string Disk,
    string Result,
    StatusKind Status,
    string Written,
    string Duration,
    string Speed,
    string? Error);

public partial class HistoryViewModel : ObservableObject
{
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty = true;

    public HistoryViewModel() => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        var entries = HistoryStore.Load();
        Rows.Clear();
        foreach (var e in entries)
        {
            var status = e.Success ? StatusKind.Ok : e.Cancelled ? StatusKind.Warning : StatusKind.Error;
            string result = e.Success ? (e.Verified ? "สำเร็จ ✓ ตรวจสอบแล้ว" : "สำเร็จ") : e.Cancelled ? "ยกเลิก" : "ไม่สำเร็จ";
            string written = e.BytesSkipped > 0
                ? $"{Format.Bytes(e.BytesWritten)} (ข้าม {Format.Bytes(e.BytesSkipped)})"
                : Format.Bytes(e.Kind == "backup" ? e.ImageSize : e.BytesWritten);
            Rows.Add(new HistoryRow(
                e.Time.ToString("dd/MM/yyyy HH:mm"),
                e.Kind == "backup" ? "สำรอง" : "เขียน",
                Path.GetFileName(e.ImagePath),
                $"{e.DiskModel} ({Format.Bytes(e.DiskSize)})",
                result,
                status,
                written,
                Format.Duration(TimeSpan.FromSeconds(e.Seconds)),
                e.Success ? $"{e.AverageMBps:0.0} MB/s" : "",
                e.Success ? null : e.Error));
        }

        var today = entries.Where(e => e.Kind == "write" && e.Success && e.Time.Date == DateTime.Today).ToList();
        var allOk = entries.Where(e => e.Kind == "write" && e.Success).ToList();
        Summary = allOk.Count == 0
            ? "ยังไม่มีประวัติการเขียนการ์ด"
            : $"วันนี้เขียนสำเร็จ {today.Count} ใบ  ·  ทั้งหมด {allOk.Count} ใบ  ·  ความเร็วเฉลี่ย {allOk.Average(e => e.AverageMBps):0.0} MB/s  ·  เวลาเฉลี่ยต่อใบ {Format.Duration(TimeSpan.FromSeconds(allOk.Average(e => e.Seconds)))}";
        IsEmpty = Rows.Count == 0;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(HistoryStore.Folder);
        SystemHelpers.OpenFolder(HistoryStore.Folder);
    }
}
