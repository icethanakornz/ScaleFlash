using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScaleFlash.Core.Engine;

namespace ScaleFlash.App.ViewModels;

/// <summary>One running or finished write/backup, shown as a progress card.</summary>
public partial class JobViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = new();
    private readonly bool _isBackup;

    public JobViewModel(string title, string subtitle, int diskNumber, bool isBackup = false)
    {
        Title = title;
        Subtitle = subtitle;
        DiskNumber = diskNumber;
        _isBackup = isBackup;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public int DiskNumber { get; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public FlashResult? Result { get; private set; }
    public double Fraction { get; private set; }

    [ObservableProperty] private string _phaseText = "กำลังเตรียมการ์ด…";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private string? _noteText;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private StatusKind _status = StatusKind.Busy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _isRunning = true;
    [ObservableProperty] private string _elapsedText = "0:00";

    public event Action<JobViewModel>? Finished;
    public event Action<JobViewModel>? RemoveRequested;

    /// <summary>Runs <paramref name="work"/> on a background thread; must be called on the UI thread.</summary>
    public void Start(Func<IProgress<FlashProgress>, CancellationToken, FlashResult> work)
    {
        _clock.Start();
        var progress = new Progress<FlashProgress>(OnProgress);
        var token = _cts.Token;
        Task.Factory.StartNew(() =>
            {
                try { return work(progress, token); }
                catch (Exception ex) { return new FlashResult { Success = false, ErrorMessage = "เกิดข้อผิดพลาด: " + ex.Message }; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .ContinueWith(t => Complete(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnProgress(FlashProgress p)
    {
        if (!IsRunning) return;
        ElapsedText = Core.Engine.Format.Duration(_clock.Elapsed);
        NoteText = p.Message;
        switch (p.Phase)
        {
            case FlashPhase.Preparing:
                PhaseText = _isBackup ? "กำลังเตรียมอ่านการ์ด…" : "กำลังเตรียมการ์ด…";
                IsIndeterminate = true;
                return;
            case FlashPhase.Finishing:
                PhaseText = "กำลังบันทึกข้อมูลที่ค้างอยู่…";
                IsIndeterminate = true;
                return;
            case FlashPhase.Writing:
                PhaseText = _isBackup ? "กำลังอ่านการ์ดเป็นไฟล์…"
                    : p.BytesSkipped > 0 ? "กำลังเขียน (ข้ามส่วนที่เหมือนเดิม)…" : "กำลังเขียนลงการ์ด…";
                break;
            case FlashPhase.Verifying:
                PhaseText = "กำลังตรวจสอบข้อมูล…";
                break;
            default:
                return;
        }

        IsIndeterminate = false;
        Fraction = p.Fraction;
        Percent = p.Fraction * 100;
        var parts = new List<string> { $"{Percent:0.0}%" };
        if (p.BytesPerSecond > 0) parts.Add(Core.Engine.Format.Speed(p.BytesPerSecond));
        if (p.Remaining is { } r) parts.Add("เหลือ " + Core.Engine.Format.DurationThai(r));
        if (p.Phase == FlashPhase.Writing && p.BytesSkipped > 0) parts.Add("ข้ามได้ " + Core.Engine.Format.Bytes(p.BytesSkipped));
        StatsText = string.Join("  ·  ", parts);
    }

    private void Complete(FlashResult r)
    {
        Result = r;
        _clock.Stop();
        IsRunning = false;
        IsIndeterminate = false;
        NoteText = null;
        ElapsedText = Core.Engine.Format.Duration(_clock.Elapsed);

        if (r.Success)
        {
            Status = StatusKind.Ok;
            Percent = 100;
            Fraction = 1;
            PhaseText = _isBackup ? "สำรองเสร็จแล้ว" : "เสร็จแล้ว — ถอดการ์ดได้เลย";
            var parts = new List<string>
            {
                $"{Core.Engine.Format.Bytes(r.TotalBytes)} ใน {ElapsedText}",
                "เฉลี่ย " + Core.Engine.Format.Speed(r.WriteBytesPerSecond),
            };
            if (r.BytesSkipped > 0) parts.Add($"เขียนจริง {Core.Engine.Format.Bytes(r.BytesWritten)} (ข้าม {Core.Engine.Format.Bytes(r.BytesSkipped)})");
            if (r.BytesVerified > 0) parts.Add("ตรวจสอบผ่าน ✓");
            ResultText = string.Join("  ·  ", parts);
            StatsText = "";
        }
        else if (r.Cancelled)
        {
            StatsText = Percent > 0 ? $"หยุดที่ {Percent:0.0}%" : "";
            Status = StatusKind.Warning;
            PhaseText = "ยกเลิกแล้ว";
            ErrorText = _isBackup ? "ไม่ได้บันทึกไฟล์" : "การ์ดใบนี้ยังใช้งานไม่ได้ ต้องเขียนใหม่ให้เสร็จก่อนนำไปใส่เครื่องชั่ง";
        }
        else
        {
            StatsText = Percent > 0 ? $"หยุดที่ {Percent:0.0}%" : "";
            Status = StatusKind.Error;
            PhaseText = "ไม่สำเร็จ";
            ErrorText = r.ErrorMessage;
        }
        Finished?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        _cts.Cancel();
        PhaseText = "กำลังยกเลิก…";
    }

    private bool CanRemove() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => RemoveRequested?.Invoke(this);

    public void CancelSilently() => _cts.Cancel();
}
