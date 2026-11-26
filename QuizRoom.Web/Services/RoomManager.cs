using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using QuizRoom.Contracts;
using QuizRoom.Web.Data;

namespace QuizRoom.Web.Services;

/// <summary>Вариант ответа в памяти комнаты.</summary>
public sealed record GameOption(int Id, string Text, bool IsCorrect);

/// <summary>Вопрос в памяти комнаты (загружается из БД один раз при создании комнаты).</summary>
public sealed record GameQuestion(int Id, string Text, int TimeLimitSec, List<GameOption> Options);

/// <summary>
/// Игровое ядро: комнаты в памяти сервера (IRoomManager из Contracts.cs).
/// Время ответа считается на сервере. Очки считает Scoring.Calculate.
/// Регистрируется как Singleton: состояние общее для всех подключений.
/// </summary>
public class RoomManager : IRoomManager
{
    private const int MaxPlayers = 30;   // не более 30 игроков в комнате
    private const int TopCount = 5;      // размер краткого рейтинга

    // Все комнаты: ключ - код из 6 цифр
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoomManager> _log;

    public RoomManager(IServiceScopeFactory scopeFactory, ILogger<RoomManager> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    // ---------- Состояние (в БД не пишется) ----------

    private sealed class Player
    {
        public string Nick { get; init; } = "";
        public string ConnectionId { get; set; } = "";
        public bool Connected { get; set; } = true;      // false, если связь потеряна во время игры
        public int Score { get; set; }
        public int? AnswerOptionId { get; set; }          // ответ на текущий вопрос
        public DateTime? AnsweredAtUtc { get; set; }      // серверное время ответа
        public PlayerQuestionResultDto? LastResult { get; set; }  // итог последнего вопроса (для возврата в игру)
    }

    private sealed class Room
    {
        public object Lock { get; } = new();
        public string Code { get; init; } = "";
        public int HostId { get; init; }
        public string HostConnectionId { get; init; } = "";
        public List<GameQuestion> Questions { get; init; } = new();
        public List<Player> Players { get; } = new();
        public RoomStatus Status { get; set; } = RoomStatus.Lobby;
        public bool GameStarted { get; set; }             // после старта вход закрыт
        public int CurrentIndex { get; set; } = -1;
        public DateTime QuestionStartedUtc { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    }

    // ---------- Создание комнаты ----------

    /// <summary>Создаёт комнату: только своя викторина, без вопросов отклоняется.</summary>
    public async Task<CreateRoomResult> CreateRoomAsync(int hostId, string hostConnectionId, int quizId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Фильтр по HostId: чужую викторину запустить нельзя
        var quiz = await db.Quizzes
            .AsNoTracking()
            .Include(q => q.Questions).ThenInclude(x => x.Options)
            .FirstOrDefaultAsync(q => q.Id == quizId && q.HostId == hostId);

        if (quiz is null)
            return new CreateRoomResult(false, null, "Викторина не найдена");
        if (quiz.Questions.Count == 0)
            return new CreateRoomResult(false, null, "Викторина без вопросов");

        var questions = quiz.Questions
            .OrderBy(x => x.OrderNum)
            .Select(x => new GameQuestion(x.Id, x.Text, x.TimeLimitSec,
                x.Options.OrderBy(o => o.Id)
                    .Select(o => new GameOption(o.Id, o.Text, o.IsCorrect))
                    .ToList()))
            .ToList();

        var code = CreateRoom(hostId, hostConnectionId, questions);
        return new CreateRoomResult(true, code, null);
    }

    /// <summary>Создаёт комнату из готового списка вопросов (без БД; используется и в тестах).</summary>
    public string CreateRoom(int hostId, string hostConnectionId, List<GameQuestion> questions)
    {
        while (true)
        {
            var code = Random.Shared.Next(100000, 1000000).ToString();   // 6 цифр
            var room = new Room
            {
                Code = code,
                HostId = hostId,
                HostConnectionId = hostConnectionId,
                Questions = questions
            };
            if (_rooms.TryAdd(code, room))
            {
                _log.LogInformation("Создана комната {Code}", code);
                return code;
            }
        }
    }

    // ---------- Подключение игроков ----------

    /// <summary>Вход игрока по коду и нику.</summary>
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
            if (room.Players.Count >= MaxPlayers)
                return new JoinResult(false, "Комната заполнена");

            room.Players.Add(new Player { Nick = nick, ConnectionId = connectionId });
            room.LastActivityUtc = DateTime.UtcNow;
            return new JoinResult(true, null);
        }
    }

    /// <summary>Возврат игрока по нику: новый connectionId и текущий этап игры.</summary>
    public RejoinResult RejoinRoom(string code, string nick, string newConnectionId)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room))
            return RejoinFail("Комната не найдена");

        nick = (nick ?? "").Trim();

        lock (room.Lock)
        {
            var player = room.Players.FirstOrDefault(p =>
                string.Equals(p.Nick, nick, StringComparison.OrdinalIgnoreCase));
            if (player is null)
                return RejoinFail("Игрок не найден");

            player.ConnectionId = newConnectionId;
            player.Connected = true;
            room.LastActivityUtc = DateTime.UtcNow;

            var current = room.Status == RoomStatus.Question ? BuildQuestionDto(room) : null;
            var last = room.Status == RoomStatus.QuestionResults ? player.LastResult : null;
            return new RejoinResult(true, null, room.Status, ConnectedCount(room),
                current, player.AnswerOptionId is not null, last);
        }
    }

    private static RejoinResult RejoinFail(string error) =>
        new(false, error, RoomStatus.Lobby, 0, null, false, null);

    // ---------- Ход игры ----------

    /// <summary>Начало игры. false, если нет игроков, игра уже идёт или вызвал не ведущий.</summary>
    public bool StartGame(string code, string hostConnectionId)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return false;
        lock (room.Lock)
        {
            if (room.HostConnectionId != hostConnectionId) return false;
            if (room.GameStarted || ConnectedCount(room) == 0) return false;
            room.GameStarted = true;
            room.LastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>Следующий вопрос или null (вопросы кончились или переход сейчас невозможен).</summary>
    public QuestionForPlayerDto? StartNextQuestion(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return null;
        lock (room.Lock)
        {
            if (!room.GameStarted || room.Status == RoomStatus.Question || room.Status == RoomStatus.Finished)
                return null;
            if (room.CurrentIndex + 1 >= room.Questions.Count)
                return null;

            room.CurrentIndex++;
            room.Status = RoomStatus.Question;
            room.QuestionStartedUtc = DateTime.UtcNow;
            room.LastActivityUtc = DateTime.UtcNow;

            // Сбрасываем ответы прошлого вопроса
            foreach (var p in room.Players)
            {
                p.AnswerOptionId = null;
                p.AnsweredAtUtc = null;
            }
            return BuildQuestionDto(room);
        }
    }

    /// <summary>Приём ответа: один раз на вопрос, время считается по серверу.</summary>
    public AnswerResult SubmitAnswer(string code, string connectionId, int optionId, DateTime receivedAtUtc)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room))
            return new AnswerResult(false, "Комната не найдена");

        lock (room.Lock)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
            if (player is null)
                return new AnswerResult(false, "Комната не найдена");

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

    /// <summary>Сколько игроков ответили и сколько всего (отключённые в счётчик не входят).</summary>
    public (int Answered, int Total) GetAnswerCount(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return (0, 0);
        lock (room.Lock)
            return CountAnswers(room);
    }

    /// <summary>
    /// Закрывает вопрос: считает очки, места и итоги. Идемпотентно:
    /// повторный вызов (гонка таймера и «все ответили») вернёт null.
    /// </summary>
    public QuestionClosure? CloseQuestion(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return null;
        lock (room.Lock)
        {
            if (room.Status != RoomStatus.Question) return null;

            var q = room.Questions[room.CurrentIndex];
            var correctId = q.Options.First(o => o.IsCorrect).Id;

            // Шаг 1: начисляем очки всем (без ответа или неверно - 0)
            var results = new List<(Player P, bool Answered, bool Correct, int Points)>();
            foreach (var p in room.Players)
            {
                var answered = p.AnswerOptionId is not null;
                var correct = answered && p.AnswerOptionId == correctId;
                var elapsed = answered
                    ? Math.Max(0, (p.AnsweredAtUtc!.Value - room.QuestionStartedUtc).TotalSeconds)
                    : 0;
                var points = Scoring.Calculate(correct, elapsed, q.TimeLimitSec);
                p.Score += points;
                results.Add((p, answered, correct, points));
            }

            room.Status = RoomStatus.QuestionResults;
            room.LastActivityUtc = DateTime.UtcNow;

            // Шаг 2: места считаем уже по обновлённым очкам
            var top = BuildLeaderboard(room, TopCount);
            var forPlayers = new Dictionary<string, PlayerQuestionResultDto>();
            foreach (var (p, answered, correct, points) in results)
            {
                var dto = new PlayerQuestionResultDto(
                    correctId, answered, correct, points, p.Score, RankOf(room, p), top);
                p.LastResult = dto;                       // нужен, если игрок вернётся
                if (p.Connected) forPlayers[p.ConnectionId] = dto;
            }

            var perOption = q.Options
                .Select(o => new OptionAnswerCountDto(o.Id, room.Players.Count(p => p.AnswerOptionId == o.Id)))
                .ToList();
            var (answeredCount, total) = CountAnswers(room);
            var forHost = new HostQuestionResultDto(correctId, answeredCount, total, top, perOption);

            return new QuestionClosure(forHost, forPlayers);
        }
    }

    /// <summary>Завершает игру и возвращает итоговый рейтинг.</summary>
    public List<LeaderboardEntryDto> EndGame(string code)
    {
        if (!_rooms.TryGetValue(code ?? "", out var room)) return new List<LeaderboardEntryDto>();
        lock (room.Lock)
        {
            room.Status = RoomStatus.Finished;
            room.LastActivityUtc = DateTime.UtcNow;
            _log.LogInformation("Игра в комнате {Code} завершена", code);
            return BuildLeaderboard(room, null);
        }
    }

    /// <summary>
    /// Разрыв соединения. Ведущий: игра завершается, комната удаляется.
    /// Игрок в лобби: удаляется. Игрок в игре: остаётся с очками, но не входит в счётчик ответивших.
    /// </summary>
    public DisconnectResult? HandleDisconnect(string connectionId)
    {
        foreach (var pair in _rooms)
        {
            var room = pair.Value;
            lock (room.Lock)
            {
                if (room.HostConnectionId == connectionId)
                {
                    room.Status = RoomStatus.Finished;
                    var total = ConnectedCount(room);
                    _rooms.TryRemove(pair.Key, out _);
                    _log.LogInformation("Ведущий отключился, комната {Code} закрыта", room.Code);
                    return new DisconnectResult(room.Code, true, null, total);
                }

                var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
                if (player is null) continue;

                if (!room.GameStarted) room.Players.Remove(player);
                else player.Connected = false;

                room.LastActivityUtc = DateTime.UtcNow;
                return new DisconnectResult(room.Code, false, player.Nick, ConnectedCount(room));
            }
        }
        return null;
    }

    // ---------- Вспомогательные методы (вызываются внутри lock) ----------

    private static int ConnectedCount(Room room) => room.Players.Count(p => p.Connected);

    private static (int Answered, int Total) CountAnswers(Room room)
    {
        var connected = room.Players.Where(p => p.Connected).ToList();
        return (connected.Count(p => p.AnswerOptionId is not null), connected.Count);
    }

    /// <summary>Текущий вопрос для игрока (поля IsCorrect нет).</summary>
    private static QuestionForPlayerDto BuildQuestionDto(Room room)
    {
        var q = room.Questions[room.CurrentIndex];
        return new QuestionForPlayerDto(
            room.CurrentIndex + 1, room.Questions.Count, q.Text, q.TimeLimitSec,
            room.QuestionStartedUtc,
            q.Options.Select(o => new OptionForPlayerDto(o.Id, o.Text)).ToList());
    }

    /// <summary>Место: одинаковые очки дают одинаковое место, следующее пропускается (1, 2, 2, 4).</summary>
    private static int RankOf(Room room, Player p) =>
        1 + room.Players.Count(x => x.Score > p.Score);

    /// <summary>Рейтинг: по очкам, внутри ничьей по нику.</summary>
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