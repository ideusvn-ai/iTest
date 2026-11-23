namespace ITest.Core.Tests;

public class ShuffleScoringTests
{
    private static ExamSet Sample(int codes)
    {
        var x = new ExamSet { Codes = codes, Minutes = 45, ExamName = "Thử", Subject = "Toán" };
        int id = 1;
        for (int i = 0; i < 8; i++)
            x.Questions.Add(new Question { Id = id++, Type = QuestionType.MultipleChoice, Text = $"MC {i}", Options = new() { "A0", "A1", "A2", "A3" }, Correct = i % 4, Points = 0.25 });
        x.Questions.Add(new Question { Id = id++, Type = QuestionType.MultiSelect, Text = "MS", Options = new() { "a", "b", "c", "d" }, Corrects = new() { 1, 3 }, Points = 1 });
        x.Questions.Add(new Question { Id = id++, Type = QuestionType.TrueFalse, Text = "TF", Options = new() { "s1", "s2", "s3", "s4" }, TfKey = new() { true, false, true, false }, Points = 1 });
        x.Questions.Add(new Question { Id = id++, Type = QuestionType.ShortAnswer, Text = "SA", ShortKey = "0,75; 3/4", Points = 0.5 });
        x.Questions.Add(new Question { Id = id++, Type = QuestionType.Essay, Text = "E1", Points = 2 });
        x.Questions.Add(new Question { Id = id++, Type = QuestionType.Essay, Text = "E2", Points = 3.5 });
        return x;
    }

    /// <summary>Tạo bài làm đúng hoàn toàn theo bố cục của mã đề.</summary>
    private static AnswerSheet Perfect(ExamSet x, CodeLayout l)
    {
        var s = new AnswerSheet { Code = l.Code };
        var byId = x.Questions.ToDictionary(q => q.Id);
        for (int i = 0; i < l.Order.Count; i++)
        {
            var q = byId[l.Order[i]]; int no = i + 1; var p = l.PermOf(q);
            switch (q.Type)
            {
                case QuestionType.MultipleChoice: s.Choices[no] = Array.IndexOf(p, q.Correct); break;
                case QuestionType.MultiSelect: s.Multi[no] = Enumerable.Range(0, p.Length).Where(k => q.Corrects.Contains(p[k])).ToList(); break;
                case QuestionType.TrueFalse: s.Tf[no] = Enumerable.Range(0, p.Length).ToDictionary(k => k, k => q.TfKey[p[k]] == true); break;
                case QuestionType.ShortAnswer: s.Short[no] = " 3/4 "; break;
            }
        }
        return s;
    }

    [Fact]
    public void Two_codes_or_less_are_not_shuffled()
    {
        var x = Sample(2);
        var ls = Shuffler.Build(x);
        Assert.Equal(2, ls.Count);
        foreach (var l in ls)
        {
            Assert.Equal(x.Questions.Select(q => q.Id), l.Order);
            Assert.All(l.Perm.Values, p => Assert.Equal(new[] { 0, 1, 2, 3 }, p));
        }
    }

    [Fact]
    public void Shuffled_codes_keep_parts_and_permutations_valid()
    {
        var x = Sample(4);
        var ls = Shuffler.Build(x);
        Assert.Equal(new[] { "101", "102", "103", "104" }, ls.Select(l => l.Code));
        Assert.Equal(x.Questions.Select(q => q.Id), ls[0].Order);   // mã 101 giữ đề gốc
        var obj = x.McQuestions.Select(q => q.Id).ToHashSet();
        foreach (var l in ls)
        {
            Assert.Equal(x.Questions.Count, l.Order.Distinct().Count());
            // mọi câu khách quan đứng trước câu tự luận
            Assert.All(l.Order.Take(obj.Count), id => Assert.Contains(id, obj));
            Assert.All(l.Perm.Values, p => Assert.Equal(new[] { 0, 1, 2, 3 }, p.OrderBy(v => v)));
        }
        Assert.Contains(ls.Skip(1), l => !l.Order.SequenceEqual(ls[0].Order));
    }

    [Fact]
    public void Shuffle_is_deterministic()
    {
        var a = Shuffler.Build(Sample(5));
        var b = Shuffler.Build(Sample(5));
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Order, b[i].Order);
            foreach (var (k, v) in a[i].Perm) Assert.Equal(v, b[i].Perm[k]);
        }
    }

    [Fact]
    public void Perfect_sheet_gets_full_objective_score_for_every_code()
    {
        var x = Sample(4);
        double objTotal = x.McQuestions.Sum(q => q.Points);
        foreach (var l in Shuffler.Build(x))
        {
            var r = Scoring.GradeObjective(x, l, Perfect(x, l));
            Assert.Equal(objTotal, r.Score, 2);
            Assert.Equal(x.McQuestions.Count(), r.Right);
            Assert.Equal(0, r.Wrong);
        }
    }

    [Fact]
    public void Empty_sheet_scores_zero_and_counts_blank()
    {
        var x = Sample(3);
        var l = Shuffler.Build(x)[1];
        var r = Scoring.GradeObjective(x, l, new AnswerSheet { Code = l.Code });
        Assert.Equal(0, r.Score);
        Assert.Equal(x.McQuestions.Count(), r.Blank);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(1, 0.1)]
    [InlineData(2, 0.25)]
    [InlineData(3, 0.5)]
    [InlineData(4, 1.0)]
    public void True_false_partial_credit(int correct, double fraction)
    {
        var x = new ExamSet { Codes = 1 };
        var q = new Question { Id = 1, Type = QuestionType.TrueFalse, Options = new() { "a", "b", "c", "d" }, TfKey = new() { true, true, false, false }, Points = 1 };
        x.Questions.Add(q);
        var l = Shuffler.Build(x)[0];
        var s = new AnswerSheet { Code = l.Code };
        s.Tf[1] = new Dictionary<int, bool>();
        for (int k = 0; k < 4; k++) s.Tf[1][k] = k < correct ? q.TfKey[k] == true : q.TfKey[k] != true;
        Assert.Equal(fraction, Scoring.GradeObjective(x, l, s).Score, 3);
    }

    [Fact]
    public void Multi_select_requires_exact_set()
    {
        var x = new ExamSet { Codes = 1 };
        x.Questions.Add(new Question { Id = 1, Type = QuestionType.MultiSelect, Options = new() { "a", "b", "c", "d" }, Corrects = new() { 0, 2 }, Points = 1 });
        var l = Shuffler.Build(x)[0];
        var s = new AnswerSheet { Code = l.Code };
        s.Multi[1] = new() { 0 };
        Assert.Equal(0, Scoring.GradeObjective(x, l, s).Score);
        s.Multi[1] = new() { 0, 2, 3 };
        Assert.Equal(0, Scoring.GradeObjective(x, l, s).Score);
        s.Multi[1] = new() { 2, 0 };
        Assert.Equal(1, Scoring.GradeObjective(x, l, s).Score);
    }

    [Fact]
    public void Yes_no_is_never_shuffled_and_graded_as_single_choice()
    {
        var x = new ExamSet { Codes = 5 };
        for (int i = 1; i <= 6; i++)
            x.Questions.Add(new Question { Id = i, Type = QuestionType.YesNo, Text = $"Mệnh đề {i}", Options = QuestionTypes.YesNoOptions.ToList(), Correct = i % 2, Points = 0.5 });
        foreach (var l in Shuffler.Build(x))
        {
            Assert.All(l.Perm.Values, p => Assert.Equal(new[] { 0, 1 }, p));
            var ce = Shuffler.ToClient(x, l, new Dictionary<string, byte[]>(), "", "");
            Assert.All(ce.Questions, q => Assert.Equal(new[] { "Đúng", "Sai" }, q.Options));
            var s = new AnswerSheet { Code = l.Code };
            var byId = x.Questions.ToDictionary(q => q.Id);
            for (int i = 0; i < l.Order.Count; i++) s.Choices[i + 1] = byId[l.Order[i]].Correct;
            s.Choices[1] = 1 - s.Choices[1];   // sai một câu
            var r = Scoring.GradeObjective(x, l, s);
            Assert.Equal(5, r.Right);
            Assert.Equal(2.5, r.Score);
        }
    }

    [Fact]
    public void Analysis_covers_every_objective_type_across_shuffled_codes()
    {
        var x = Sample(4);
        var layouts = Shuffler.Build(x);
        // 4 bài đúng hoàn toàn (mỗi mã một bài) + 1 bài bỏ trống + 1 bài bị huỷ (không tính).
        var sheets = layouts.Select(l => Perfect(x, l)).ToList();
        sheets.Add(new AnswerSheet { Code = "101" });
        var revoked = Perfect(x, layouts[0]); revoked.Revoked = true; sheets.Add(revoked);
        var stats = Scoring.Analyze(x, layouts, sheets);
        Assert.Equal(x.McQuestions.Count(), stats.Count);
        foreach (var s in stats)
        {
            var q = x.Questions.First(k => k.Id == s.QuestionId);
            Assert.Equal(4, s.Answered);
            Assert.Equal(4, s.Correct);
            switch (q.Type)
            {
                case QuestionType.MultipleChoice:
                    Assert.Equal(4, s.OptionCounts[q.Correct]);          // quy về phương án gốc dù đã trộn
                    Assert.Equal(4, s.OptionCounts.Sum());
                    break;
                case QuestionType.MultiSelect:
                    Assert.Equal(new[] { 0, 4, 0, 4 }, s.OptionCounts);
                    break;
                case QuestionType.TrueFalse:
                    Assert.Equal(new[] { 4, 4, 4, 4 }, s.OptionCounts);  // số bài đúng từng ý
                    break;
            }
        }
    }

    [Fact]
    public void Analysis_reports_partial_true_false_and_common_wrong_short_answer()
    {
        var x = new ExamSet { Codes = 1 };
        x.Questions.Add(new Question { Id = 1, Type = QuestionType.TrueFalse, Options = new() { "a", "b", "c", "d" }, TfKey = new() { true, true, false, false }, Points = 1 });
        x.Questions.Add(new Question { Id = 2, Type = QuestionType.ShortAnswer, ShortKey = "12", Points = 1 });
        var l = Shuffler.Build(x);
        AnswerSheet Sheet(bool a, string shortAns)
        {
            var s = new AnswerSheet { Code = "101" };
            s.Tf[1] = new() { [0] = a, [1] = true, [2] = false, [3] = false };
            s.Short[2] = shortAns;
            return s;
        }
        var stats = Scoring.Analyze(x, l, new[] { Sheet(true, "12"), Sheet(false, "21"), Sheet(false, " 21 ") });
        var tf = stats.First(s => s.QuestionId == 1);
        Assert.Equal(3, tf.Answered);
        Assert.Equal(1, tf.Correct);
        Assert.Equal(new[] { 1, 3, 3, 3 }, tf.OptionCounts);
        var sa = stats.First(s => s.QuestionId == 2);
        Assert.Equal(1, sa.Correct);
        Assert.StartsWith("21 (2", sa.TopWrong);
        Assert.Equal("a:Đ b:Đ c:S d:S", Scoring.KeyText(x.Questions[0]));
    }

    [Fact]
    public void Client_exam_carries_audio_extension()
    {
        var x = new ExamSet { Codes = 1 };
        x.Questions.Add(new Question { Id = 1, Type = QuestionType.Essay, Text = "Nghe và trả lời", Points = 1, Audio = "abc.WAV", Options = new() });
        var l = Shuffler.Build(x)[0];
        var ce = Shuffler.ToClient(x, l, new Dictionary<string, byte[]> { ["abc.WAV"] = new byte[] { 1, 2, 3 } }, "", "");
        Assert.Equal(".wav", ce.Questions[0].AudioExt);
        Assert.NotNull(ce.Questions[0].AudioBase64);
    }

    [Fact]
    public void Yes_no_missing_answer_is_reported()
    {
        var q = new Question { Type = QuestionType.YesNo, Text = "x", Points = 1, Options = QuestionTypes.YesNoOptions.ToList() };
        Assert.Contains("đáp án Đúng/Sai", q.Missing());
        q.Correct = 1;
        Assert.Empty(q.Missing());
    }

    [Theory]
    [InlineData("0,75", true)]
    [InlineData("0.75", true)]
    [InlineData(" 3 / 4", true)]
    [InlineData("0.7", false)]
    [InlineData("", false)]
    public void Short_answer_normalized_match(string answer, bool ok)
    {
        var x = new ExamSet { Codes = 1 };
        x.Questions.Add(new Question { Id = 1, Type = QuestionType.ShortAnswer, ShortKey = "0,75; 3/4", Points = 0.5 });
        var l = Shuffler.Build(x)[0];
        var s = new AnswerSheet { Code = l.Code };
        s.Short[1] = answer;
        Assert.Equal(ok ? 0.5 : 0, Scoring.GradeObjective(x, l, s).Score);
    }

    [Fact]
    public void Client_exam_has_no_answer_key_and_uses_shuffled_options()
    {
        var x = Sample(4);
        var l = Shuffler.Build(x)[2];
        var ce = Shuffler.ToClient(x, l, new Dictionary<string, byte[]>(), "Sở", "Trường");
        var json = System.Text.Json.JsonSerializer.Serialize(ce);
        Assert.DoesNotContain("Correct", json);
        Assert.DoesNotContain("TfKey", json);
        Assert.DoesNotContain("ShortKey", json);
        Assert.DoesNotContain("3/4", json);
        var byId = x.Questions.ToDictionary(q => q.Id);
        foreach (var cq in ce.Questions.Where(q => q.Type.HasOptions()))
        {
            var src = byId[cq.SourceId];
            Assert.Equal(l.PermOf(src).Select(i => src.Options[i]), cq.Options);
        }
    }
}
