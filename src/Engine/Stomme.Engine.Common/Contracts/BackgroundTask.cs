namespace Stomme.Engine.Common;

public abstract class BackgroundTask : IBackgroundTask
{
    public virtual Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}