using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using QuizRoom.Contracts;
using QuizRoom.Web.Data;

namespace QuizRoom.Web.Services;
public class RoomManager : IRoomManager
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoomManager> _log;

    public RoomManager(IServiceScopeFactory scopeFactory, ILogger<RoomManager> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    // Внутренние классы состояния

    private sealed class OptionState
    {
        public int Id { get; init; }
        public string Text { get; init; } = "";
        public bool IsCorrect { get; init; }
    }

    private sealed class QuestionState
    {
        public int Id { get; init; }
        public string Text { get; init; } = "";
        public int TimeLimitSec { get; init; }
        public List<OptionState> Options { get; init; } = new();
    }

    private sealed class PlayerState
    {
        public string Nick { get; init; } = "";
        public string ConnectionId { get; set; } = "";
        public int Score { get; set; }
        public int? AnswerOptionId { get; set; }      // ответ на текущий вопрос
        public DateTime? AnsweredAtUtc { get; set; }   // серверное время ответа
        public int LastPoints { get; set; }            // очки за последний вопрос
        public bool LastCorrect { get; set; }
    }

    private sealed class Room
    {
        public object Lock { get; } = new();
        public string Code { get; init; } = "";
        public int HostId { get; init; }
        public string HostConnectionId { get; set; } = "";
        public List<QuestionState> Questions { get; init; } = new();
        public List<PlayerState> Players { get; } = new();
        public RoomStatus Status { get; set; } = RoomStatus.Lobby;
        public bool GameStarted { get; set; }          // после старта вход закрыт
        public int CurrentIndex { get; set; } = -1;
        public DateTime QuestionStartedUtc { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    }

    // Создание комнаты 
    public async Task<CreateRoomResult> CreateRoomAsync(int hostId, string hostConnectionId, int quizId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var quiz = await db.Quizzes
            .AsNoTracking()
            .Include(q => q.Questions).ThenInclude(x => x.Options)
            .FirstOrDefaultAsync(q => q.Id == quizId && q.HostId == hostId);

        if (quiz is null)
            return new CreateRoomResult(false, null, "Викторина не найдена");
        if (quiz.Questions.Count == 0)
            return new CreateRoomResult(false, null, "Викторина без вопросов");

        var questions = quiz.Questions.OrderBy(x => x.OrderNum).Select(x => new QuestionState
        {
            Id = x.Id,
            Text = x.Text,
            TimeLimitSec = x.TimeLimitSec,
            Options = x.Options.OrderBy(o => o.Id)
                .Select(o => new OptionState { Id = o.Id, Text = o.Text, IsCorrect = o.IsCorrect })
                .ToList()
        }).ToList();

        // Подбираем свободный код
        string code;
        Room room;
        do
        {
            code = Random.Shared.Next(100000, 1000000).ToString();
            room = new Room
            {
                Code = code,
                HostId = hostId,
                HostConnectionId = hostConnectionId,
                Questions = questions
            };
        } while (!_rooms.TryAdd(code, room));

        _log.LogInformation("Создана комната {Code} по викторине {QuizId}", code, quizId);
        return new CreateRoomResult(true, code, null);
    }

    // Подключение игрока 
    public JoinResult JoinRoom(string code, string nick, string connectionId)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room))
            return new JoinResult(false, "Комната не найдена");

        nick = (nick ?? "").Trim();

        lock (room.Lock)
        {
            if (room.GameStarted)
                return new JoinResult(false, "Игра уже началась");
            if (nick.Length is < 2 or > 20)
                return new JoinResult(false, "Недопустимая длина ника");
            // Ник уникален в комнате без учёта регистра
            if (room.Players.Any(p => string.Equals(p.Nick, nick, StringComparison.OrdinalIgnoreCase)))
                return new JoinResult(false, "Ник занят");

            room.Players.Add(new PlayerState { Nick = nick, ConnectionId = connectionId });
            room.LastActivityUtc = DateTime.UtcNow;
        }

        _log.LogInformation("Игрок подключился к комнате {Code}", code);
        return new JoinResult(true, null);
    }

    //Ход игры
    public bool StartGame(string code, string hostConnectionId)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return false;
        lock (room.Lock)
        {
            if (room.HostConnectionId != hostConnectionId) return false;
            if (room.GameStarted || room.Players.Count == 0) return false;
            room.GameStarted = true;
            room.LastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    public QuestionForPlayerDto? StartNextQuestion(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return null;
        lock (room.Lock)
        {
            if (!room.GameStarted || room.Status == RoomStatus.Question || room.Status == RoomStatus.Finished)
                return null;

            if (room.CurrentIndex + 1 >= room.Questions.Count)
            {
                room.Status = RoomStatus.Finished;
                return null;
            }

            room.CurrentIndex++;
            room.Status = RoomStatus.Question;
            room.QuestionStartedUtc = DateTime.UtcNow;
            room.LastActivityUtc = DateTime.UtcNow;

            foreach (var p in room.Players)
            {
                p.AnswerOptionId = null;
                p.AnsweredAtUtc = null;
                p.LastPoints = 0;
                p.LastCorrect = false;
            }

            var q = room.Questions[room.CurrentIndex];
            return new QuestionForPlayerDto(
                room.CurrentIndex + 1,
                room.Questions.Count,
                q.Text,
                q.TimeLimitSec,
                room.QuestionStartedUtc,
                q.Options.Select(o => new OptionForPlayerDto(o.Id, o.Text)).ToList());
        }
    }
    public AnswerResult SubmitAnswer(string code, string connectionId, int optionId, DateTime receivedAtUtc)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room))
            return new AnswerResult(false, "Комната не найдена");

        lock (room.Lock)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
            if (player is null)
                return new AnswerResult(false, "Ответ не принят");

            if (room.Status != RoomStatus.Question)
                return new AnswerResult(false, "Время вышло");

            var q = room.Questions[room.CurrentIndex];
            var elapsed = (receivedAtUtc - room.QuestionStartedUtc).TotalSeconds;
            if (elapsed > q.TimeLimitSec)
                return new AnswerResult(false, "Время вышло");

            if (player.AnswerOptionId is not null)
                return new AnswerResult(false, "Ответ уже отправлен");

            if (!q.Options.Any(o => o.Id == optionId))
                return new AnswerResult(false, "Ответ не принят");

            player.AnswerOptionId = optionId;
            player.AnsweredAtUtc = receivedAtUtc;
            room.LastActivityUtc = DateTime.UtcNow;
            return new AnswerResult(true, null);
        }
    }
    public void EndGame(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return;
        lock (room.Lock)
        {
            room.Status = RoomStatus.Finished;
            room.LastActivityUtc = DateTime.UtcNow;
        }
        _log.LogInformation("Игра в комнате {Code} завершена", code);
    }
    public void HandleDisconnect(string connectionId)
    {
        foreach (var pair in _rooms)
        {
            if (pair.Value.HostConnectionId == connectionId)
            {
                _rooms.TryRemove(pair.Key, out _);
                _log.LogInformation("Ведущий отключился, комната {Code} удалена", pair.Key);
            }
        }
    }
    public (HostQuestionResultDto Host, Dictionary<string, PlayerQuestionResultDto> ByConnection)? CloseQuestion(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return null;
        lock (room.Lock)
        {
            if (room.Status != RoomStatus.Question) return null;

            var q = room.Questions[room.CurrentIndex];
            var correctId = q.Options.First(o => o.IsCorrect).Id;

            foreach (var p in room.Players)
            {
                var answered = p.AnswerOptionId is not null;
                var correct = answered && p.AnswerOptionId == correctId;
                var elapsed = answered ? (p.AnsweredAtUtc!.Value - room.QuestionStartedUtc).TotalSeconds : 0;
                p.LastCorrect = correct;
                p.LastPoints = Scoring.Calculate(correct, elapsed, q.TimeLimitSec);
                p.Score += p.LastPoints;
            }

            room.Status = RoomStatus.QuestionResults;
            var top = BuildLeaderboard(room, 5);

            var byConnection = room.Players.ToDictionary(
                p => p.ConnectionId,
                p => new PlayerQuestionResultDto(
                    correctId, p.AnswerOptionId is not null, p.LastCorrect,
                    p.LastPoints, p.Score, RankOf(room, p), top));

            var host = new HostQuestionResultDto(
                correctId,
                room.Players.Count(p => p.AnswerOptionId is not null),
                room.Players.Count,
                top);

            return (host, byConnection);
        }
    }
    public (int Answered, int Total) GetAnswerCount(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return (0, 0);
        lock (room.Lock)
            return (room.Players.Count(p => p.AnswerOptionId is not null), room.Players.Count);
    }
    public List<LeaderboardEntryDto> GetLeaderboard(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return new List<LeaderboardEntryDto>();
        lock (room.Lock)
            return BuildLeaderboard(room, null);
    }

    //Рейтинг
    private static int RankOf(Room room, PlayerState p) =>
        1 + room.Players.Count(x => x.Score > p.Score);

    private static List<LeaderboardEntryDto> BuildLeaderboard(Room room, int? top)
    {
        var entries = room.Players
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Nick, StringComparer.OrdinalIgnoreCase)
            .Select(p => new LeaderboardEntryDto(RankOf(room, p), p.Nick, p.Score))
            .ToList();
        return top is null ? entries : entries.Take(top.Value).ToList();
    }
}