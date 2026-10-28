namespace Stomme.Engine.Common;

public interface ITask
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}