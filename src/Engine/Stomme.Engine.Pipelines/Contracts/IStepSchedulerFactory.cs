namespace Stomme.Engine.Pipelines;

public interface IStepSchedulerFactory
{
    IStepScheduler CreateScheduler();
}