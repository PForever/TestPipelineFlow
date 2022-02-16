// See https://aka.ms/new-console-template for more information
Console.WriteLine("Hello, World!");

#region DomainModels

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

        ResultState state = ResultState.AlreadyAlerted | ResultState.DbSaveFailed;

        state.CompareTo(ResultState.AlreadyAlerted)


        Item.With(ResultState.Unknown).Handle<RecoLine>(MarkAlreadyAlerted1).Out(ResultState.AlreadyAlerted)


        _ = await _repository.GetNotSyncedLines()
        .HandleEach(ResultState.Unknown, MarkAlreadyAlerted)
        .HandleEach(Item.With(ResultState.Unknown).Handle(MarkAlreadyAlerted).Out(ResultState.AlreadyAlerted))
        .HandleEachNot(ResultState.AlreadyAlerted, MarkReadyToSync)
        .HandleEach(ResultState.ReadyToSync, TryToSync)
        .HandleEach(ResultState.SyncSuccess, SaveSyncSuccedState)
        .HandleEach(ResultState.SyncFailed, SaveSyncFailedState)
        .HandleEach(ResultState.SyncFailed | ResultState.AlreadyAlerted | ResultState.DbSaveFailed, AlertLine)
        .HandleAll(ResultState.SyncFailed | ResultState.AlreadyAlerted, AlertFailCount)
        ;
    }

    #region Handlers
    Result<RecoLine, ResultState> MarkAlreadyAlerted1(Result<RecoLine, ResultState> recoLine)
        => recoLine.Value.SyncState.AlertedOn is not null ? recoLine
        with { State = ResultState.AlreadyAlerted } :
        recoLine;


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

readonly record struct Result<T, TState>(T Value, TState State, Exception? Exception);
[Flags]
enum ResultState
{
    Unknown = 0,
    ReadyToSync = 1,
    AlreadyAlerted = 2,
    SyncSuccess = 4,
    SyncFailed = 8,
    DbSaveFailed = 16,
}

struct HandleNode<TInputState, TValue>
{
    public HandleNode(TInputState inputState, Func<Result<TValue, TInputState>, Result<TValue, TInputState>> handler, TInputState outputState) : this()
    {
        Input = inputState;
        Output = outputState;
        Handler = handler;
    }

    public TInputState Input { get; }
    public TInputState Output { get; }
    public Func<Result<TValue, TInputState>, Result<TValue, TInputState>> Handler  { get; }
}

//Item.With(ResultState.Unknown).Handle(MarkAlreadyAlerted).Out(ResultState.AlreadyAlerted)
static class Item
{
    public static Input<TInputState> With<TInputState, TValue>(this IEnumerable<TValue> _, TInputState inputState) => new(inputState);
}
class Input<TInputState>
{
    private readonly TInputState _inputState;

    public Input(TInputState inputState)
    {
        _inputState = inputState;
    }
    public Handler<TInputState, TValue> Handle<TValue>(Func<Result<TValue, TInputState>, Result<TValue, TInputState>> handler) => new(_inputState, handler);
}

internal class Handler<TInputState, TValue>
{
    private readonly TInputState _inputState;
    private readonly Func<Result<TValue, TInputState>, Result<TValue, TInputState>> _handler;

    public Handler(TInputState inputState, Func<Result<TValue, TInputState>, Result<TValue, TInputState>> handler)
    {
        _inputState = inputState;
        _handler = handler;
    }
    public HandleNode<TInputState, TValue> Out(TInputState outputState) => new(_inputState, _handler, outputState);
}



static class Extantions
{

    private static Func<Result<T, TState>, Result<T, TState>> ToHandlerResult<T, TState>(Func<T, ResultState> handler) => r => handler(r.Value) is var newState && newState != ResultState.Unknown ? new Result<T, TState>(r.Value, newState, null) : r;
    private static Func<Result<T, TState>, ValueTask<Result<T, TState>>> ToHandlerResult<T, TState>(Func<T, ValueTask<ResultState>> handler) => async r => await handler(r.Value) is var newState && newState != ResultState.Unknown ? new Result<T, TState>(r.Value, newState, null) : r;

    public static IEnumerable<Result<T, TState>> HandleEach<T, TState>(this IEnumerable<Result<T, TState>> src, ResultState handleState, Func<Result<T, TState>, Result<T, TState>> handler)
    where TState : 
    {
        foreach (var item in src.Where(i => (i.State & handleState) == i.State))
        {
            yield return handler(item);
        }
    }
    public static IEnumerable<Result<T, TState>> HandleEachNot<T, TState>(this IEnumerable<Result<T, TState>> src, ResultState handleState, Func<Result<T, TState>, Result<T, TState>> handler)
    where TState 
    {
        foreach (var item in src.Where(i => (i.State & handleState) != i.State))
        {
            yield return handler(item);
        }
    }
    public static IReadOnlyCollection<T> HandleAll<T, TState>(this IEnumerable<Result<T, TState>> src, ResultState handleState, Action<IEnumerable<T>> action)
    {
        var list = src.Where(i => (i.State & handleState) == i.State).Select(i => i.Value).ToList();
        action(list);
        return list;
    }


    

    public static IAsyncEnumerable<Result<T, TState>> HandleEach<T>(this IAsyncEnumerable<Result<T, TState>> src, ResultState handleState, Func<T, ResultState> handler) => HandleEach(src, handleState, ToHandlerResult(handler));
    public static async IAsyncEnumerable<Result<T, TState>> HandleEach<T>(this IAsyncEnumerable<Result<T, TState>> src, ResultState handleState, Func<Result<T, TState>, Result<T, TState>> handler)
    {
        await foreach (var item in src)
        {
            if ((item.State & handleState) != item.State) continue;
            yield return handler(item);
        }
    }

    public static IAsyncEnumerable<Result<T, TState>> HandleEach<T>(this IAsyncEnumerable<Result<T, TState>> src, ResultState handleState, Func<T, ValueTask<ResultState>> handler) => HandleEach(src, handleState, ToHandlerResult(handler));
    public static async IAsyncEnumerable<Result<T, TState>> HandleEach<T>(this IAsyncEnumerable<Result<T, TState>> src, ResultState handleState, Func<Result<T, TState>, ValueTask<Result<T, TState>>> handler)
    {
        await foreach (var item in src)
        {
            if ((item.State & handleState) != item.State) continue;
            yield return await handler(item);
        }
    }




    public static IAsyncEnumerable<Result<T, TState>> HandleEachNot<T>(this IAsyncEnumerable<Result<T, TState>> src, ResultState handleState, Func<T, ResultState> handler) => HandleEachNot(src, handleState, ToHandlerResult(handler));
    public static async IAsyncEnumerable<Result<T, TState>> HandleEachNot<T>(this IAsyncEnumerable<Result<T, TState>> src, ResultState handleState, Func<Result<T, TState>, Result<T, TState>> handler)
    {
        await foreach (var item in src)
        {
            if ((item.State & handleState) == item.State) continue;
            yield return handler(item);
        }
    }

    public static async Task<IReadOnlyCollection<T>> HandleAll<T>(this IAsyncEnumerable<Result<T, TState>> src, ResultState handleState, Action<IReadOnlyCollection<T>> action)
    {
        var list = new List<T>();
        await foreach (var item in src)
        {
            if ((item.State & handleState) == item.State) list.Add(item.Value);
        }
        action(list);
        return list;
    }

    public static async Task<IReadOnlyCollection<T>> HandleAll<T>(this IAsyncEnumerable<Result<T, TState>> src, ResultState handleState, Func<IReadOnlyCollection<T>, Task> action)
    {
        var list = new List<T>();
        await foreach (var item in src)
        {
            if ((item.State & handleState) == item.State) list.Add(item.Value);
        }
        await action(list);
        return list;
    }

    #endregion
}