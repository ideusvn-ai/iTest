namespace ITest.Core;

/// <summary>Kết quả trộn cho một mã đề: thứ tự câu và hoán vị phương án / ý.</summary>
public sealed class CodeLayout
{
    public string Code { get; set; } = "";
    /// <summary>Id câu gốc theo thứ tự hiển thị.</summary>
    public List<int> Order { get; set; } = new();
    /// <summary>Id câu gốc → hoán vị: vị trí hiển thị k chứa phương án (hoặc ý) gốc Perm[k].</summary>
    public Dictionary<int, int[]> Perm { get; set; } = new();

    /// <summary>Hoán vị phương án của câu; dữ liệu hoán vị thiếu, sai độ dài hoặc không phải một hoán vị thì dùng thứ tự gốc.</summary>
    public int[] PermOf(Question q)
    {
        int n = q.Options.Count;
        if (Perm.TryGetValue(q.Id, out var p) && p != null && p.Length == n && IsPermutation(p)) return p;
        return Enumerable.Range(0, n).ToArray();
    }

    private static bool IsPermutation(int[] p)
    {
        var seen = new bool[p.Length];
        foreach (var v in p)
        {
            if (v < 0 || v >= p.Length || seen[v]) return false;
            seen[v] = true;
        }
        return true;
    }
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
            if (q.Audio != null && media.TryGetValue(q.Audio, out var aud))
            {
                cq.AudioBase64 = Convert.ToBase64String(aud);
                var ext = System.IO.Path.GetExtension(q.Audio).ToLowerInvariant();
                cq.AudioExt = ext is ".mp3" or ".wav" or ".m4a" ? ext : ".mp3";
            }
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
                if (q.Correct < 0 || q.Correct >= perm.Length) return (0, false, true);   // đề chưa có đáp án hợp lệ: không ai được điểm câu này
                return perm[shown] == q.Correct ? (q.Points, true, true) : (0, false, true);

            case QuestionType.MultiSelect:
                if (!sheet.Multi.TryGetValue(no, out var picked) || picked == null || picked.Count == 0) return (0, false, false);
                // Có phương án ngoài phạm vi: bài làm sai định dạng, không được tính điểm (không lặng lẽ bỏ qua phần sai).
                if (picked.Any(i => i < 0 || i >= perm.Length)) return (0, false, true);
                if (q.Corrects.Count == 0) return (0, false, true);
                var orig = picked.Select(i => perm[i]).ToHashSet();
                return orig.SetEquals(q.Corrects) ? (q.Points, true, true) : (0, false, true);

            case QuestionType.TrueFalse:
                if (!sheet.Tf.TryGetValue(no, out var tf) || tf == null || tf.Count == 0) return (0, false, false);
                // Chỉ chấm khi đáp án đủ cho mọi ý; ý ngoài phạm vi bị bỏ qua và không bao giờ được tính là đúng.
                int total = q.Options.Count;
                if (total == 0 || q.TfKey.Count != total) return (0, false, true);
                int ok = 0, valid = 0;
                foreach (var (idx, val) in tf)
                {
                    if (idx < 0 || idx >= perm.Length) continue;
                    valid++;
                    if (q.TfKey[perm[idx]] == val) ok++;
                }
                if (valid == 0) return (0, false, false);
                var frac = TrueFalseFraction(ok, total);
                return (Math.Round(q.Points * frac, 2), ok == total, true);

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
        var byId = new Dictionary<int, Question>();
        foreach (var x in exam.Questions) byId.TryAdd(x.Id, x);
        int right = 0, wrong = 0, blank = 0; double score = 0;
        for (int i = 0; i < layout.Order.Count; i++)
        {
            if (!byId.TryGetValue(layout.Order[i], out var q) || !q.Type.IsObjective()) continue;
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

    /// <summary>
    /// Thống kê một câu khách quan. <c>Correct</c> = số bài trả lời đúng hoàn toàn.
    /// <c>OptionCounts</c> (theo thứ tự phương án gốc): với câu một/nhiều đáp án là số lượt chọn từng phương án;
    /// với câu Đúng/Sai nhiều ý là số bài trả lời đúng từng ý. <c>TopWrong</c>: câu trả lời sai gặp nhiều nhất (câu trả lời ngắn).
    /// </summary>
    public sealed record ItemStat(int QuestionId, int Answered, int Correct, int[] OptionCounts, string TopWrong = "")
    {
        public double CorrectRate => Answered == 0 ? 0 : (double)Correct / Answered;
    }

    /// <summary>Đáp án của câu theo thứ tự gốc (đề 101), dạng chữ để in trong bảng phân tích và đáp án.</summary>
    public static string KeyText(Question q) => q.Type switch
    {
        QuestionType.MultipleChoice or QuestionType.YesNo => q.Correct >= 0 ? ((char)('A' + q.Correct)).ToString() : "",
        QuestionType.MultiSelect => string.Join(", ", q.Corrects.OrderBy(i => i).Select(i => ((char)('A' + i)).ToString())),
        QuestionType.TrueFalse => string.Join(" ", q.TfKey.Select((k, i) => $"{(char)('a' + i)}:{(k == true ? "Đ" : k == false ? "S" : "?")}")),
        QuestionType.ShortAnswer => q.ShortKey,
        _ => ""
    };

    /// <summary>Phân tích mọi câu khách quan từ các bài làm (bỏ bài bị huỷ).</summary>
    public static List<ItemStat> Analyze(ExamSet exam, IReadOnlyList<CodeLayout> layouts, IEnumerable<AnswerSheet> sheets)
    {
        var objective = exam.Questions.Where(q => q.Type.IsObjective()).ToList();
        var answered = objective.ToDictionary(q => q.Id, _ => 0);
        var correct = objective.ToDictionary(q => q.Id, _ => 0);
        var counts = objective.ToDictionary(q => q.Id, q => new int[q.Type.HasOptions() ? q.Options.Count : 0]);
        var wrongTexts = objective.Where(q => q.Type == QuestionType.ShortAnswer).ToDictionary(q => q.Id, _ => new Dictionary<string, int>());
        var byCode = layouts.ToDictionary(l => l.Code);
        var byId = exam.Questions.ToDictionary(q => q.Id);
        // Chỉ thống kê bài đã chốt (đã nộp, không bị huỷ) để bài bỏ dở không làm lệch tỉ lệ.
        foreach (var s in sheets.Where(s => !s.Revoked))
        {
            if (!byCode.TryGetValue(s.Code, out var layout)) continue;
            for (int no = 1; no <= layout.Order.Count; no++)
            {
                if (!byId.TryGetValue(layout.Order[no - 1], out var q) || !q.Type.IsObjective()) continue;
                var (_, full, did) = GradeOne(q, layout, s, no);
                if (!did) continue;
                answered[q.Id]++;
                if (full) correct[q.Id]++;
                var perm = layout.PermOf(q);
                var cnt = counts[q.Id];
                switch (q.Type)
                {
                    case QuestionType.MultipleChoice:
                    case QuestionType.YesNo:
                        if (s.Choices.TryGetValue(no, out var shown) && shown >= 0 && shown < perm.Length && perm[shown] < cnt.Length) cnt[perm[shown]]++;
                        break;
                    case QuestionType.MultiSelect:
                        if (s.Multi.TryGetValue(no, out var picked))
                            foreach (var k in picked.Distinct())
                                if (k >= 0 && k < perm.Length && perm[k] < cnt.Length) cnt[perm[k]]++;
                        break;
                    case QuestionType.TrueFalse:
                        if (s.Tf.TryGetValue(no, out var tf))
                            foreach (var (k, val) in tf)
                                if (k >= 0 && k < perm.Length && perm[k] < cnt.Length && perm[k] < q.TfKey.Count && q.TfKey[perm[k]] == val) cnt[perm[k]]++;
                        break;
                    case QuestionType.ShortAnswer:
                        if (!full && s.Short.TryGetValue(no, out var ans))
                        {
                            var key = ans.Trim();
                            if (key.Length > 40) key = key[..40] + "…";
                            var d = wrongTexts[q.Id];
                            d[key] = d.GetValueOrDefault(key) + 1;
                        }
                        break;
                }
            }
        }
        return objective.Select(q =>
        {
            string top = "";
            if (wrongTexts.TryGetValue(q.Id, out var d) && d.Count > 0)
            {
                var best = d.OrderByDescending(kv => kv.Value).First();
                top = $"{best.Key} ({best.Value} lượt)";
            }
            return new ItemStat(q.Id, answered[q.Id], correct[q.Id], counts[q.Id], top);
        }).ToList();
    }
}
