using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ITest.Core;

namespace ITest.Server;

public enum CandStatus { Connected, Waiting, Doing, Done, Lost, Moving, Revoked }

/// <summary>Một thí sinh trong phòng thi (dòng trong danh sách).</summary>
public sealed class Candidate : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public Connection? Conn { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;
    public AnswerSheet Sheet { get; } = new();
    public int Index { get; set; }
    public int ExtraSeconds { get; set; }
    public string? WarnedAt { get; set; }
    public string? RevokedAt { get; set; }
    public bool MoveApproved { get; set; }
    public bool EditRequested { get; set; }
    public int TotalQuestions { get; set; }
    /// <summary>Máy chủ đang thu bài của thí sinh này: chờ bản bài làm cuối cùng từ máy thí sinh tới hạn này (giờ UTC).</summary>
    public DateTime? CollectDeadline { get; set; }
    public string CollectText { get; set; } = "";
    /// <summary>Đã ghi nhật ký về dữ liệu bài làm sai định dạng từ máy này (chỉ ghi một lần).</summary>
    public bool BadDataLogged { get; set; }

    private CandStatus _status = CandStatus.Connected;
    public CandStatus Status { get => _status; set { _status = value; RefreshAll(); } }

    private string _machine = "";
    public string Machine { get => _machine; set { _machine = value; Sheet.Machine = value; On(); } }
    private string _ip = "";
    public string Ip { get => _ip; set { _ip = value; On(); } }
    private string _left = "";
    public string Left { get => _left; set { _left = value; On(); } }

    public string Sbd => Sheet.Candidate.Sbd;
    public string FullName => Sheet.Candidate.FullName;
    public string BirthDate => Sheet.Candidate.BirthDate;
    public string ClassName => Sheet.Candidate.ClassName;
    public string Code => Sheet.Code;
    public int Answered => Sheet.AnsweredCount;
    /// <summary>Số dấu hiệu gian lận máy thí sinh đã báo.</summary>
    public int CheatCount { get; set; }
    /// <summary>Token phiên do máy chủ cấp; chỉ máy thí sinh giữ đúng token mới được giành lại phiên khi kết nối lại.</summary>
    public string Token { get; set; } = "";

    public string StatusText => Status switch
    {
        CandStatus.Connected => "Đã kết nối · chưa đăng ký",
        CandStatus.Waiting => "Chờ phát đề",
        CandStatus.Doing => CollectDeadline != null ? $"Đang thu bài ({Answered}/{TotalQuestions})" : $"Đang làm bài ({Answered}/{TotalQuestions})",
        CandStatus.Done => $"Đã nộp bài ({Answered}/{TotalQuestions})",
        CandStatus.Lost => $"Mất kết nối ({Answered}/{TotalQuestions})",
        CandStatus.Moving => "Đang chuyển máy",
        CandStatus.Revoked => "Đình chỉ · huỷ KQ",
        _ => ""
    };

    public string Discipline => (RevokedAt != null ? "Thu bài " + RevokedAt : WarnedAt != null ? "⚑ Nhắc " + WarnedAt : "—")
                                + (CheatCount > 0 ? $"  ⚠{CheatCount}" : "");

    private static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private static readonly Brush SDoing = B(0x25, 0x63, 0xEB), SDone = B(0x16, 0xA3, 0x4A), SLost = B(0xDC, 0x26, 0x26),
        SMoving = B(0x08, 0x91, 0xB2), SRevoked = B(0x7C, 0x3A, 0xED), SIdle = B(0x64, 0x74, 0x8B);
    private static readonly Brush RDone = B(0xF0, 0xFD, 0xF4), RLost = B(0xFE, 0xF2, 0xF2), RMoving = B(0xEC, 0xFE, 0xFF),
        RRevoked = B(0xF5, 0xF3, 0xFF), RWhite = B(0xFF, 0xFF, 0xFF);
    private static readonly Brush PDoing = B(0xDB, 0xEA, 0xFE), PDone = B(0xDC, 0xFC, 0xE7), PLost = B(0xFE, 0xE2, 0xE2),
        PMoving = B(0xCF, 0xFA, 0xFE), PRevoked = B(0xED, 0xE9, 0xFE), PIdle = B(0xF1, 0xF5, 0xF9);

    public Brush StatusBrush => Status switch
    {
        CandStatus.Doing => SDoing, CandStatus.Done => SDone, CandStatus.Lost => SLost,
        CandStatus.Moving => SMoving, CandStatus.Revoked => SRevoked, _ => SIdle
    };

    /// <summary>Nền "viên thuốc" trạng thái.</summary>
    public Brush PillBrush => Status switch
    {
        CandStatus.Doing => PDoing, CandStatus.Done => PDone, CandStatus.Lost => PLost,
        CandStatus.Moving => PMoving, CandStatus.Revoked => PRevoked, _ => PIdle
    };

    public Brush RowBrush => Status switch
    {
        CandStatus.Done => RDone, CandStatus.Lost => RLost, CandStatus.Moving => RMoving, CandStatus.Revoked => RRevoked, _ => RWhite
    };

    /// <summary>Chỉ báo thay đổi tiến độ (nhẹ hơn RefreshAll).</summary>
    public void NotifyProgress() => On(nameof(StatusText));

    public bool IsActive => Status is CandStatus.Doing or CandStatus.Lost or CandStatus.Moving;
}

/// <summary>Một dòng trong "Hoạt động trực tiếp".</summary>
public sealed record FeedItem(string At, string Tag, string Pc, string Text, Brush TagBg, Brush TagFg);

/// <summary>Một thí sinh trong bản dữ liệu xuất kết quả (bản sao, không còn gắn với phòng thi đang chạy).</summary>
public sealed class ExportCand
{
    public AnswerSheet Sheet { get; init; } = new();
    public CandStatus Status { get; init; }
    public string StatusText { get; init; } = "";
    public string Discipline { get; init; } = "—";
    public string? WarnedAt { get; init; }
    public string? RevokedAt { get; init; }
    public string Machine { get; init; } = "";
    public string Sbd => Sheet.Candidate.Sbd;
    public string FullName => Sheet.Candidate.FullName;
    public string BirthDate => Sheet.Candidate.BirthDate;
    public string ClassName => Sheet.Candidate.ClassName;
    public string Code => Sheet.Code;
    /// <summary>Bài đã nộp và không bị huỷ: chỉ những bài này mới có điểm trong các file xuất.</summary>
    public bool HasScore => Status == CandStatus.Done && !Sheet.Revoked;
}

/// <summary>Bản dữ liệu riêng của phòng thi dùng cho việc xuất kết quả ở luồng nền.</summary>
public sealed class ExportData
{
    public ExamSet Exam { get; init; } = new();
    public List<CodeLayout> Layouts { get; init; } = new();
    public string Department { get; init; } = "";
    public string School { get; init; } = "";
    public string Room { get; init; } = "";
    public string Proctor { get; init; } = "";
    public string PackagePath { get; init; } = "";
    public string BackupDir { get; init; } = "";
    public DateTime? StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public List<ExportCand> Candidates { get; init; } = new();
}
