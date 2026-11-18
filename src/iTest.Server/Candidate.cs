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
    public AnswerSheet Sheet { get; } = new();
    public int Index { get; set; }
    public int ExtraSeconds { get; set; }
    public string? WarnedAt { get; set; }
    public string? RevokedAt { get; set; }
    public bool MoveApproved { get; set; }
    public bool EditRequested { get; set; }
    public int TotalQuestions { get; set; }

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
    public int Answered => Sheet.Choices.Count + Sheet.Essays.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));

    public string StatusText => Status switch
    {
        CandStatus.Connected => "Đã kết nối · chưa đăng ký",
        CandStatus.Waiting => "Chờ phát đề",
        CandStatus.Doing => $"Đang làm bài ({Answered}/{TotalQuestions})",
        CandStatus.Done => $"Đã nộp bài ({Answered}/{TotalQuestions})",
        CandStatus.Lost => $"Mất kết nối ({Answered}/{TotalQuestions})",
        CandStatus.Moving => "Đang chuyển máy",
        CandStatus.Revoked => "Đình chỉ · huỷ KQ",
        _ => ""
    };

    public string Discipline => RevokedAt != null ? "Thu bài " + RevokedAt : WarnedAt != null ? "⚑ Nhắc " + WarnedAt : "—";

    public Brush StatusBrush => new SolidColorBrush(Status switch
    {
        CandStatus.Doing => Color.FromRgb(0x25, 0x63, 0xEB),
        CandStatus.Done => Color.FromRgb(0x16, 0xA3, 0x4A),
        CandStatus.Lost => Color.FromRgb(0xDC, 0x26, 0x26),
        CandStatus.Moving => Color.FromRgb(0x08, 0x91, 0xB2),
        CandStatus.Revoked => Color.FromRgb(0x7C, 0x3A, 0xED),
        _ => Color.FromRgb(0x64, 0x74, 0x8B)
    });

    public Brush RowBrush => new SolidColorBrush(Status switch
    {
        CandStatus.Done => Color.FromRgb(0xF0, 0xFD, 0xF4),
        CandStatus.Lost => Color.FromRgb(0xFE, 0xF2, 0xF2),
        CandStatus.Moving => Color.FromRgb(0xEC, 0xFE, 0xFF),
        CandStatus.Revoked => Color.FromRgb(0xF5, 0xF3, 0xFF),
        _ => Colors.White
    });

    public bool IsActive => Status is CandStatus.Doing or CandStatus.Lost or CandStatus.Moving;
}

/// <summary>Một dòng trong "Hoạt động trực tiếp".</summary>
public sealed record FeedItem(string At, string Tag, string Pc, string Text, Brush TagBg, Brush TagFg);
