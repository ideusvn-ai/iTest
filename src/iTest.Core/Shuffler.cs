namespace ITest.Core;

/// <summary>Kết quả trộn cho một mã đề: thứ tự câu và hoán vị phương án.</summary>
public sealed class CodeLayout
{
    public string Code { get; set; } = "";
    /// <summary>Id câu gốc theo thứ tự hiển thị.</summary>
    public List<int> Order { get; set; } = new();
    /// <summary>Id câu gốc → hoán vị: vị trí hiển thị k chứa phương án gốc Perm[k].</summary>
    public Dictionary<int, int[]> Perm { get; set; } = new();
}

public static class Shuffler
{
    /// <summary>
    /// Tạo bố cục cho tất cả mã đề. Chỉ trộn khi số mã đề lớn hơn 2.
    /// Câu trắc nghiệm chỉ trộn với nhau, câu tự luận chỉ trộn với nhau; Phần I luôn đứng trước Phần II.
    /// </summary>
    public static List<CodeLayout> Build(ExamSet exam, int seedBase = 2026)
    {
        var result = new List<CodeLayout>();
        bool shuffle = exam.Codes > 2;
        for (int c = 0; c < Math.Max(1, exam.Codes); c++)
        {
            var code = (101 + c).ToString();
            var rnd = new Random(seedBase * 31 + c * 7919);
            var mc = exam.McQuestions.Select(q => q.Id).ToList();
            var es = exam.EssayQuestions.Select(q => q.Id).ToList();
            if (shuffle && c > 0) { Shuffle(mc, rnd); Shuffle(es, rnd); }
            var layout = new CodeLayout { Code = code, Order = mc.Concat(es).ToList() };
            foreach (var q in exam.McQuestions)
            {
                var p = Enumerable.Range(0, q.Options.Count).ToArray();
                if (shuffle && c > 0) Shuffle(p, rnd);
                layout.Perm[q.Id] = p;
            }
            result.Add(layout);
        }
        return result;
    }

    public static ClientExam ToClient(ExamSet exam, CodeLayout layout, IReadOnlyDictionary<string, byte[]> media, string department, string school)
    {
        var ce = new ClientExam
        {
            Code = layout.Code,
            Department = department,
            School = school,
            ExamName = exam.ExamName,
            Subject = exam.Subject,
            SchoolYear = exam.SchoolYear,
            Minutes = exam.Minutes,
            AllowDocuments = exam.AllowDocuments,
            McCount = exam.McQuestions.Count(),
            EssayCount = exam.EssayQuestions.Count(),
            McTotal = Math.Round(exam.McQuestions.Sum(q => q.Points), 2),
            EssayTotal = Math.Round(exam.EssayQuestions.Sum(q => q.Points), 2)
        };
        var byId = exam.Questions.ToDictionary(q => q.Id);
        int no = 1;
        foreach (var id in layout.Order)
        {
            var q = byId[id];
            var cq = new ClientQuestion
            {
                No = no++,
                SourceId = q.Id,
                Type = q.Type,
                Text = q.Text,
                Points = q.Points,
                AudioMaxPlays = q.AudioMaxPlays
            };
            if (q.Type == QuestionType.MultipleChoice)
                cq.Options = layout.Perm[q.Id].Select(i => q.Options[i]).ToList();
            if (q.Image != null && media.TryGetValue(q.Image, out var img)) cq.ImageBase64 = Convert.ToBase64String(img);
            if (q.Audio != null && media.TryGetValue(q.Audio, out var aud)) cq.AudioBase64 = Convert.ToBase64String(aud);
            ce.Questions.Add(cq);
        }
        return ce;
    }

    private static void Shuffle<T>(IList<T> list, Random rnd)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>Chấm điểm trắc nghiệm và thống kê đề.</summary>
public static class Scoring
{
    public sealed record McResult(int Right, int Wrong, int Blank, double Score);

    public static McResult GradeMc(ExamSet exam, CodeLayout layout, AnswerSheet sheet)
    {
        var byId = exam.Questions.ToDictionary(q => q.Id);
        int right = 0, wrong = 0, blank = 0; double score = 0;
        for (int i = 0; i < layout.Order.Count; i++)
        {
            var q = byId[layout.Order[i]];
            if (q.Type != QuestionType.MultipleChoice) continue;
            int no = i + 1;
            if (!sheet.Choices.TryGetValue(no, out var shown)) { blank++; continue; }
            int original = layout.Perm[q.Id][shown];
            if (original == q.Correct) { right++; score += q.Points; } else wrong++;
        }
        return new McResult(right, wrong, blank, Math.Round(score, 2));
    }

    /// <summary>Thống kê từng câu: tỉ lệ đúng và số lượt chọn từng phương án gốc.</summary>
    public sealed record ItemStat(int QuestionId, int Answered, int Correct, int[] OptionCounts)
    {
        public double CorrectRate => Answered == 0 ? 0 : (double)Correct / Answered;
    }

    public static List<ItemStat> Analyze(ExamSet exam, IReadOnlyList<CodeLayout> layouts, IEnumerable<AnswerSheet> sheets)
    {
        var stats = exam.McQuestions.ToDictionary(q => q.Id, q => (answered: 0, correct: 0, counts: new int[q.Options.Count]));
        var byCode = layouts.ToDictionary(l => l.Code);
        var byId = exam.Questions.ToDictionary(q => q.Id);
        foreach (var s in sheets.Where(s => !s.Revoked))
        {
            if (!byCode.TryGetValue(s.Code, out var layout)) continue;
            foreach (var (no, shown) in s.Choices)
            {
                if (no < 1 || no > layout.Order.Count) continue;
                var id = layout.Order[no - 1];
                if (!stats.ContainsKey(id)) continue;
                var orig = layout.Perm[id][shown];
                var st = stats[id];
                st.answered++;
                if (orig == byId[id].Correct) st.correct++;
                st.counts[orig]++;
                stats[id] = st;
            }
        }
        return stats.Select(kv => new ItemStat(kv.Key, kv.Value.answered, kv.Value.correct, kv.Value.counts)).ToList();
    }
}
