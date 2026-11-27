using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using QuizRoom.Contracts;
using QuizRoom.Web.Hubs;

namespace QuizRoom.Web.Services;

/// <summary>
/// Помощник хаба: серверный таймер вопроса, закрытие вопроса и рассылка итогов.
/// Singleton: таймеры должны жить дольше одного вызова хаба.
/// </summary>
public class GameBroadcaster
{
    /// <summary>Служебное состояние комнаты на стороне хаба.</summary>
    private sealed class RoomRuntime
    {
        public string HostConnectionId { get; init; } = "";
        public bool Started { get; set; }                 // вызван StartGame
        public CancellationTokenSource? Timer { get; set; } // не null, пока идёт вопрос
    }

    private readonly ConcurrentDictionary<string, RoomRuntime> _runtime = new();
    private readonly IRoomManager _rooms;
    private readonly IHubContext<GameHub, IQuizHubClient> _hub;
    private readonly ILogger<GameBroadcaster> _log;

    public GameBroadcaster(IRoomManager rooms, IHubContext<GameHub, IQuizHubClient> hub,
        ILogger<GameBroadcaster> log)
    {
        _rooms = rooms;
        _hub = hub;
        _log = log;
    }

    /// <summary>Имя группы всех участников комнаты.</summary>
    public static string RoomGroup(string code) => $"room:{code}";

    /// <summary>Имя группы ведущего.</summary>
    public static string HostGroup(string code) => $"host:{code}";

    /// <summary>Запоминает, какое соединение владеет комнатой.</summary>
    public void RegisterRoom(string code, string hostConnectionId) =>
        _runtime[code] = new RoomRuntime { HostConnectionId = hostConnectionId };

    /// <summary>Проверка: соединение - ведущий этой комнаты.</summary>
    public bool IsHost(string code, string connectionId) =>
        _runtime.TryGetValue(code ?? "", out var rt) && rt.HostConnectionId == connectionId;

    /// <summary>Игра начата (после этого можно переходить к вопросам).</summary>
    public void MarkStarted(string code)
    {
        if (_runtime.TryGetValue(code, out var rt)) rt.Started = true;
    }

    /// <summary>Забывает комнату и останавливает её таймер (ведущий ушёл).</summary>
    public void RemoveRoom(string code)
    {
        if (_runtime.TryRemove(code, out var rt)) rt.Timer?.Cancel();
    }

    /// <summary>
    /// Запускает следующий вопрос. Если вопросы кончились, завершает игру.
    /// Пока идёт текущий вопрос или игра не начата, ничего не делает.
    /// </summary>
    public async Task StartNextQuestionAsync(string code)
    {
        if (!_runtime.TryGetValue(code ?? "", out var rt) || !rt.Started || rt.Timer != null)
            return;

        var question = _rooms.StartNextQuestion(code);
        if (question is null)
        {
            await FinishGameAsync(code);
            return;
        }

        // Таймер запускаем до рассылки, чтобы повторный NextQuestion не прошёл
        StartTimer(code, rt, question.TimeLimitSec);

        await _hub.Clients.Group(RoomGroup(code)).QuestionStarted(question);
        var (answered, total) = _rooms.GetAnswerCount(code);
        await _hub.Clients.Group(HostGroup(code)).AnswerCountChanged(answered, total);
    }

    /// <summary>Закрывает вопрос и рассылает итоги. Безопасно вызывать повторно.</summary>
    public async Task CloseQuestionAsync(string code)
    {
        if (_runtime.TryGetValue(code, out var rt))
        {
            rt.Timer?.Cancel();
            rt.Timer = null;
        }

        var closure = _rooms.CloseQuestion(code);   // повторный вызов вернёт null
        if (closure is null) return;

        await _hub.Clients.Group(HostGroup(code)).HostQuestionEnded(closure.ForHost);
        foreach (var (connectionId, result) in closure.ForPlayers)
            await _hub.Clients.Client(connectionId).PlayerQuestionEnded(result);

        _log.LogInformation("Вопрос в комнате {Code} закрыт", code);
    }

    /// <summary>Завершает игру и рассылает итоговый рейтинг.</summary>
    public async Task FinishGameAsync(string code)
    {
        RemoveRoom(code);
        var board = _rooms.EndGame(code);
        await _hub.Clients.Group(RoomGroup(code)).GameEnded(board);
    }

    /// <summary>Если во время вопроса все оставшиеся игроки ответили, закрывает вопрос.</summary>
    public async Task CloseIfAllAnsweredAsync(string code)
    {
        if (!_runtime.TryGetValue(code, out var rt) || rt.Timer == null) return;
        var (answered, total) = _rooms.GetAnswerCount(code);
        if (total > 0 && answered >= total)
            await CloseQuestionAsync(code);
    }

    /// <summary>Таймер: по истечении времени вопрос закрывается сам.</summary>
    private void StartTimer(string code, RoomRuntime rt, int seconds)
    {
        rt.Timer?.Cancel();
        var cts = new CancellationTokenSource();
        rt.Timer = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
                await CloseQuestionAsync(code);
            }
            catch (OperationCanceledException) { /* вопрос закрыт раньше */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка таймера комнаты {Code}", code);
            }
        });
    }
}