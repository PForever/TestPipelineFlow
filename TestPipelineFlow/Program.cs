// See https://aka.ms/new-console-template for more information
Console.WriteLine("Hello, World!");

#region Domain

class RecoLine
{
    public int Id { get; }
    public SyncState SyncState { get; }
}
record SyncState(int RecoLineId, DateTime AttemtedOn, DateTime? AlertedOn = null, bool Succed = false, string? LastError = null);

#endregion

#region Services

class Repository
{
    public async IAsyncEnumerable<Result<RecoLine>> GetNotSyncedLines()
    {
        throw new NotImplementedException();
    }

    public Task SaveState(SyncState syncState)
    {
        throw new NotImplementedException();
    }
}
class SyncService
{
    public bool TrySend(RecoLine recoLine, out Exception error)
    {
        throw new NotImplementedException();
    }
}

class AlertService
{
    public Task AlertLine(RecoLine recoLine, Exception? error, string? message)
    {
        throw new NotImplementedException();
    }

    internal Task Alert(int count)
    {
        throw new NotImplementedException();
    }
}
#endregion

public class DomainLogic
{
    private Repository _repository;
    private SyncService _syncService;
    private AlertService _alertService;

    private int _maxDurationInMinutes;
    private DateTime _now;
    public async Task DoSync()
    {
        _now = DateTime.UtcNow;

        _ = await _repository.GetNotSyncedLines()
        .HandleEach(ResultState.Unknown, MarkAlreadyAlerted)
        .HandleEachNot(ResultState.AlreadyAlerted, MarkReadyToSync)
        .HandleEach(ResultState.ReadyToSync, TryToSync)
        .HandleEach(ResultState.SyncSuccess, SaveSyncSuccedState)
        .HandleEach(ResultState.SyncFailed, SaveSyncFailedState)
        .HandleEach(ResultState.SyncFailed | ResultState.AlreadyAlerted | ResultState.DbSaveFailed, AlertLine)
        .HandleAll(ResultState.SyncFailed | ResultState.AlreadyAlerted, AlertFailCount)
        ;
    }

    #region Handlers
    ResultState MarkAlreadyAlerted(RecoLine recoLine) 
        => recoLine.SyncState.AlertedOn is not null ? ResultState.AlreadyAlerted : ResultState.Unknown;

    ResultState MarkReadyToSync(RecoLine recoLine) 
        => (_now - recoLine.SyncState.AttemtedOn).TotalMinutes >= _maxDurationInMinutes ? ResultState.ReadyToSync : ResultState.Unknown;

    Result<RecoLine> TryToSync(Result<RecoLine> recoLine) 
        => _syncService.TrySend(recoLine.Value, out var error) ?
            recoLine with { State = ResultState.SyncSuccess } :
            recoLine with { State = ResultState.SyncFailed, Exception = error };

    async ValueTask<Result<RecoLine>> SaveSyncSuccedState(Result<RecoLine> recoLine)
    {
        try
        {
            var newState = recoLine.Value.SyncState ?? new SyncState(recoLine.Value.Id, _now);
            await _repository.SaveState(newState with { Succed = true, LastError = "" });
            return recoLine;
        }
        catch (Exception e)
        {
            return recoLine with { State = ResultState.DbSaveFailed, Exception = e };
        }
    }

    async ValueTask<Result<RecoLine>> SaveSyncFailedState(Result<RecoLine> recoLine)
    {
        try
        {
            var newState = recoLine.Value.SyncState ?? new SyncState(recoLine.Value.Id, _now);
            await _repository.SaveState(newState with { Succed = false, LastError = recoLine.Exception?.ToString() ?? "unknow exception" });
            return recoLine;
        }
        catch (Exception e)
        {
            return recoLine with { State = ResultState.DbSaveFailed, Exception = e };
        }
    }

    async ValueTask<Result<RecoLine>> AlertLine(Result<RecoLine> recoLine)
    {
        await _alertService.AlertLine(recoLine.Value, recoLine.Exception, $"failed with state {recoLine.State}");
        return recoLine;
    }

    async Task AlertFailCount(IReadOnlyCollection<RecoLine> recoLine)
    {
        await _alertService.Alert(recoLine.Count);
    } 
    #endregion
}

#region Infrastructure

readonly record struct Result<T>(T Value, ResultState State, Exception? Exception);
[Flags]
enum ResultState
{
    Unknown = 0,
    ReadyToSync = 1,
    AlreadyAlerted = 2,
    SyncSuccess = 4,
    SyncFailed = 8,
    DbSaveFailed = 16,
    All = 32 - 1,
}
class InfrastructureException : Exception
{
    public InfrastructureException(string? message) : base(message)
    {
    }
}
class InvalidOutputStatusException : InfrastructureException
{
    public InvalidOutputStatusException(string? message = null) : base(message)
    {
    }
}
static class Extantions
{
    private static Func<Result<T>, Result<T>> ToHandlerResult<T>(Func<T, ResultState> handler) => r => handler(r.Value) is var newState && newState != ResultState.Unknown ? new Result<T>(r.Value, newState, null) : r;
    private static Func<Result<T>, ValueTask<Result<T>>> ToHandlerResult<T>(Func<T, ValueTask<ResultState>> handler) => async r => await handler(r.Value) is var newState && newState != ResultState.Unknown ? new Result<T>(r.Value, newState, null) : r;
    private static void VerifyMatchAny(ResultState src, ResultState node)
    {
        if (!MatchAny(src, node)) throw new InvalidOutputStatusException($"Unexpected result state {node}. Expected any of {src}");
    }
    private static bool MatchAny(ResultState src, ResultState node) => node == ResultState.Unknown && src == ResultState.Unknown || (node & src) != ResultState.Unknown;

    public static IEnumerable<Result<T>> HandleEach<T>(this IEnumerable<Result<T>> src, ResultState handleState, Func<Result<T>, Result<T>> handler)
    {
        foreach (var item in src.Where(i => MatchAny(i.State, handleState)))
        {
            var result = handler(item);
            yield return result;
        }
    }
    public static IEnumerable<Result<T>> HandleEachNot<T>(this IEnumerable<Result<T>> src, ResultState handleState, Func<Result<T>, Result<T>> handler)
    {
        foreach (var item in src.Where(i => !MatchAny(i.State, handleState)))
        {
            var result = handler(item);
            yield return result;
        }
    }
    public static IReadOnlyCollection<T> HandleAll<T>(this IEnumerable<Result<T>> src, ResultState handleState, Action<IEnumerable<T>> action)
    {
        var list = src.Where(i => MatchAny(i.State, handleState)).Select(i => i.Value).ToList();
        action(list);
        return list;
    }


    

    public static IAsyncEnumerable<Result<T>> HandleEach<T>(this IAsyncEnumerable<Result<T>> src, ResultState handleState, Func<T, ResultState> handler) => HandleEach(src, handleState, ToHandlerResult(handler));
    public static async IAsyncEnumerable<Result<T>> HandleEach<T>(this IAsyncEnumerable<Result<T>> src, ResultState handleState, Func<Result<T>, Result<T>> handler)
    {
        await foreach (var item in src)
        {
            if (!MatchAny(item.State, handleState)) continue;
            var result = handler(item);
            yield return result;
        }
    }

    public static IAsyncEnumerable<Result<T>> HandleEach<T>(this IAsyncEnumerable<Result<T>> src, ResultState handleState, Func<T, ValueTask<ResultState>> handler) => HandleEach(src, handleState, ToHandlerResult(handler));
    public static async IAsyncEnumerable<Result<T>> HandleEach<T>(this IAsyncEnumerable<Result<T>> src, ResultState handleState, Func<Result<T>, ValueTask<Result<T>>> handler)
    {
        await foreach (var item in src)
        {
            if (!MatchAny(item.State, handleState)) continue;
            var result = await handler(item);
            yield return result;
        }
    }




    public static IAsyncEnumerable<Result<T>> HandleEachNot<T>(this IAsyncEnumerable<Result<T>> src, ResultState handleState, Func<T, ResultState> handler) => HandleEachNot(src, handleState, ToHandlerResult(handler));
    public static async IAsyncEnumerable<Result<T>> HandleEachNot<T>(this IAsyncEnumerable<Result<T>> src, ResultState handleState, Func<Result<T>, Result<T>> handler)
    {
        await foreach (var item in src)
        {
            if (MatchAny(item.State, handleState)) continue;
            var result = handler(item);
            yield return result;
        }
    }

    public static async Task<IReadOnlyCollection<T>> HandleAll<T>(this IAsyncEnumerable<Result<T>> src, ResultState handleState, Action<IReadOnlyCollection<T>> action)
    {
        var list = new List<T>();
        await foreach (var item in src)
        {
            if (MatchAny(item.State, handleState)) list.Add(item.Value);
        }
        action(list);
        return list;
    }

    public static async Task<IReadOnlyCollection<T>> HandleAll<T>(this IAsyncEnumerable<Result<T>> src, ResultState handleState, Func<IReadOnlyCollection<T>, Task> action)
    {
        var list = new List<T>();
        await foreach (var item in src)
        {
            if (MatchAny(item.State, handleState)) list.Add(item.Value);
        }
        await action(list);
        return list;
    }



    #endregion
}