using Microsoft.Extensions.Logging.Abstractions;
using QuizRoom.Contracts;
using QuizRoom.Web.Services;
using Xunit;

namespace QuizRoom.Tests;

/// <summary>Тесты игрового ядра RoomManager (без базы данных).</summary>
public class RoomManagerTests
{
    // Вопрос: вариант 1 правильный, вариант 2 неверный, время 20 с
    private static List<GameQuestion> OneQuestion() => new()
    {
        new GameQuestion(1, "Столица Франции?", 20, new List<GameOption>
        {
            new(1, "Париж", true),
            new(2, "Лион", false)
        })
    };

    private static RoomManager NewManager() =>
        new(null!, NullLogger<RoomManager>.Instance);   // БД в этих тестах не нужна

    private static (RoomManager Rm, string Code) NewRoom()
    {
        var rm = NewManager();
        var code = rm.CreateRoom(1, "host", OneQuestion());
        return (rm, code);
    }

    [Fact]
    public void Room_code_has_six_digits_and_is_unique()
    {
        var rm = NewManager();
        var codes = Enumerable.Range(0, 50).Select(_ => rm.CreateRoom(1, "h", OneQuestion())).ToList();
        Assert.All(codes, c => Assert.Matches("^[0-9]{6}$", c));
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void Players_can_join_in_lobby()
    {
        var (rm, code) = NewRoom();
        Assert.True(rm.JoinRoom(code, "Анна", "c1").Success);
        Assert.True(rm.JoinRoom(code, "Борис", "c2").Success);
    }

    [Fact]
    public void Unknown_room_is_rejected()
    {
        var rm = NewManager();
        Assert.Equal("Комната не найдена", rm.JoinRoom("000000", "Анна", "c1").Error);
    }

    [Fact]
    public void Duplicate_nick_is_rejected_ignoring_case()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        Assert.Equal("Ник занят", rm.JoinRoom(code, "аННа", "c2").Error);
    }

    [Theory]
    [InlineData("А")]
    [InlineData("ААААААААААААААААААААА")]   // 21 символ
    public void Invalid_nick_length_is_rejected(string nick)
    {
        var (rm, code) = NewRoom();
        Assert.Equal("Недопустимая длина ника", rm.JoinRoom(code, nick, "c1").Error);
    }

    [Fact]
    public void Join_after_start_is_rejected()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        Assert.True(rm.StartGame(code, "host"));
        Assert.Equal("Игра уже началась", rm.JoinRoom(code, "Борис", "c2").Error);
    }

    [Fact]
    public void Player_31_is_rejected_as_room_full()
    {
        var (rm, code) = NewRoom();
        for (var i = 1; i <= 30; i++)
            Assert.True(rm.JoinRoom(code, $"Игрок{i}", $"c{i}").Success);
        Assert.Equal("Комната заполнена", rm.JoinRoom(code, "Лишний", "c31").Error);
    }

    [Fact]
    public void Start_without_players_returns_false()
    {
        var (rm, code) = NewRoom();
        Assert.False(rm.StartGame(code, "host"));
    }

    [Fact]
    public void Start_by_not_host_returns_false()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        Assert.False(rm.StartGame(code, "чужой"));
    }

    [Fact]
    public void Rejoin_by_nick_gives_new_connection_and_current_question()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        rm.StartGame(code, "host");
        rm.StartNextQuestion(code);

        var result = rm.RejoinRoom(code, "анна", "c-new");

        Assert.True(result.Success);
        Assert.Equal(RoomStatus.Question, result.Status);
        Assert.NotNull(result.CurrentQuestion);
        Assert.False(result.AlreadyAnswered);
        // Новый connectionId работает: ответ принимается
        Assert.True(rm.SubmitAnswer(code, "c-new", 1, DateTime.UtcNow).Accepted);
    }

    [Fact]
    public void Answer_is_accepted_only_once()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        rm.StartGame(code, "host");
        rm.StartNextQuestion(code);

        Assert.True(rm.SubmitAnswer(code, "c1", 1, DateTime.UtcNow).Accepted);
        Assert.Equal("Ответ уже отправлен", rm.SubmitAnswer(code, "c1", 2, DateTime.UtcNow).Error);
    }

    [Fact]
    public void Answer_after_time_limit_is_rejected()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        rm.StartGame(code, "host");
        rm.StartNextQuestion(code);

        var late = DateTime.UtcNow.AddSeconds(25);   // лимит 20 с
        Assert.Equal("Время вышло", rm.SubmitAnswer(code, "c1", 1, late).Error);
    }

    [Fact]
    public void CloseQuestion_is_idempotent()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        rm.StartGame(code, "host");
        rm.StartNextQuestion(code);

        Assert.NotNull(rm.CloseQuestion(code));
        Assert.Null(rm.CloseQuestion(code));
    }

    [Fact]
    public void CloseQuestion_returns_answers_per_option_and_player_results()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        rm.JoinRoom(code, "Борис", "c2");
        rm.StartGame(code, "host");
        rm.StartNextQuestion(code);
        rm.SubmitAnswer(code, "c1", 1, DateTime.UtcNow);   // верно
        rm.SubmitAnswer(code, "c2", 2, DateTime.UtcNow);   // неверно

        var closure = rm.CloseQuestion(code)!;

        Assert.Equal(1, closure.ForHost.CorrectOptionId);
        Assert.Equal(1, closure.ForHost.AnswersPerOption.Single(o => o.OptionId == 1).Count);
        Assert.Equal(1, closure.ForHost.AnswersPerOption.Single(o => o.OptionId == 2).Count);
        Assert.True(closure.ForPlayers["c1"].IsCorrect);
        Assert.True(closure.ForPlayers["c1"].PointsEarned >= 500);
        Assert.False(closure.ForPlayers["c2"].IsCorrect);
        Assert.Equal(0, closure.ForPlayers["c2"].PointsEarned);
    }

    [Fact]
    public void Player_without_answer_gets_zero()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        rm.StartGame(code, "host");
        rm.StartNextQuestion(code);

        var closure = rm.CloseQuestion(code)!;

        Assert.False(closure.ForPlayers["c1"].Answered);
        Assert.Equal(0, closure.ForPlayers["c1"].PointsEarned);
    }

    [Fact]
    public void Ties_share_rank_and_next_rank_is_skipped()
    {
        var (rm, code) = NewRoom();
        foreach (var nick in new[] { "Анна", "Борис", "Вера", "Глеб" })
            rm.JoinRoom(code, nick, "c-" + nick);
        rm.StartGame(code, "host");
        rm.StartNextQuestion(code);

        rm.SubmitAnswer(code, "c-Анна", 1, DateTime.UtcNow);      // быстро: ~1000
        var slow = DateTime.UtcNow.AddSeconds(10);                 // одно время для двоих: одинаковые очки
        rm.SubmitAnswer(code, "c-Борис", 1, slow);
        rm.SubmitAnswer(code, "c-Вера", 1, slow);
        rm.SubmitAnswer(code, "c-Глеб", 2, DateTime.UtcNow);       // неверно: 0

        rm.CloseQuestion(code);
        var board = rm.EndGame(code);

        // Ожидаем места 1, 2, 2, 4; внутри ничьей порядок по нику
        Assert.Equal(new[] { 1, 2, 2, 4 }, board.Select(e => e.Rank).ToArray());
        Assert.Equal(new[] { "Анна", "Борис", "Вера", "Глеб" }, board.Select(e => e.Nick).ToArray());
    }

    [Fact]
    public void Disconnect_in_lobby_removes_player()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        rm.JoinRoom(code, "Борис", "c2");

        var result = rm.HandleDisconnect("c1")!;

        Assert.False(result.WasHost);
        Assert.Equal("Анна", result.Nick);
        Assert.Equal(1, result.TotalPlayers);
        // Ник освободился
        Assert.True(rm.JoinRoom(code, "Анна", "c3").Success);
    }

    [Fact]
    public void Disconnect_in_game_keeps_player_but_not_in_answer_count()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");
        rm.JoinRoom(code, "Борис", "c2");
        rm.StartGame(code, "host");
        rm.StartNextQuestion(code);

        rm.HandleDisconnect("c2");

        Assert.Equal((0, 1), rm.GetAnswerCount(code));   // Борис не входит в счётчик
        Assert.True(rm.RejoinRoom(code, "Борис", "c2-new").Success);   // но вернуться может
        Assert.Equal((0, 2), rm.GetAnswerCount(code));
    }

    [Fact]
    public void Host_disconnect_ends_game_and_removes_room()
    {
        var (rm, code) = NewRoom();
        rm.JoinRoom(code, "Анна", "c1");

        var result = rm.HandleDisconnect("host")!;

        Assert.True(result.WasHost);
        Assert.Equal("Комната не найдена", rm.JoinRoom(code, "Борис", "c2").Error);
    }
}