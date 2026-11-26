using QuizRoom.Contracts;
using Xunit;

namespace QuizRoom.Tests;

/// <summary>Тесты подсчёта очков: очки = round(1000 * (1 - 0.5 * t / T)).</summary>
public class ScoringTests
{
    [Fact]
    public void Fast_answer_gives_max_points() =>
        Assert.Equal(1000, Scoring.Calculate(true, 0, 20));

    [Fact]
    public void Example_from_spec_gives_875() =>
        // Пример из ТЗ: T = 20 с, ответ за 5 с
        Assert.Equal(875, Scoring.Calculate(true, 5, 20));

    [Fact]
    public void Slow_answer_gives_about_half() =>
        Assert.Equal(550, Scoring.Calculate(true, 18, 20));

    [Fact]
    public void Boundary_t_equals_T_gives_500() =>
        Assert.Equal(500, Scoring.Calculate(true, 20, 20));

    [Fact]
    public void After_time_limit_gives_zero() =>
        Assert.Equal(0, Scoring.Calculate(true, 20.01, 20));

    [Fact]
    public void Wrong_answer_gives_zero() =>
        Assert.Equal(0, Scoring.Calculate(false, 1, 20));

    [Fact]
    public void No_answer_gives_zero() =>
        // Отсутствие ответа считается неверным
        Assert.Equal(0, Scoring.Calculate(false, 0, 20));

    [Fact]
    public void Result_is_rounded_to_integer() =>
        // 1000 * (1 - 0.5 / 3) = 833.33
        Assert.Equal(833, Scoring.Calculate(true, 1, 3));
}