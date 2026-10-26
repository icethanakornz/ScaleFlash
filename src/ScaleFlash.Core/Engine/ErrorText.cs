using ScaleFlash.Core.IO;

namespace ScaleFlash.Core.Engine;

/// <summary>Turns Win32 errors into short Thai explanations with a suggested fix.</summary>
public static class ErrorText
{
    public static bool IsRetryable(int win32Error) => win32Error switch
    {
        2 or 3 => false,          // device path gone
        19 => false,              // write protected
        21 => false,              // not ready (card removed)
        1112 => false,            // no media
        1167 => false,            // device not connected
        112 => false,             // disk full / past the end
        _ => true,
    };

    public static string ForIo(DeviceIoException ex, bool isSource)
    {
        string where = $" (ตำแหน่ง {Format.Bytes(ex.Offset)})";
        if (isSource)
        {
            return ex.Win32Error switch
            {
                2 or 3 => "ไม่พบไฟล์ต้นฉบับ",
                5 or 32 => "เปิดไฟล์ต้นฉบับไม่ได้ เพราะมีโปรแกรมอื่นใช้อยู่หรือไม่มีสิทธิ์",
                21 or 1112 or 1167 => "อุปกรณ์ต้นทางถูกถอดออกระหว่างอ่าน",
                23 => "อ่านข้อมูลต้นฉบับไม่ได้ (ดิสก์/การ์ดอาจเสีย)" + where,
                _ => $"อ่านข้อมูลต้นฉบับไม่สำเร็จ (รหัส {ex.Win32Error})" + where,
            };
        }

        return ex.Win32Error switch
        {
            5 => "Windows ไม่อนุญาตให้เขียน: ปิดหน้าต่างที่เปิดการ์ดอยู่ แล้วเปิดโปรแกรมแบบ Administrator",
            32 => "มีโปรแกรมอื่นใช้งานการ์ดอยู่ ปิดโปรแกรมนั้นแล้วลองใหม่",
            19 => "การ์ดถูกล็อกป้องกันการเขียน ให้เลื่อนสวิตช์ Lock ข้างการ์ดขึ้น แล้วเสียบใหม่",
            21 or 1112 => "ไม่พบการ์ด: การ์ดอาจถูกถอดออกหรือหลวม",
            1167 or 2 or 3 => "เครื่องอ่านการ์ดถูกถอดออกระหว่างเขียน",
            23 => "เขียนลงการ์ดไม่สำเร็จ: การ์ดอาจเสีย ลองเปลี่ยนการ์ดใบใหม่" + where,
            112 => "การ์ดเต็ม: ความจุไม่พอสำหรับไฟล์ image นี้",
            121 => "เครื่องอ่านการ์ดไม่ตอบสนอง (หมดเวลา) ลองเสียบใหม่หรือเปลี่ยนพอร์ต USB" + where,
            31 or 1117 => "เครื่องอ่านการ์ดมีปัญหา (I/O error) ลองเสียบใหม่หรือเปลี่ยนการ์ด" + where,
            _ => $"เขียนลงการ์ดไม่สำเร็จ (รหัส {ex.Win32Error})" + where,
        };
    }

    public static string ForVerifyMismatch(long offset) =>
        $"ตรวจสอบไม่ผ่าน: ข้อมูลบนการ์ดไม่ตรงกับไฟล์ที่ตำแหน่ง {Format.Bytes(offset)} การ์ดอาจเสียหรือเป็นการ์ดปลอม ควรเปลี่ยนการ์ด";
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        double b = bytes;
        if (b >= 1e9) return $"{b / 1e9:0.00} GB";
        if (b >= 1e6) return $"{b / 1e6:0.0} MB";
        if (b >= 1e3) return $"{b / 1e3:0} KB";
        return $"{bytes} B";
    }

    public static string Speed(double bytesPerSecond) => $"{bytesPerSecond / 1e6:0.0} MB/s";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"x นาที y วินาที" style, used for remaining-time labels.</summary>
    public static string DurationThai(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} วินาที";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} ชม. {t.Minutes} นาที";
        return $"{(int)t.TotalMinutes} นาที {t.Seconds:00} วินาที";
    }
}
