namespace ITest.Core;

/// <summary>Kết quả trộn cho một mã đề: thứ tự câu và hoán vị phương án / ý.</summary>
public sealed class CodeLayout
{
    public string Code { get; set; } = "";
    /// <summary>Id câu gốc theo thứ tự hiển thị.</summary>
    public List<int> Order { get; set; } = new();
    /// <summary>Id câu gốc → hoán vị: vị trí hiển thị k chứa phương án (hoặc ý) gốc Perm[k].</summary>
    public Dictionary<int, int[]> Perm { get; set; } = new();

    public int[] PermOf(Question q) =>
        Perm.TryGetValue(q.Id, out var p) && p.Length == q.Options.Count ? p : Enumerable.Range(0, q.Options.Count).ToArray();
}

public static class Shuffler
{
    /// <summary>
    /// Tạo bố cục cho tất cả mã đề. Chỉ trộn khi số mã đề lớn hơn 2; mã đầu tiên (101) giữ nguyên thứ tự gốc.
    /// Câu khách quan chỉ trộn với nhau, câu tự luận chỉ trộn với nhau; Phần I luôn đứng trước Phần II.
    /// Phương án (một/nhiều đáp án) và các ý Đúng/Sai được trộn; câu trả lời ngắn không có phương án.
    /// </summary>
    public static List<CodeLayout> Build(ExamSet exam, int seedBase = 2026)
    {
        var result = new List<CodeLayout>();
        bool shuffle = exam.Codes > 2;
        for (int c = 0; c < Math.Max(1, exam.Codes); c++)
        {
            var code = (101 + c).ToString();
            var rnd = new Random(seedBase * 31 + c * 7919);
            var obj = exam.McQuestions.Select(q => q.Id).ToList();
            var es = exam.EssayQuestions.Select(q => q.Id).ToList();
            bool mix = shuffle && c > 0;
            if (mix) { Shuffle(obj, rnd); Shuffle(es, rnd); }
            var layout = new CodeLayout { Code = code, Order = obj.Concat(es).ToList() };
            foreach (var q in exam.Questions.Where(q => q.Type.HasOptions()))
            {
                var p = Enumerable.Range(0, q.Options.Count).ToArray();
                if (mix && q.Type != QuestionType.YesNo) Shuffle(p, rnd);   // Đúng luôn là A, Sai luôn là B
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
            if (q.Type.HasOptions())
                cq.Options = layout.PermOf(q).Select(i => q.Options[i]).ToList();
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

/// <summary>Chấm điểm phần khách quan và thống kê đề.</summary>
public static class Scoring
{
    public sealed record McResult(int Right, int Wrong, int Blank, double Score);

    /// <summary>
    /// Tỉ lệ điểm câu Đúng/Sai theo số ý trả lời đúng (theo cách tính của đề thi tốt nghiệp THPT từ 2025 với 4 ý):
    /// 1 ý → 10%, 2 ý → 25%, 3 ý → 50%, 4 ý → 100%. Câu có số ý khác 4 được tính theo tỉ lệ ý đúng.
    /// </summary>
    public static double TrueFalseFraction(int correct, int total)
    {
        if (total <= 0) return 0;
        if (total == 4) return correct switch { 1 => 0.1, 2 => 0.25, 3 => 0.5, 4 => 1.0, _ => 0 };
        return (double)correct / total;
    }

    /// <summary>Điểm của một câu khách quan (theo thứ tự hiển thị <paramref name="no"/>) và cờ "đúng hoàn toàn".</summary>
    public static (double score, bool full, bool answered) GradeOne(Question q, CodeLayout layout, AnswerSheet sheet, int no)
    {
        var perm = layout.PermOf(q);
        switch (q.Type)
        {
            case QuestionType.MultipleChoice:
            case QuestionType.YesNo:
                if (!sheet.Choices.TryGetValue(no, out var shown) || shown < 0 || shown >= perm.Length) return (0, false, false);
                return perm[shown] == q.Correct ? (q.Points, true, true) : (0, false, true);

            case QuestionType.MultiSelect:
                if (!sheet.Multi.TryGetValue(no, out var picked) || picked.Count == 0) return (0, false, false);
                var orig = picked.Where(i => i >= 0 && i < perm.Length).Select(i => perm[i]).ToHashSet();
                return orig.SetEquals(q.Corrects) ? (q.Points, true, true) : (0, false, true);

            case QuestionType.TrueFalse:
                if (!sheet.Tf.TryGetValue(no, out var tf) || tf.Count == 0) return (0, false, false);
                int ok = 0;
                foreach (var (idx, val) in tf)
                {
                    if (idx < 0 || idx >= perm.Length) continue;
                    int o = perm[idx];
                    if (o < q.TfKey.Count && q.TfKey[o] == val) ok++;
                }
                var frac = TrueFalseFraction(ok, q.Options.Count);
                return (Math.Round(q.Points * frac, 2), ok == q.Options.Count, true);

            case QuestionType.ShortAnswer:
                if (!sheet.Short.TryGetValue(no, out var ans) || string.IsNullOrWhiteSpace(ans)) return (0, false, false);
                var given = QuestionTypes.NormalizeShort(ans);
                bool hit = q.ShortKey.Split(';').Select(QuestionTypes.NormalizeShort).Any(k => k.Length > 0 && k == given);
                return hit ? (q.Points, true, true) : (0, false, true);

            default:
                return (0, false, false);
        }
    }

    /// <summary>Chấm toàn bộ phần khách quan của một bài làm.</summary>
    public static McResult GradeObjective(ExamSet exam, CodeLayout layout, AnswerSheet sheet)
    {
        var byId = exam.Questions.ToDictionary(q => q.Id);
        int right = 0, wrong = 0, blank = 0; double score = 0;
        for (int i = 0; i < layout.Order.Count; i++)
        {
            var q = byId[layout.Order[i]];
            if (!q.Type.IsObjective()) continue;
            var (s, full, answered) = GradeOne(q, layout, sheet, i + 1);
            score += s;
            if (!answered) blank++;
            else if (full) right++;
            else wrong++;
        }
        return new McResult(right, wrong, blank, Math.Round(score, 2));
    }

    /// <summary>Giữ tên cũ cho tương thích.</summary>
    public static McResult GradeMc(ExamSet exam, CodeLayout layout, AnswerSheet sheet) => GradeObjective(exam, layout, sheet);

    /// <summary>Thống kê từng câu một đáp án: tỉ lệ đúng và số lượt chọn từng phương án gốc.</summary>
    public sealed record ItemStat(int QuestionId, int Answered, int Correct, int[] OptionCounts)
    {
        public double CorrectRate => Answered == 0 ? 0 : (double)Correct / Answered;
    }

    public static List<ItemStat> Analyze(ExamSet exam, IReadOnlyList<CodeLayout> layouts, IEnumerable<AnswerSheet> sheets)
    {
        var mc = exam.Questions.Where(q => q.Type.IsSingleChoice()).ToList();
        var stats = mc.ToDictionary(q => q.Id, q => (answered: 0, correct: 0, counts: new int[q.Options.Count]));
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
                var perm = layout.PermOf(byId[id]);
                if (shown < 0 || shown >= perm.Length) continue;
                var orig = perm[shown];
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
