using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using QuizRoom.Contracts;
using QuizRoom.Web.Services;

namespace QuizRoom.Web.Hubs;

/// <summary>
/// SignalR-хаб игры (адрес /hubs/game). Группы: room:{code} - все участники и ведущий,
/// host:{code} - только ведущий. Методы ведущего работают только для владельца комнаты.
/// </summary>
public class GameHub : Hub<IQuizHubClient>, IQuizHubServer
{
    private readonly IRoomManager _rooms;
    private readonly GameBroadcaster _game;

    public GameHub(IRoomManager rooms, GameBroadcaster game)
    {
        _rooms = rooms;
        _game = game;
    }

    // ---------------- Ведущий ----------------

    /// <summary>Создаёт комнату. Id ведущего берётся из cookie-аутентификации.</summary>
    public async Task<CreateRoomResult> CreateRoom(int quizId)
    {
        // Без cookie hostId = 0: комната не создастся (викторина не найдена)
        var hostId = int.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

        var result = await _rooms.CreateRoomAsync(hostId, Context.ConnectionId, quizId);
        if (result.Success && result.RoomCode is { } code)
        {
            _game.RegisterRoom(code, Context.ConnectionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, GameBroadcaster.RoomGroup(code));
            await Groups.AddToGroupAsync(Context.ConnectionId, GameBroadcaster.HostGroup(code));
        }
        return result;
    }

    /// <summary>Начало игры. false, если нет игроков или вызвал не владелец.</summary>
    public async Task<bool> StartGame(string roomCode)
    {
        if (!_game.IsHost(roomCode, Context.ConnectionId)) return false;
        if (!_rooms.StartGame(roomCode, Context.ConnectionId)) return false;

        _game.MarkStarted(roomCode);
        await Clients.Group(GameBroadcaster.RoomGroup(roomCode)).GameStarted();
        return true;
    }

    /// <summary>Следующий вопрос. После последнего вопроса сервер сам шлёт GameEnded.</summary>
    public async Task NextQuestion(string roomCode)
    {
        if (!_game.IsHost(roomCode, Context.ConnectionId)) return;
        await _game.StartNextQuestionAsync(roomCode);
    }

    /// <summary>Досрочное завершение игры.</summary>
    public async Task EndGame(string roomCode)
    {
        if (!_game.IsHost(roomCode, Context.ConnectionId)) return;
        await _game.FinishGameAsync(roomCode);
    }

    // ---------------- Игрок ----------------

    /// <summary>Вход игрока. При успехе всей группе уходит PlayerJoined.</summary>
    public async Task<JoinResult> JoinRoom(string roomCode, string nick)
    {
        var result = _rooms.JoinRoom(roomCode, nick, Context.ConnectionId);
        if (result.Success)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GameBroadcaster.RoomGroup(roomCode));
            var (_, total) = _rooms.GetAnswerCount(roomCode);   // всего подключённых игроков
            await Clients.Group(GameBroadcaster.RoomGroup(roomCode)).PlayerJoined(nick.Trim(), total);
        }
        return result;
    }

    /// <summary>Возврат игрока по нику после обрыва связи.</summary>
    public async Task<RejoinResult> RejoinRoom(string roomCode, string nick)
    {
        var result = _rooms.RejoinRoom(roomCode, nick, Context.ConnectionId);
        if (result.Success)
            await Groups.AddToGroupAsync(Context.ConnectionId, GameBroadcaster.RoomGroup(roomCode));
        return result;
    }

    /// <summary>Ответ игрока. Время фиксирует сервер. Если ответили все, вопрос закрывается.</summary>
    public async Task<AnswerResult> SubmitAnswer(string roomCode, int optionId)
    {
        var result = _rooms.SubmitAnswer(roomCode, Context.ConnectionId, optionId, DateTime.UtcNow);
        if (result.Accepted)
        {
            var (answered, total) = _rooms.GetAnswerCount(roomCode);
            await Clients.Group(GameBroadcaster.HostGroup(roomCode)).AnswerCountChanged(answered, total);
            await _game.CloseIfAllAnsweredAsync(roomCode);
        }
        return result;
    }

    // ---------------- Отключение ----------------

    /// <summary>
    /// Ведущий ушёл: игра завершается (HostDisconnected). Игрок ушёл: PlayerLeft;
    /// если он был последним неответившим, вопрос закрывается.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var result = _rooms.HandleDisconnect(Context.ConnectionId);
        if (result is not null)
        {
            var group = GameBroadcaster.RoomGroup(result.Code);
            if (result.WasHost)
            {
                _game.RemoveRoom(result.Code);
                await Clients.Group(group).HostDisconnected();
            }
            else if (result.Nick is not null)
            {
                await Clients.Group(group).PlayerLeft(result.Nick, result.TotalPlayers);
                await _game.CloseIfAllAnsweredAsync(result.Code);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }
}