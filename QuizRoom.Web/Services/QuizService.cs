using Microsoft.EntityFrameworkCore;
using QuizRoom.Contracts;
using QuizRoom.Web.Data;

namespace QuizRoom.Web.Services;

/// <summary>
/// Реальный сервис викторин (MySQL). Все методы работают только с викторинами
/// текущего ведущего (фильтр по host_id). Тексты ошибок такие же, как в FakeQuizService.
/// </summary>
public class QuizService : IQuizService
{
    private const int MaxQuestions = 20;

    private readonly AppDbContext _db;
    private readonly IAuthService _auth;
    private readonly ILogger<QuizService> _log;

    public QuizService(AppDbContext db, IAuthService auth, ILogger<QuizService> log)
    {
        _db = db;
        _auth = auth;
        _log = log;
    }

    /// <summary>Список викторин текущего ведущего с числом вопросов.</summary>
    public async Task<List<QuizSummaryDto>> GetMyQuizzesAsync()
    {
        var hostId = _auth.CurrentHostId;
        if (hostId is null) return new List<QuizSummaryDto>();

        return await _db.Quizzes
            .Where(q => q.HostId == hostId)
            .OrderBy(q => q.Id)
            .Select(q => new QuizSummaryDto(q.Id, q.Title, q.Questions.Count))
            .ToListAsync();
    }

    /// <summary>Викторина целиком (вопросы и варианты) или null, если она чужая или не найдена.</summary>
    public async Task<QuizEditDto?> GetQuizAsync(int quizId)
    {
        var hostId = _auth.CurrentHostId;
        if (hostId is null) return null;

        var quiz = await _db.Quizzes
            .AsNoTracking()
            .Include(q => q.Questions).ThenInclude(x => x.Options)
            .FirstOrDefaultAsync(q => q.Id == quizId && q.HostId == hostId);
        if (quiz is null) return null;

        var questions = quiz.Questions
            .OrderBy(x => x.OrderNum)
            .Select(x => new QuestionEditDto(
                x.Id, x.OrderNum, x.Text, x.TimeLimitSec,
                x.Options.OrderBy(o => o.Id)
                    .Select(o => new OptionEditDto(o.Id, o.Text, o.IsCorrect))
                    .ToList()))
            .ToList();

        return new QuizEditDto(quiz.Id, quiz.Title, questions);
    }

    /// <summary>Создание викторины с названием.</summary>
    public async Task<(int? QuizId, ValidationResult Validation)> CreateQuizAsync(string title)
    {
        var hostId = _auth.CurrentHostId;
        if (hostId is null)
            return (null, new ValidationResult(new List<string> { "Войдите в систему" }));

        var errors = ValidateTitle(title);
        if (errors.Count > 0) return (null, new ValidationResult(errors));

        var quiz = new Quiz { HostId = hostId.Value, Title = title.Trim() };
        _db.Quizzes.Add(quiz);
        await _db.SaveChangesAsync();

        _log.LogInformation("Ведущий {HostId} создал викторину {QuizId}", hostId, quiz.Id);
        return (quiz.Id, new ValidationResult(new List<string>()));
    }

    /// <summary>Переименование викторины.</summary>
    public async Task<ValidationResult> RenameQuizAsync(int quizId, string title)
    {
        var hostId = _auth.CurrentHostId;
        if (hostId is null)
            return new ValidationResult(new List<string> { "Войдите в систему" });

        var errors = ValidateTitle(title);
        if (errors.Count > 0) return new ValidationResult(errors);

        var quiz = await _db.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId && q.HostId == hostId);
        if (quiz is null)
            return new ValidationResult(new List<string> { "Викторина не найдена" });

        quiz.Title = title.Trim();
        await _db.SaveChangesAsync();
        return new ValidationResult(new List<string>());
    }

    /// <summary>Удаление викторины. Вопросы и варианты удаляются каскадом на уровне БД.</summary>
    public async Task DeleteQuizAsync(int quizId)
    {
        var hostId = _auth.CurrentHostId;
        if (hostId is null) return;

        var deleted = await _db.Quizzes
            .Where(q => q.Id == quizId && q.HostId == hostId)
            .ExecuteDeleteAsync();

        if (deleted > 0)
            _log.LogInformation("Ведущий {HostId} удалил викторину {QuizId}", hostId, quizId);
    }

    /// <summary>
    /// Создание или обновление вопроса. Некорректный вопрос не сохраняется,
    /// возвращается список причин.
    /// </summary>
    public async Task<ValidationResult> SaveQuestionAsync(int quizId, QuestionEditDto question)
    {
        var hostId = _auth.CurrentHostId;
        if (hostId is null)
            return new ValidationResult(new List<string> { "Войдите в систему" });

        var quiz = await _db.Quizzes
            .Include(q => q.Questions)
            .FirstOrDefaultAsync(q => q.Id == quizId && q.HostId == hostId);
        if (quiz is null)
            return new ValidationResult(new List<string> { "Викторина не найдена" });

        var existing = question.Id is null
            ? null
            : quiz.Questions.FirstOrDefault(x => x.Id == question.Id);

        var errors = ValidateQuestion(question);
        if (existing is null && quiz.Questions.Count >= MaxQuestions)
            errors.Add("В викторине не может быть больше 20 вопросов");
        if (errors.Count > 0) return new ValidationResult(errors);

        if (existing is null)
        {
            existing = new Question
            {
                QuizId = quiz.Id,
                OrderNum = quiz.Questions.Count + 1
            };
            _db.Questions.Add(existing);
        }
        else
        {
            // Старые варианты заменяем новыми
            var old = await _db.AnswerOptions.Where(o => o.QuestionId == existing.Id).ToListAsync();
            _db.AnswerOptions.RemoveRange(old);
        }

        existing.Text = question.Text.Trim();
        existing.TimeLimitSec = question.TimeLimitSec;
        foreach (var o in question.Options)
            existing.Options.Add(new AnswerOption { Text = o.Text.Trim(), IsCorrect = o.IsCorrect });

        await _db.SaveChangesAsync();
        return new ValidationResult(new List<string>());
    }

    /// <summary>Удаление вопроса и перенумерация порядка оставшихся.</summary>
    public async Task DeleteQuestionAsync(int quizId, int questionId)
    {
        var hostId = _auth.CurrentHostId;
        if (hostId is null) return;

        var quiz = await _db.Quizzes
            .Include(q => q.Questions)
            .FirstOrDefaultAsync(q => q.Id == quizId && q.HostId == hostId);
        if (quiz is null) return;

        var target = quiz.Questions.FirstOrDefault(x => x.Id == questionId);
        if (target is null) return;

        _db.Questions.Remove(target);
        await _db.SaveChangesAsync();

        // Перенумерация в два шага: из-за уникального индекса (quiz_id, order_num)
        // нельзя сразу ставить новые номера, поэтому сначала временные отрицательные.
        var rest = quiz.Questions.Where(x => x.Id != questionId).OrderBy(x => x.OrderNum).ToList();
        for (var i = 0; i < rest.Count; i++) rest[i].OrderNum = -(i + 1);
        await _db.SaveChangesAsync();
        for (var i = 0; i < rest.Count; i++) rest[i].OrderNum = i + 1;
        await _db.SaveChangesAsync();
    }

    // ---- Валидация (те же правила и тексты, что в FakeQuizService) ----

    private static List<string> ValidateTitle(string title)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(title)) errors.Add("Укажите название викторины");
        else if (title.Trim().Length > 100) errors.Add("Название не должно быть длиннее 100 символов");
        return errors;
    }

    private static List<string> ValidateQuestion(QuestionEditDto q)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(q.Text)) errors.Add("Укажите текст вопроса");
        else if (q.Text.Length > 500) errors.Add("Текст вопроса не должен быть длиннее 500 символов");

        if (q.Options.Count < 2 || q.Options.Count > 4)
            errors.Add("Должно быть от 2 до 4 вариантов ответа");
        if (q.Options.Any(o => string.IsNullOrWhiteSpace(o.Text)))
            errors.Add("Текст варианта ответа не может быть пустым");
        if (q.Options.Any(o => o.Text?.Length > 200))
            errors.Add("Текст варианта не должен быть длиннее 200 символов");

        if (q.Options.Count(o => o.IsCorrect) != 1)
            errors.Add("Отметьте ровно один правильный вариант");

        if (q.TimeLimitSec is < 5 or > 120)
            errors.Add("Время на ответ должно быть от 5 до 120 секунд");
        return errors;
    }
}