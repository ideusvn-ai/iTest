namespace ITest.Core;

/// <summary>
/// Kiểm tra câu trả lời nhận từ máy thí sinh trước khi ghi vào bài làm: đúng số câu của mã đề, đúng loại câu,
/// chỉ số phương án trong phạm vi và độ dài văn bản có giới hạn. Dữ liệu sai bị từ chối, không làm hỏng bài làm đã lưu.
/// </summary>
public static class AnswerRules
{
    public const int MaxEssayChars = 200_000;
    public const int MaxShortChars = 500;

    /// <summary>Câu gốc ứng với số câu hiển thị <paramref name="no"/> của mã đề; null nếu số câu không có trong đề.</summary>
    public static Question? QuestionAt(ExamSet exam, CodeLayout layout, int no)
    {
        if (no < 1 || no > layout.Order.Count) return null;
        int id = layout.Order[no - 1];
        foreach (var q in exam.Questions) if (q.Id == id) return q;
        return null;
    }

    private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];

    public static bool SetChoice(ExamSet exam, CodeLayout layout, AnswerSheet sheet, int no, int choice)
    {
        var q = QuestionAt(exam, layout, no);
        if (q == null || !q.Type.IsSingleChoice() || choice < 0 || choice >= q.Options.Count) return false;
        sheet.Choices[no] = choice;
        return true;
    }

    public static bool SetMulti(ExamSet exam, CodeLayout layout, AnswerSheet sheet, int no, IEnumerable<int>? choices)
    {
        var q = QuestionAt(exam, layout, no);
        if (q == null || q.Type != QuestionType.MultiSelect) return false;
        var list = (choices ?? Enumerable.Empty<int>()).Take(64).Distinct().OrderBy(i => i).ToList();
        if (list.Any(i => i < 0 || i >= q.Options.Count)) return false;
        sheet.Multi[no] = list;
        return true;
    }

    public static bool SetTf(ExamSet exam, CodeLayout layout, AnswerSheet sheet, int no, IDictionary<int, bool>? tf)
    {
        var q = QuestionAt(exam, layout, no);
        if (q == null || q.Type != QuestionType.TrueFalse) return false;
        var d = new Dictionary<int, bool>();
        if (tf != null)
        {
            if (tf.Count > 64) return false;
            foreach (var (k, v) in tf)
            {
                if (k < 0 || k >= q.Options.Count) return false;
                d[k] = v;
            }
        }
        sheet.Tf[no] = d;
        return true;
    }

    public static bool SetShort(ExamSet exam, CodeLayout layout, AnswerSheet sheet, int no, string? text)
    {
        var q = QuestionAt(exam, layout, no);
        if (q == null || q.Type != QuestionType.ShortAnswer) return false;
        sheet.Short[no] = Clip(text, MaxShortChars);
        return true;
    }

    public static bool SetEssay(ExamSet exam, CodeLayout layout, AnswerSheet sheet, int no, string? text)
    {
        var q = QuestionAt(exam, layout, no);
        if (q == null || q.Type != QuestionType.Essay) return false;
        sheet.Essays[no] = Clip(text, MaxEssayChars);
        return true;
    }

    /// <summary>
    /// Ghi bản bài làm cuối cùng gửi kèm lúc nộp. Từng câu được kiểm tra như khi gửi lẻ; câu hợp lệ thay cho bản đang lưu,
    /// câu không có trong bản cuối thì giữ nguyên bản máy chủ đang có. Trả về số mục bị từ chối.
    /// </summary>
    public static int ApplyFinal(ExamSet exam, CodeLayout layout, AnswerSheet sheet, FinalAnswers? final)
    {
        if (final == null) return 0;
        int bad = 0;
        int limit = Math.Max(1, layout.Order.Count);
        // Số mục vượt số câu của đề là dữ liệu bừa: chỉ xét tối đa số câu của đề cho mỗi nhóm.
        foreach (var (no, v) in (final.Choices ?? new()).Take(limit)) if (!SetChoice(exam, layout, sheet, no, v)) bad++;
        foreach (var (no, v) in (final.Multi ?? new()).Take(limit)) if (!SetMulti(exam, layout, sheet, no, v)) bad++;
        foreach (var (no, v) in (final.Tf ?? new()).Take(limit)) if (!SetTf(exam, layout, sheet, no, v)) bad++;
        foreach (var (no, v) in (final.ShortAns ?? new()).Take(limit)) if (!SetShort(exam, layout, sheet, no, v)) bad++;
        foreach (var (no, v) in (final.Essays ?? new()).Take(limit)) if (!SetEssay(exam, layout, sheet, no, v)) bad++;
        return bad;
    }
}

/// <summary>Các quy định của phiên thi mà máy chủ tự kiểm tra, không tin vào máy thí sinh.</summary>
public static class SessionRules
{
    /// <summary>Sai lệch đồng hồ cho phép giữa máy thí sinh và máy chủ (giây).</summary>
    public const int ClockSlackSeconds = 5;

    /// <summary>
    /// Thí sinh chỉ được nộp khi còn không quá <paramref name="windowMinutes"/> phút (theo đồng hồ máy chủ),
    /// trừ khi chính máy chủ đang thu bài (<paramref name="collecting"/>).
    /// </summary>
    public static bool CanSubmit(int secondsLeft, int windowMinutes, bool collecting) =>
        collecting || secondsLeft <= Math.Max(0, windowMinutes) * 60 + ClockSlackSeconds;

    /// <summary>
    /// Một kết nối mới có được gắn vào số báo danh đã có trong phòng không.
    /// Chưa phát đề cho thí sinh đó: được nếu phiên cũ đã rời phòng, hoặc đúng token.
    /// Đã có bài làm (đang làm, mất kết nối, đã nộp…): bắt buộc đúng token phiên, hoặc giám thị đã duyệt chuyển máy.
    /// </summary>
    public static bool MayAttach(bool started, bool tokenOk, bool moveApproved, bool oldConnectionAlive) =>
        started ? tokenOk || moveApproved : tokenOk || !oldConnectionAlive;

    public static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
}
