using QuizRoom.Contracts;

namespace QuizRoom.Web.Fakes;

/// <summary>Заглушка сервиса викторин: данные в памяти, валидация по правилам из ТЗ.</summary>
public class FakeQuizService : IQuizService
{
    private readonly object _lock = new();
    private readonly List<QuizEditDto> _quizzes = new();
    private int _nextQuizId = 2;
    private int _nextQuestionId = 100;
    private int _nextOptionId = 1000;

    public FakeQuizService()
    {
        // Демо-викторина, чтобы список не был пустым
        _quizzes.Add(new QuizEditDto(1, "Демо-викторина", new List<QuestionEditDto>
        {
            new(1, 1, "Столица Франции?", 20, new List<OptionEditDto>
            {
                new(1, "Париж", true), new(2, "Лион", false), new(3, "Ницца", false)
            })
        }));
    }

    public Task<List<QuizSummaryDto>> GetMyQuizzesAsync()
    {
        lock (_lock)
            return Task.FromResult(_quizzes.Select(q => new QuizSummaryDto(q.Id, q.Title, q.Questions.Count)).ToList());
    }

    public Task<QuizEditDto?> GetQuizAsync(int quizId)
    {
        lock (_lock)
            return Task.FromResult(_quizzes.FirstOrDefault(q => q.Id == quizId));
    }

    public Task<(int? QuizId, ValidationResult Validation)> CreateQuizAsync(string title)
    {
        var errors = ValidateTitle(title);
        if (errors.Count > 0) return Task.FromResult(((int?)null, new ValidationResult(errors)));
        lock (_lock)
        {
            var id = _nextQuizId++;
            _quizzes.Add(new QuizEditDto(id, title.Trim(), new List<QuestionEditDto>()));
            return Task.FromResult(((int?)id, new ValidationResult(new List<string>())));
        }
    }

    public Task<ValidationResult> RenameQuizAsync(int quizId, string title)
    {
        var errors = ValidateTitle(title);
        if (errors.Count == 0)
            lock (_lock)
            {
                var i = _quizzes.FindIndex(q => q.Id == quizId);
                if (i >= 0) _quizzes[i] = _quizzes[i] with { Title = title.Trim() };
            }
        return Task.FromResult(new ValidationResult(errors));
    }

    public Task DeleteQuizAsync(int quizId)
    {
        lock (_lock) _quizzes.RemoveAll(q => q.Id == quizId);
        return Task.CompletedTask;
    }

    public Task<ValidationResult> SaveQuestionAsync(int quizId, QuestionEditDto question)
    {
        lock (_lock)
        {
            var quiz = _quizzes.FirstOrDefault(q => q.Id == quizId);
            if (quiz is null) return Task.FromResult(new ValidationResult(new List<string> { "Викторина не найдена" }));

            var isNew = question.Id is null || !quiz.Questions.Any(q => q.Id == question.Id);
            var errors = ValidateQuestion(question);
            if (isNew && quiz.Questions.Count >= 20)
                errors.Add("В викторине не может быть больше 20 вопросов");
            if (errors.Count > 0) return Task.FromResult(new ValidationResult(errors));

            var options = question.Options
                .Select(o => o with { Id = o.Id ?? _nextOptionId++ }).ToList();

            if (isNew)
                quiz.Questions.Add(question with { Id = _nextQuestionId++, OrderNum = quiz.Questions.Count + 1, Options = options });
            else
            {
                var i = quiz.Questions.FindIndex(q => q.Id == question.Id);
                quiz.Questions[i] = question with { OrderNum = quiz.Questions[i].OrderNum, Options = options };
            }
            return Task.FromResult(new ValidationResult(new List<string>()));
        }
    }

    public Task DeleteQuestionAsync(int quizId, int questionId)
    {
        lock (_lock)
        {
            var quiz = _quizzes.FirstOrDefault(q => q.Id == quizId);
            if (quiz is null) return Task.CompletedTask;
            quiz.Questions.RemoveAll(q => q.Id == questionId);
            for (var i = 0; i < quiz.Questions.Count; i++)           // перенумеровать порядок
                quiz.Questions[i] = quiz.Questions[i] with { OrderNum = i + 1 };
        }
        return Task.CompletedTask;
    }

    // ---- Валидация (те же правила, что будут в реальном сервисе) ----

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
