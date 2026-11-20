namespace QuizRoom.Web.Data;

/// <summary>Ведущий (таблица hosts).</summary>
public class Host
{
    public int Id { get; set; }

    /// <summary>Логин, уникальный (3-50 символов).</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>Хэш пароля. Пароль в открытом виде не хранится.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public List<Quiz> Quizzes { get; set; } = new();
}

/// <summary>Викторина (таблица quizzes).</summary>
public class Quiz
{
    public int Id { get; set; }

    /// <summary>Владелец викторины.</summary>
    public int HostId { get; set; }
    public Host? Host { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<Question> Questions { get; set; } = new();
}

/// <summary>Вопрос викторины (таблица questions).</summary>
public class Question
{
    public int Id { get; set; }

    public int QuizId { get; set; }
    public Quiz? Quiz { get; set; }

    /// <summary>Порядковый номер вопроса в викторине.</summary>
    public int OrderNum { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Время на ответ в секундах (5-120).</summary>
    public int TimeLimitSec { get; set; }

    public List<AnswerOption> Options { get; set; } = new();
}

/// <summary>Вариант ответа (таблица answer_options).</summary>
public class AnswerOption
{
    public int Id { get; set; }

    public int QuestionId { get; set; }
    public Question? Question { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Признак правильного ответа.</summary>
    public bool IsCorrect { get; set; }
}