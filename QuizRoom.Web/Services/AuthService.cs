using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using QuizRoom.Contracts;
using QuizRoom.Web.Data;
// Псевдоним, чтобы не путать нашу сущность с системным классом Host
using HostEntity = QuizRoom.Web.Data.Host;

namespace QuizRoom.Web.Services;

/// <summary>
/// Реальная авторизация ведущих: регистрация, вход, выход.
/// Пароли хранятся только в виде хэшей BCrypt. Пароль и хэш нигде не логируются.
/// Cookie выдаётся, когда метод вызван из HTTP-эндпоинта (там доступен HttpContext).
/// </summary>
public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuthService> _log;

    public AuthService(AppDbContext db, IHttpContextAccessor http, ILogger<AuthService> log)
    {
        _db = db;
        _http = http;
        _log = log;
    }

    /// <summary>Id текущего ведущего из cookie или null, если не авторизован.</summary>
    public int? CurrentHostId
    {
        get
        {
            var value = _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(value, out var id) ? id : null;
        }
    }

    /// <summary>Регистрация: проверка длин, уникальность логина, хэш пароля, автоматический вход.</summary>
    public async Task<AuthResult> RegisterAsync(string login, string password)
    {
        login = (login ?? string.Empty).Trim();

        if (login.Length is < 3 or > 50)
            return new AuthResult(false, "Логин должен быть от 3 до 50 символов");
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            return new AuthResult(false, "Пароль должен быть не короче 6 символов");

        if (await _db.Hosts.AnyAsync(h => h.Login == login))
            return new AuthResult(false, "Такой логин уже занят");

        var host = new HostEntity
        {
            Login = login,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        };
        _db.Hosts.Add(host);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Два человека одновременно заняли один логин: сработал уникальный индекс
            return new AuthResult(false, "Такой логин уже занят");
        }

        _log.LogInformation("Зарегистрирован ведущий {Login}", login);
        await SignInAsync(host);
        return new AuthResult(true, null);
    }

    /// <summary>Вход: поиск ведущего по логину и проверка пароля по хэшу.</summary>
    public async Task<AuthResult> LoginAsync(string login, string password)
    {
        login = (login ?? string.Empty).Trim();

        var host = await _db.Hosts.FirstOrDefaultAsync(h => h.Login == login);
        if (host is null || string.IsNullOrEmpty(password)
            || !BCrypt.Net.BCrypt.Verify(password, host.PasswordHash))
        {
            _log.LogInformation("Неудачная попытка входа, логин {Login}", login);
            return new AuthResult(false, "Проверьте логин и пароль");
        }

        _log.LogInformation("Вход ведущего {Login}", login);
        await SignInAsync(host);
        return new AuthResult(true, null);
    }

    /// <summary>Выход: удаление cookie.</summary>
    public async Task LogoutAsync()
    {
        var context = _http.HttpContext;
        if (context is not null)
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>Выдаёт cookie ведущему (только если есть HttpContext, то есть вызов из эндпоинта).</summary>
    private async Task SignInAsync(HostEntity host)
    {
        var context = _http.HttpContext;
        if (context is null) return;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, host.Id.ToString()),
            new(ClaimTypes.Name, host.Login)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }
}