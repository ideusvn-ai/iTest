namespace ITest.Core.Tests;

public class PointsTests
{
    [Theory]
    [InlineData(7, 28)]
    [InlineData(3, 3)]
    [InlineData(10, 3)]
    [InlineData(6, 40)]
    [InlineData(0.5, 7)]
    public void Split_sums_exactly_to_total(double total, int count)
    {
        var pts = ExamSet.Split(total, count);
        Assert.Equal(count, pts.Count);
        Assert.Equal(total, Math.Round(pts.Sum(), 2));
        Assert.True(pts.Max() - pts.Min() <= 0.0100001, "chênh lệch giữa các câu không quá 0,01 điểm");
        Assert.All(pts, p => Assert.Equal(p, Math.Round(p, 2)));
    }

    [Fact]
    public void Split_zero_count_is_empty() => Assert.Empty(ExamSet.Split(5, 0));

    [Fact]
    public void Resize_keeps_existing_questions_and_totals()
    {
        var x = new ExamSet { McCount = 4, McTotal = 6, EssayCount = 2, EssayTotal = 4 };
        x.BuildSkeleton();
        x.Questions[0].Text = "Câu giữ lại";
        x.Resize(6, 6, 1, 4);
        Assert.Equal(6, x.McQuestions.Count());
        Assert.Single(x.EssayQuestions);
        Assert.Equal("Câu giữ lại", x.Questions[0].Text);
        Assert.Equal(10, x.TotalPoints);
    }

    private static ExamSet Skeleton(int mc, double mcTotal, int es, double esTotal)
    {
        var x = new ExamSet { McCount = mc, McTotal = mcTotal, EssayCount = es, EssayTotal = esTotal };
        x.BuildSkeleton();
        for (int i = 0; i < x.Questions.Count; i++) x.Questions[i].Text = "Q" + (i + 1);
        return x;
    }

    [Fact]
    public void Insert_adds_in_same_part_and_keeps_total()
    {
        var x = Skeleton(4, 8, 2, 2);
        var q = x.InsertAfter(x.Questions[1], objective: true);
        Assert.Equal(7, x.Questions.Count);
        Assert.Same(q, x.Questions[2]);
        Assert.Equal(QuestionType.MultipleChoice, q.Type);
        Assert.Equal(5, x.McCount);
        Assert.Equal(Enumerable.Range(1, 7), x.Questions.Select(k => k.Id));
        Assert.Equal(8, Math.Round(x.McQuestions.Sum(k => k.Points), 2));
        Assert.Equal(10, x.TotalPoints);

        var e = x.InsertAfter(null, objective: false);
        Assert.Same(e, x.Questions[^1]);
        Assert.Equal(QuestionType.Essay, e.Type);
        Assert.Equal(3, x.EssayCount);
        Assert.Equal(10, x.TotalPoints);

        var first = x.InsertAfter(null, objective: true);   // cuối phần trắc nghiệm, trước phần tự luận
        Assert.Equal(5, x.Questions.IndexOf(first));
        Assert.True(x.Questions.Take(6).All(k => k.Type.IsObjective()));
    }

    [Fact]
    public void Remove_redistributes_and_never_empties_the_exam()
    {
        var x = Skeleton(3, 9, 1, 1);
        Assert.True(x.Remove(x.Questions[0]));
        Assert.Equal(new[] { "Q2", "Q3", "Q4" }, x.Questions.Select(k => k.Text));
        Assert.Equal(new[] { 1, 2, 3 }, x.Questions.Select(k => k.Id));
        Assert.Equal(9, Math.Round(x.McQuestions.Sum(k => k.Points), 2));
        Assert.True(x.Remove(x.Questions[2]));           // câu tự luận duy nhất
        Assert.Equal(0, x.EssayCount);
        Assert.Equal(0, x.EssayTotal);
        Assert.True(x.Remove(x.Questions[0]));
        Assert.False(x.Remove(x.Questions[0]));          // còn một câu: không xoá
        Assert.Single(x.Questions);
    }

    [Fact]
    public void Move_stays_inside_its_part_and_points_follow_the_question()
    {
        var x = Skeleton(3, 6, 2, 4);
        x.Questions[0].Points = 3; x.Questions[1].Points = 2; x.Questions[2].Points = 1;
        var a = x.Questions[0];
        Assert.True(x.Move(a, +1));
        Assert.Equal(new[] { "Q2", "Q1", "Q3", "Q4", "Q5" }, x.Questions.Select(k => k.Text));
        Assert.Equal(3, a.Points);
        Assert.Equal(2, a.Id);
        Assert.False(x.Move(x.Questions[0], -1));        // đã ở đầu
        Assert.False(x.Move(x.Questions[2], +1));        // không vượt sang phần tự luận
        Assert.False(x.Move(x.Questions[3], -1));        // tự luận không vượt lên phần trắc nghiệm
        Assert.True(x.Move(x.Questions[3], +1));
        Assert.Equal("Q5", x.Questions[3].Text);
    }

    [Fact]
    public void Objective_part_includes_all_new_types()
    {
        var x = new ExamSet();
        x.Questions.Add(new Question { Id = 1, Type = QuestionType.MultipleChoice });
        x.Questions.Add(new Question { Id = 2, Type = QuestionType.TrueFalse });
        x.Questions.Add(new Question { Id = 3, Type = QuestionType.ShortAnswer });
        x.Questions.Add(new Question { Id = 4, Type = QuestionType.MultiSelect });
        x.Questions.Add(new Question { Id = 5, Type = QuestionType.Essay });
        Assert.Equal(4, x.McQuestions.Count());
        Assert.Single(x.EssayQuestions);
    }

    [Fact]
    public void Missing_reports_per_type()
    {
        var tf = new Question { Type = QuestionType.TrueFalse, Text = "x", Points = 1, Options = new() { "a", "b", "c", "d" }, TfKey = new() { true, false, null, true } };
        Assert.Contains("đáp án Đúng/Sai", tf.Missing());
        tf.TfKey[2] = false;
        Assert.Empty(tf.Missing());

        var sa = new Question { Type = QuestionType.ShortAnswer, Text = "x", Points = 1 };
        Assert.Contains("đáp án", sa.Missing());
        sa.ShortKey = "12";
        Assert.Empty(sa.Missing());

        var ms = new Question { Type = QuestionType.MultiSelect, Text = "x", Points = 1, Options = new() { "a", "b", "c", "d" } };
        Assert.Contains("đáp án đúng", ms.Missing());
        ms.Corrects = new() { 0, 2 };
        Assert.Empty(ms.Missing());
    }
}
