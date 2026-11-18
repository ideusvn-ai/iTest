using System.Text.Json.Serialization;

namespace ITest.Core;

/// <summary>Loại câu hỏi.</summary>
public enum QuestionType { MultipleChoice = 0, Essay = 1 }

/// <summary>Một câu hỏi trong bộ đề (bản đầy đủ, có đáp án — chỉ nằm trong Editor/Server).</summary>
public sealed class Question
{
    public int Id { get; set; }
    public QuestionType Type { get; set; }
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = new() { "", "", "", "" };
    /// <summary>Chỉ số đáp án đúng (0..3), -1 nếu chưa chọn.</summary>
    public int Correct { get; set; } = -1;
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
        if (Type == QuestionType.MultipleChoice)
        {
            if (Options.Count < 2 || Options.Any(string.IsNullOrWhiteSpace)) m.Add("phương án");
            if (Correct < 0 || Correct >= Options.Count) m.Add("đáp án đúng");
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
    public double McPoints { get; set; }
    public int EssayCount { get; set; }
    public double EssayPoints { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<Question> Questions { get; set; } = new();

    [JsonIgnore] public IEnumerable<Question> McQuestions => Questions.Where(q => q.Type == QuestionType.MultipleChoice);
    [JsonIgnore] public IEnumerable<Question> EssayQuestions => Questions.Where(q => q.Type == QuestionType.Essay);
    [JsonIgnore] public double TotalPoints => Math.Round(Questions.Sum(q => q.Points), 2);
    [JsonIgnore] public bool IsComplete => Questions.Count > 0 && Questions.All(q => q.Missing().Count == 0);

    /// <summary>Tạo khung câu hỏi trống theo thiết lập.</summary>
    public void BuildSkeleton()
    {
        Questions.Clear();
        int id = 1;
        for (int i = 0; i < McCount; i++) Questions.Add(new Question { Id = id++, Type = QuestionType.MultipleChoice, Points = McPoints });
        for (int i = 0; i < EssayCount; i++) Questions.Add(new Question { Id = id++, Type = QuestionType.Essay, Points = EssayPoints, Options = new() });
    }

    /// <summary>Đổi số câu / điểm nhưng giữ các câu đã soạn.</summary>
    public void Resize(int mc, double mcPts, int es, double esPts)
    {
        var oldMc = McQuestions.ToList();
        var oldEs = EssayQuestions.ToList();
        bool mcPtsChanged = Math.Abs(mcPts - McPoints) > 1e-9;
        bool esPtsChanged = Math.Abs(esPts - EssayPoints) > 1e-9;
        var list = new List<Question>();
        for (int i = 0; i < mc; i++)
        {
            var q = i < oldMc.Count ? oldMc[i] : new Question { Type = QuestionType.MultipleChoice, Points = mcPts };
            if (mcPtsChanged) q.Points = mcPts;
            list.Add(q);
        }
        for (int i = 0; i < es; i++)
        {
            var q = i < oldEs.Count ? oldEs[i] : new Question { Type = QuestionType.Essay, Points = esPts, Options = new() };
            if (esPtsChanged) q.Points = esPts;
            list.Add(q);
        }
        for (int i = 0; i < list.Count; i++) list[i].Id = i + 1;
        McCount = mc; McPoints = mcPts; EssayCount = es; EssayPoints = esPts;
        Questions = list;
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
    public int AudioMaxPlays { get; set; } = 2;
}

/// <summary>Đề thi theo một mã đề, gửi cho máy thí sinh.</summary>
public sealed class ClientExam
{
    public string Code { get; set; } = "101";
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
    public DateTime? SubmittedAt { get; set; }
    public bool Revoked { get; set; }
    public int Warnings { get; set; }
}
