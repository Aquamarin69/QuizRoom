// QuizRoom — контракты между backend и frontend.
// Положить в отдельную папку/проект QuizRoom.Contracts. Менять только через pull request с согласия TL.

namespace QuizRoom.Contracts;

// ======================= Общие типы =======================

public enum RoomStatus { Lobby, Question, QuestionResults, Finished }

/// <summary>Строка рейтинга (краткого и итогового).</summary>
public record LeaderboardEntryDto(int Rank, string Nick, int Score);

// ======================= Авторизация ведущего =======================

public record AuthResult(bool Success, string? Error);

public interface IAuthService
{
    /// <summary>Регистрация. Ошибки: логин занят, недопустимая длина логина/пароля.</summary>
    Task<AuthResult> RegisterAsync(string login, string password);

    /// <summary>Вход. При неверных данных: "Проверьте логин и пароль".</summary>
    Task<AuthResult> LoginAsync(string login, string password);

    Task LogoutAsync();

    /// <summary>Id текущего ведущего или null, если не авторизован.</summary>
    int? CurrentHostId { get; }
}

// ======================= Викторины (редактор ведущего) =======================
// Здесь IsCorrect есть — это DTO ТОЛЬКО для редактора, игрокам не отправляется.

public record QuizSummaryDto(int Id, string Title, int QuestionCount);

public record OptionEditDto(int? Id, string Text, bool IsCorrect);

public record QuestionEditDto(
    int? Id,
    int OrderNum,
    string Text,
    int TimeLimitSec,
    List<OptionEditDto> Options);

public record QuizEditDto(int Id, string Title, List<QuestionEditDto> Questions);

/// <summary>Список понятных сообщений об ошибках. Пусто — валидно.</summary>
public record ValidationResult(List<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Все методы работают только с викторинами текущего ведущего (фильтр по host_id).</summary>
public interface IQuizService
{
    Task<List<QuizSummaryDto>> GetMyQuizzesAsync();
    Task<QuizEditDto?> GetQuizAsync(int quizId);
    Task<(int? QuizId, ValidationResult Validation)> CreateQuizAsync(string title);
    Task<ValidationResult> RenameQuizAsync(int quizId, string title);
    Task DeleteQuizAsync(int quizId);

    /// <summary>
    /// Создание/обновление вопроса. Проверки: текст, 2–4 варианта, ровно один правильный,
    /// время на ответ, длины полей, не более 20 вопросов в викторине.
    /// </summary>
    Task<ValidationResult> SaveQuestionAsync(int quizId, QuestionEditDto question);
    Task DeleteQuestionAsync(int quizId, int questionId);
}

// ======================= Игра: DTO для игрока и ведущего =======================

/// <summary>Вариант ответа для игрока. Поля IsCorrect НЕТ намеренно.</summary>
public record OptionForPlayerDto(int Id, string Text);

/// <summary>Вопрос для игрока. Правильный ответ не передаётся.</summary>
public record QuestionForPlayerDto(
    int Index,                // номер вопроса, с 1
    int Total,                // всего вопросов
    string Text,
    int TimeLimitSec,
    DateTime StartedAtUtc,    // серверное время начала (для отрисовки таймера)
    List<OptionForPlayerDto> Options);

/// <summary>Итог вопроса для конкретного игрока (отправляется персонально).</summary>
public record PlayerQuestionResultDto(
    int CorrectOptionId,
    bool Answered,
    bool IsCorrect,
    int PointsEarned,
    int TotalScore,
    int Rank,
    List<LeaderboardEntryDto> TopLeaderboard);

/// <summary>Итог вопроса для ведущего.</summary>
public record HostQuestionResultDto(
    int CorrectOptionId,
    int AnsweredCount,
    int TotalPlayers,
    List<LeaderboardEntryDto> TopLeaderboard);

public record JoinResult(bool Success, string? Error);       // Error: "Комната не найдена", "Игра уже началась", "Ник занят", "Недопустимая длина ника"
public record AnswerResult(bool Accepted, string? Error);    // Error: "Время вышло", "Ответ уже отправлен"
public record CreateRoomResult(bool Success, string? RoomCode, string? Error); // Error: "Викторина без вопросов"

// ======================= Игровое ядро (реализует Влад) =======================

public interface IRoomManager
{
    /// <summary>Создать комнату: загрузить вопросы из БД в память, сгенерировать числовой код.</summary>
    Task<CreateRoomResult> CreateRoomAsync(int hostId, string hostConnectionId, int quizId);

    JoinResult JoinRoom(string code, string nick, string connectionId);
    bool StartGame(string code, string hostConnectionId);
    QuestionForPlayerDto? StartNextQuestion(string code);
    AnswerResult SubmitAnswer(string code, string connectionId, int optionId, DateTime receivedAtUtc);
    void EndGame(string code);

    /// <summary>Вызывается при разрыве соединения. Если это ведущий — игра завершается.</summary>
    void HandleDisconnect(string connectionId);
}

/// <summary>Чистая функция подсчёта очков. Покрыть unit-тестами.</summary>
public static class Scoring
{
    /// <summary>очки = round(1000 * (1 - 0.5 * t / T)); 0 за неверный/отсутствующий ответ.</summary>
    public static int Calculate(bool isCorrect, double elapsedSec, int timeLimitSec)
    {
        if (!isCorrect || elapsedSec < 0 || elapsedSec > timeLimitSec) return 0;
        return (int)Math.Round(1000 * (1 - 0.5 * elapsedSec / timeLimitSec), MidpointRounding.AwayFromZero);
    }
}

// ======================= SignalR хаб =======================
// Группы: "room:{code}" — все участники, "host:{code}" — только ведущий.
// Методы ведущего помечены [Authorize] (cookie-аутентификация).

/// <summary>Методы, которые клиент вызывает на сервере.</summary>
public interface IQuizHubServer
{
    // --- Ведущий ---
    Task<CreateRoomResult> CreateRoom(int quizId);
    Task<bool> StartGame(string roomCode);          // false, если нет игроков
    Task NextQuestion(string roomCode);             // после последнего вопроса сервер сам шлёт GameEnded
    Task EndGame(string roomCode);

    // --- Игрок ---
    Task<JoinResult> JoinRoom(string roomCode, string nick);
    Task<AnswerResult> SubmitAnswer(string roomCode, int optionId);
}

/// <summary>События, которые сервер отправляет клиентам.</summary>
public interface IQuizHubClient
{
    // --- Ведущему ---
    Task PlayerJoined(string nick, int totalPlayers);
    Task AnswerCountChanged(int answered, int totalPlayers);
    Task HostQuestionEnded(HostQuestionResultDto result);

    // --- Всем в комнате ---
    Task GameStarted();
    Task QuestionStarted(QuestionForPlayerDto question);
    Task GameEnded(List<LeaderboardEntryDto> finalLeaderboard);
    Task HostDisconnected();                         // игра завершена из-за отключения ведущего

    // --- Игроку персонально ---
    Task PlayerQuestionEnded(PlayerQuestionResultDto result);
}
