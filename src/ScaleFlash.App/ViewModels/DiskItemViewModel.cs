using CommunityToolkit.Mvvm.ComponentModel;
using ScaleFlash.Core.Devices;
using ScaleFlash.Core.Engine;
using ScaleFlash.Core.Imaging;

namespace ScaleFlash.App.ViewModels;

/// <summary>A card reader slot / removable disk in the target list.</summary>
public partial class DiskItemViewModel : ObservableObject
{
    private ImageInfo? _image;
    private bool _allowFixedUsb;

    public DiskItemViewModel(DiskInfo info) => _info = info;

    [ObservableProperty] private DiskInfo _info;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isBusy;

    public int Number => Info.Number;
    public bool IsUsbStick => !Info.LooksLikeCardReader;
    public string Title => Info.Model;

    public string Details
    {
        get
        {
            var parts = new List<string> { $"ดิสก์ #{Info.Number}" };
            parts.Add(Info.MediaPresent ? Format.Bytes(Info.Size) : "ไม่มีการ์ด");
            if (Info.IsFixedUsb) parts.Add("USB แบบติดตั้งถาวร");
            parts.Add(Info.Bus switch
            {
                BusType.Usb => "USB",
                BusType.Sd => "SD",
                BusType.Mmc => "MMC",
                BusType.FileBackedVirtual or BusType.Virtual => "Virtual",
                _ => Info.Bus.ToString(),
            });
            var letters = Info.DriveLetters.ToList();
            if (letters.Count > 0) parts.Add(string.Join(" ", letters));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>What is on the card now, e.g. "มี: Linux 7.81 GB".</summary>
    public string? LayoutText
    {
        get
        {
            if (!Info.MediaPresent || Info.Layout is null) return null;
            if (Info.Layout.Partitions.Count == 0) return "ข้างใน: ไม่มีพาร์ทิชัน";
            return "ข้างใน: " + string.Join(", ", Info.Layout.Partitions.Select(p => $"{p.TypeName} {Format.Bytes(p.Length)}"));
        }
    }

    /// <summary>The card already carries the same partition layout as the selected image.</summary>
    public bool MatchesImageLayout =>
        _image is not null && Info.Layout is { Partitions.Count: > 0 } l && _image.Layout.Partitions.Count == l.Partitions.Count &&
        l.Partitions.Zip(_image.Layout.Partitions).All(x => x.First.StartOffset == x.Second.StartOffset && x.First.Length == x.Second.Length && x.First.MbrType == x.Second.MbrType);

    public string? HintText => MatchesImageLayout && !IsBusy
        ? "มีระบบเครื่องชั่งอยู่แล้ว (โครงสร้างตรงกับไฟล์) โหมดข้ามส่วนที่เหมือนเดิมจะช่วยให้เร็วขึ้นมาก"
        : null;

    public DiskEligibility Eligibility => Info.GetEligibility(_allowFixedUsb);

    public bool TooSmall => _image is not null && Info.MediaPresent && Info.Size < _image.WriteLength;

    public bool CanSelect => Eligibility == DiskEligibility.Eligible && !IsBusy;

    /// <summary>Reading (backup) only needs a card in the slot; write protection does not matter.</summary>
    public bool CanSelectForRead => Info.MediaPresent && !Info.IsSystemDisk && !IsBusy;

    public string StatusText => IsBusy ? "กำลังทำงาน"
        : Eligibility switch
        {
            DiskEligibility.NoMedia => "ไม่มีการ์ด",
            DiskEligibility.WriteProtected => "การ์ดถูกล็อก",
            DiskEligibility.SystemDisk => "ดิสก์ระบบ",
            DiskEligibility.NotRemovable => "ไม่ใช่การ์ด",
            _ => TooSmall ? "เล็กกว่าไฟล์" : "พร้อม",
        };

    public StatusKind StatusKind => IsBusy ? StatusKind.Busy
        : Eligibility switch
        {
            DiskEligibility.Eligible => TooSmall ? StatusKind.Warning : StatusKind.Ok,
            DiskEligibility.WriteProtected => StatusKind.Error,
            _ => StatusKind.Neutral,
        };

    public void SetContext(ImageInfo? image, bool allowFixedUsb)
    {
        _image = image;
        _allowFixedUsb = allowFixedUsb;
        RefreshAll();
    }

    partial void OnInfoChanged(DiskInfo? oldValue, DiskInfo newValue)
    {
        if (oldValue is not null && (oldValue.Size != newValue.Size || oldValue.Model != newValue.Model))
            IsSelected = false;
        RefreshAll();
    }

    partial void OnIsBusyChanged(bool value) => RefreshAll();

    private void RefreshAll()
    {
        if (!CanSelect && IsSelected && !IsBusy) IsSelected = false;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsUsbStick));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(LayoutText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(Eligibility));
        OnPropertyChanged(nameof(TooSmall));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(CanSelectForRead));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusKind));
    }
}
