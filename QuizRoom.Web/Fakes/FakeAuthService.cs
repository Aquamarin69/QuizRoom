using QuizRoom.Contracts;

namespace QuizRoom.Web.Fakes;

/// <summary>Заглушка авторизации для разработки фронтенда. Тестовый вход: demo / demo123.</summary>
public class FakeAuthService : IAuthService
{
    // Статический словарь: пользователи живут, пока запущено приложение
    private static readonly Dictionary<string, string> Users = new() { ["demo"] = "demo123" };
    private string? _login;

    public int? CurrentHostId => _login is null ? null : 1;

    public Task<AuthResult> RegisterAsync(string login, string password)
    {
        if (string.IsNullOrWhiteSpace(login) || login.Length is < 3 or > 50)
            return Task.FromResult(new AuthResult(false, "Логин должен быть от 3 до 50 символов"));
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            return Task.FromResult(new AuthResult(false, "Пароль должен быть не короче 6 символов"));
        if (Users.ContainsKey(login))
            return Task.FromResult(new AuthResult(false, "Такой логин уже занят"));

        Users[login] = password;
        _login = login;
        return Task.FromResult(new AuthResult(true, null));
    }

    public Task<AuthResult> LoginAsync(string login, string password)
    {
        if (Users.TryGetValue(login ?? "", out var pwd) && pwd == password)
        {
            _login = login;
            return Task.FromResult(new AuthResult(true, null));
        }
        return Task.FromResult(new AuthResult(false, "Проверьте логин и пароль"));
    }

    public Task LogoutAsync()
    {
        _login = null;
        return Task.CompletedTask;
    }
}
