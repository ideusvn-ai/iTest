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
