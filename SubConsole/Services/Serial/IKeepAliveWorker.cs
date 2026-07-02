namespace SubConsole.Services.Serial;

public interface IKeepAliveWorker
{
    bool IsKeepAliveRunning { get; }
    void StartKeepAlive();
    void StopKeepAlive();
}