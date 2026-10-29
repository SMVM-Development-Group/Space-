namespace SpacePlusPlus.Infrastructure;

/// <summary>
/// Cooperative pause flag consumed by long-running background loops.
/// Not thread-signal based: workers poll it between iterations.
/// </summary>
public sealed class PauseToken
{
    private volatile bool _paused;

    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public async ValueTask WaitIfPausedAsync(CancellationToken ct)
    {
        while (_paused && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(120, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
