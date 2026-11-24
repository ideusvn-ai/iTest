using System.Text.Json.Serialization;

namespace ITest.Core;

/// <summary>Loại câu hỏi.</summary>
public enum QuestionType
{
    /// <summary>Trắc nghiệm một đáp án đúng.</summary>
    MultipleChoice = 0,
    /// <summary>Tự luận.</summary>
    Essay = 1,
    /// <summary>Trắc nghiệm nhiều đáp án đúng.</summary>
    MultiSelect = 2,
    /// <summary>Đúng/Sai theo từng ý (a, b, c, d).</summary>
    TrueFalse = 3,
    /// <summary>Trả lời ngắn (điền số hoặc cụm từ).</summary>
    ShortAnswer = 4,
    /// <summary>Trắc nghiệm đúng/sai: một mệnh đề, 2 phương án cố định A. Đúng, B. Sai.</summary>
    YesNo = 5
}

public static class QuestionTypes
{
    public static bool IsObjective(this QuestionType t) => t != QuestionType.Essay;
    public static bool HasOptions(this QuestionType t) => t is QuestionType.MultipleChoice or QuestionType.MultiSelect or QuestionType.TrueFalse or QuestionType.YesNo;
    /// <summary>Loại chọn một phương án (chấm như trắc nghiệm 1 đáp án).</summary>
    public static bool IsSingleChoice(this QuestionType t) => t is QuestionType.MultipleChoice or QuestionType.YesNo;
    public static readonly string[] YesNoOptions = { "Đúng", "Sai" };

    public static string Label(this QuestionType t) => t switch
    {
        QuestionType.MultipleChoice => "Trắc nghiệm 1 đáp án",
        QuestionType.MultiSelect => "Nhiều đáp án đúng",
        QuestionType.TrueFalse => "Đúng/Sai nhiều ý (a, b, c, d)",
        QuestionType.YesNo => "Trắc nghiệm đúng/sai",
        QuestionType.ShortAnswer => "Trả lời ngắn",
        _ => "Tự luận"
    };

    /// <summary>Chuẩn hoá câu trả lời ngắn để so sánh: bỏ khoảng trắng, chữ thường, dấu phẩy thập phân thành dấu chấm.</summary>
    public static string NormalizeShort(string s) =>
        new string((s ?? "").Trim().ToLowerInvariant().Replace(',', '.').Where(c => !char.IsWhiteSpace(c)).ToArray());
}

/// <summary>Một câu hỏi trong bộ đề (bản đầy đủ, có đáp án — chỉ nằm trong Editor/Server).</summary>
public sealed class Question
{
    public int Id { get; set; }
    public QuestionType Type { get; set; }
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = new() { "", "", "", "" };
    /// <summary>Chỉ số đáp án đúng (0..3), -1 nếu chưa chọn — câu một đáp án.</summary>
    public int Correct { get; set; } = -1;
    /// <summary>Các đáp án đúng — câu nhiều đáp án.</summary>
    public List<int> Corrects { get; set; } = new();
    /// <summary>Đáp án từng ý (true = Đúng) — câu Đúng/Sai; null nghĩa là chưa chọn.</summary>
    public List<bool?> TfKey { get; set; } = new();
    /// <summary>Đáp án câu trả lời ngắn; nhiều đáp án chấp nhận được cách nhau bằng dấu ";".</summary>
    public string ShortKey { get; set; } = "";
    public double Points { get; set; }
    /// <summary>Hướng dẫn chấm cho câu tự luận (không gửi cho thí sinh).</summary>
    public string Guide { get; set; } = "";
    public string? Image { get; set; }
    public string? Audio { get; set; }
    public int AudioMaxPlays { get; set; } = 2;

    /// <summary>Danh sách thông tin còn thiếu; rỗng nghĩa là câu đã đủ.</summary>
    public List<string> Missing()
    {
        var m = new List<string>();
        if (string.IsNullOrWhiteSpace(Text)) m.Add("nội dung");
        switch (Type)
        {
            case QuestionType.YesNo:
                if (Correct is not (0 or 1)) m.Add("đáp án Đúng/Sai");
                break;
            case QuestionType.MultipleChoice:
                if (Options.Count < 2 || Options.Any(string.IsNullOrWhiteSpace)) m.Add("phương án");
                if (Correct < 0 || Correct >= Options.Count) m.Add("đáp án đúng");
                break;
            case QuestionType.MultiSelect:
                if (Options.Count < 2 || Options.Any(string.IsNullOrWhiteSpace)) m.Add("phương án");
                if (Corrects.Count == 0 || Corrects.Any(i => i < 0 || i >= Options.Count)) m.Add("đáp án đúng");
                break;
            case QuestionType.TrueFalse:
                if (Options.Count < 2 || Options.Any(string.IsNullOrWhiteSpace)) m.Add("các ý");
                if (TfKey.Count != Options.Count || TfKey.Any(k => k == null)) m.Add("đáp án Đúng/Sai");
                break;
            case QuestionType.ShortAnswer:
                if (string.IsNullOrWhiteSpace(ShortKey)) m.Add("đáp án");
                break;
        }
        if (Points <= 0) m.Add("điểm");
        return m;
    }
}

/// <summary>Bộ đề do iTest-Editor soạn và đóng gói thành file .itz.</summary>
public sealed class ExamSet
{
    public string ExamName { get; set; } = "";      // Kỳ thi
    public string Subject { get; set; } = "";       // Môn thi
    public string SchoolYear { get; set; } = "";    // Năm học
    public string Author { get; set; } = "";        // Người soạn đề
    public string Unit { get; set; } = "";          // Tổ chuyên môn / đơn vị
    public int Minutes { get; set; } = 45;
    public int Codes { get; set; } = 4;
    public bool AllowDocuments { get; set; }
    public int McCount { get; set; }
    /// <summary>Tổng điểm phần trắc nghiệm (người soạn nhập).</summary>
    public double McTotal { get; set; }
    public int EssayCount { get; set; }
    /// <summary>Tổng điểm phần tự luận (người soạn nhập).</summary>
    public double EssayTotal { get; set; }
    /// <summary>Điểm bình quân mỗi câu trắc nghiệm (tham khảo, tự tính).</summary>
    public double McPoints { get; set; }
    public double EssayPoints { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<Question> Questions { get; set; } = new();

    /// <summary>Các câu thuộc phần trắc nghiệm (mọi loại khách quan: một/nhiều đáp án, Đúng/Sai, trả lời ngắn).</summary>
    [JsonIgnore] public IEnumerable<Question> McQuestions => Questions.Where(q => q.Type.IsObjective());
    [JsonIgnore] public IEnumerable<Question> EssayQuestions => Questions.Where(q => q.Type == QuestionType.Essay);
    [JsonIgnore] public double TotalPoints => Math.Round(Questions.Sum(q => q.Points), 2);
    [JsonIgnore] public bool IsComplete => Questions.Count > 0 && Questions.All(q => q.Missing().Count == 0);

    /// <summary>
    /// Chia đều <paramref name="total"/> điểm cho <paramref name="count"/> câu, làm tròn 2 chữ số;
    /// phần dư dồn vào các câu cuối để tổng luôn đúng bằng <paramref name="total"/>.
    /// </summary>
    public static List<double> Split(double total, int count)
    {
        var list = new List<double>();
        if (count <= 0) return list;
        long cents = (long)Math.Round(total * 100);
        long baseCents = cents / count;
        long rem = cents - baseCents * count;
        for (int i = 0; i < count; i++) list.Add((baseCents + (i >= count - rem ? 1 : 0)) / 100.0);
        return list;
    }

    /// <summary>Chia lại điểm cho một phần theo tổng điểm của phần đó.</summary>
    public void Distribute(QuestionType type)
    {
        var qs = Questions.Where(q => q.Type.IsObjective() == type.IsObjective()).ToList();
        var pts = Split(type.IsObjective() ? McTotal : EssayTotal, qs.Count);
        for (int i = 0; i < qs.Count; i++) qs[i].Points = pts[i];
        if (type.IsObjective()) McPoints = qs.Count > 0 ? Math.Round(McTotal / qs.Count, 4) : 0;
        else EssayPoints = qs.Count > 0 ? Math.Round(EssayTotal / qs.Count, 4) : 0;
    }

    /// <summary>Bản nháp cũ chỉ có điểm mỗi câu: suy ra tổng điểm từng phần.</summary>
    public void EnsureTotals()
    {
        if (McTotal <= 0 && McCount > 0) McTotal = Math.Round(McQuestions.Sum(q => q.Points), 2);
        if (EssayTotal <= 0 && EssayCount > 0) EssayTotal = Math.Round(EssayQuestions.Sum(q => q.Points), 2);
    }

    /// <summary>Tạo khung câu hỏi trống theo thiết lập và tự chia điểm.</summary>
    public void BuildSkeleton()
    {
        Questions.Clear();
        int id = 1;
        for (int i = 0; i < McCount; i++) Questions.Add(new Question { Id = id++, Type = QuestionType.MultipleChoice });
        for (int i = 0; i < EssayCount; i++) Questions.Add(new Question { Id = id++, Type = QuestionType.Essay, Options = new() });
        Distribute(QuestionType.MultipleChoice);
        Distribute(QuestionType.Essay);
    }

    private void Renumber()
    {
        for (int i = 0; i < Questions.Count; i++) Questions[i].Id = i + 1;
        McCount = McQuestions.Count();
        EssayCount = EssayQuestions.Count();
    }

    /// <summary>
    /// Thêm một câu trống ngay sau câu <paramref name="after"/> (cùng phần với câu đó); nếu không chỉ định thì thêm vào cuối phần
    /// trắc nghiệm hoặc tự luận. Điểm của phần được chia đều lại theo tổng điểm phần.
    /// </summary>
    public Question InsertAfter(Question? after, bool objective)
    {
        if (after != null) objective = after.Type.IsObjective();
        var q = objective ? new Question { Type = QuestionType.MultipleChoice } : new Question { Type = QuestionType.Essay, Options = new() };
        int at;
        if (after != null && Questions.Contains(after)) at = Questions.IndexOf(after) + 1;
        else at = objective ? McQuestions.Count() : Questions.Count;   // phần trắc nghiệm luôn đứng trước phần tự luận
        Questions.Insert(at, q);
        Renumber();
        Distribute(q.Type);
        return q;
    }

    /// <summary>Xoá một câu (bộ đề phải còn ít nhất một câu). Điểm của phần được chia đều lại.</summary>
    public bool Remove(Question q)
    {
        if (Questions.Count <= 1 || !Questions.Remove(q)) return false;
        Renumber();
        if (q.Type.IsObjective()) { if (McCount == 0) McTotal = 0; else Distribute(QuestionType.MultipleChoice); }
        else { if (EssayCount == 0) EssayTotal = 0; else Distribute(QuestionType.Essay); }
        return true;
    }

    /// <summary>Chuyển một câu lên (<paramref name="delta"/> = -1) hoặc xuống (+1) trong phần của nó; điểm đi theo câu.</summary>
    public bool Move(Question q, int delta)
    {
        int i = Questions.IndexOf(q), j = i + delta;
        if (i < 0 || j < 0 || j >= Questions.Count) return false;
        if (Questions[j].Type.IsObjective() != q.Type.IsObjective()) return false;   // không vượt sang phần khác
        (Questions[i], Questions[j]) = (Questions[j], Questions[i]);
        Renumber();
        return true;
    }

    /// <summary>Đổi số câu / tổng điểm từng phần nhưng giữ các câu đã soạn; điểm từng câu được chia lại nếu phần đó thay đổi.</summary>
    public void Resize(int mc, double mcTotal, int es, double esTotal)
    {
        var oldMc = McQuestions.ToList();
        var oldEs = EssayQuestions.ToList();
        bool mcChanged = mc != oldMc.Count || Math.Abs(mcTotal - McTotal) > 1e-9;
        bool esChanged = es != oldEs.Count || Math.Abs(esTotal - EssayTotal) > 1e-9;
        var list = new List<Question>();
        for (int i = 0; i < mc; i++) list.Add(i < oldMc.Count ? oldMc[i] : new Question { Type = QuestionType.MultipleChoice });
        for (int i = 0; i < es; i++) list.Add(i < oldEs.Count ? oldEs[i] : new Question { Type = QuestionType.Essay, Options = new() });
        for (int i = 0; i < list.Count; i++) list[i].Id = i + 1;
        McCount = mc; McTotal = mcTotal; EssayCount = es; EssayTotal = esTotal;
        Questions = list;
        if (mcChanged) Distribute(QuestionType.MultipleChoice);
        if (esChanged) Distribute(QuestionType.Essay);
    }
}

/// <summary>Câu hỏi gửi cho thí sinh (không có đáp án, phương án đã trộn).</summary>
public sealed class ClientQuestion
{
    public int No { get; set; }                     // Số thứ tự hiển thị trên đề
    public int SourceId { get; set; }               // Id câu gốc
    public QuestionType Type { get; set; }
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public double Points { get; set; }
    public string? ImageBase64 { get; set; }
    public string? AudioBase64 { get; set; }
    /// <summary>Phần mở rộng file âm thanh gốc (.mp3, .wav, .m4a) để máy thí sinh phát đúng định dạng.</summary>
    public string AudioExt { get; set; } = ".mp3";
    public int AudioMaxPlays { get; set; } = 2;
}

/// <summary>Đề thi theo một mã đề, gửi cho máy thí sinh.</summary>
public sealed class ClientExam
{
    public string Code { get; set; } = "101";
    /// <summary>Dấu vân tay bộ đề (do máy chủ gắn): máy thí sinh dùng để không nạp nhầm bản lưu tạm của bài thi khác.</summary>
    public string Fingerprint { get; set; } = "";
    public string Department { get; set; } = "";    // Sở GD&ĐT (từ Server)
    public string School { get; set; } = "";        // Trường (từ Server)
    public string ExamName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string SchoolYear { get; set; } = "";
    public int Minutes { get; set; }
    public bool AllowDocuments { get; set; }
    public int McCount { get; set; }
    public int EssayCount { get; set; }
    public double McTotal { get; set; }
    public double EssayTotal { get; set; }
    public List<ClientQuestion> Questions { get; set; } = new();
}

/// <summary>Thông tin thí sinh tự nhập.</summary>
public sealed class CandidateInfo
{
    public string Sbd { get; set; } = "";
    public string FullName { get; set; } = "";
    public string BirthDate { get; set; } = "";     // dd/mm/yyyy
    public string ClassName { get; set; } = "";
}

/// <summary>Bài làm của một thí sinh (lưu ở Server, sao lưu định kỳ).</summary>
public sealed class AnswerSheet
{
    public CandidateInfo Candidate { get; set; } = new();
    public string Code { get; set; } = "";
    public string Machine { get; set; } = "";
    /// <summary>Số câu hiển thị → chỉ số phương án thí sinh chọn (theo thứ tự đã trộn).</summary>
    public Dictionary<int, int> Choices { get; set; } = new();
    public Dictionary<int, string> Essays { get; set; } = new();
    /// <summary>Câu nhiều đáp án: số câu → các phương án đã chọn (theo thứ tự hiển thị).</summary>
    public Dictionary<int, List<int>> Multi { get; set; } = new();
    /// <summary>Câu Đúng/Sai: số câu → (chỉ số ý hiển thị → true/false).</summary>
    public Dictionary<int, Dictionary<int, bool>> Tf { get; set; } = new();
    /// <summary>Câu trả lời ngắn: số câu → câu trả lời.</summary>
    public Dictionary<int, string> Short { get; set; } = new();

    /// <summary>Số câu đã trả lời (mọi loại).</summary>
    [JsonIgnore] public int AnsweredCount => Choices.Count + Multi.Count(kv => kv.Value.Count > 0) + Tf.Count(kv => kv.Value.Count > 0)
        + Short.Count(kv => !string.IsNullOrWhiteSpace(kv.Value)) + Essays.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));
    public DateTime? SubmittedAt { get; set; }
    public bool Revoked { get; set; }
    public int Warnings { get; set; }
}
