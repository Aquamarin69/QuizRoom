using Microsoft.AspNetCore.Authentication.Cookies;
using QuizRoom.Contracts;
using QuizRoom.Web.Components;
using QuizRoom.Web.Fakes;

// ВНИМАНИЕ: этот файл меняет только TL. Нужна правка - пишите в чат.

var builder = WebApplication.CreateBuilder(args);

// Локальные настройки (пароль БД и т.п.), файл не попадает в git
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR();

// Cookie-аутентификация ведущего
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.HttpOnly = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// ---- Сервисы: заглушки или реальные (переключатель UseFakes в appsettings) ----
var useFakes = builder.Configuration.GetValue<bool>("UseFakes");

if (useFakes)
{
    builder.Services.AddScoped<IAuthService, FakeAuthService>();
    builder.Services.AddSingleton<IQuizService, FakeQuizService>();
}
else
{
    // TODO (Влад): подключить БД и реальные сервисы, например:
    // builder.Services.AddDbContext<AppDbContext>(o => o.UseMySql(
    //     builder.Configuration.GetConnectionString("Default"),
    //     ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("Default"))));
    // builder.Services.AddScoped<IAuthService, AuthService>();
    // builder.Services.AddScoped<IQuizService, QuizService>();
    // builder.Services.AddSingleton<IRoomManager, RoomManager>();
    throw new InvalidOperationException(
        "Реальные сервисы ещё не подключены. Установите \"UseFakes\": true в appsettings.");
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
// TODO (Влад): app.MapHub<QuizHub>("/hubs/quiz");

app.Run();
