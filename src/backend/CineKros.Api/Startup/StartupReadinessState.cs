namespace CineKros.Api.Startup;

public sealed class StartupReadinessState
{
    private int _isReady;

    public bool IsReady => Volatile.Read(ref _isReady) == 1;

    public void MarkReady() => Volatile.Write(ref _isReady, 1);
}
