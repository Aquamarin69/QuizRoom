
using QuizRoom.Contracts;

namespace QuizRoom.Web.Services;

public interface IGameConnection
{
    // Методы ведущего
    Task<CreateRoomResult> CreateRoomAsync(int quizId);
    Task<bool> StartGameAsync(string roomCode);
    Task NextQuestionAsync(string roomCode);
    Task EndGameAsync(string roomCode);

    // Методы игрока
    Task<JoinResult> JoinRoomAsync(string roomCode, string nick);
    Task<AnswerResult> SubmitAnswerAsync(string roomCode, int optionId);

    // События игрового хаба
    event Action<string, int>? PlayerJoined;
    event Action<int, int>? AnswerCountChanged;
    event Action<HostQuestionResultDto>? HostQuestionEnded;

    event Action? GameStarted;
    event Action<QuestionForPlayerDto>? QuestionStarted;
    event Action<List<LeaderboardEntryDto>>? GameEnded;
    event Action? HostDisconnected;

    event Action<PlayerQuestionResultDto>? PlayerQuestionEnded;
}
