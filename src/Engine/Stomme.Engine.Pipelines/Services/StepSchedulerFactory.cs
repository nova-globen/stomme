namespace Stomme.Engine.Pipelines;

public class StepSchedulerFactory : IStepSchedulerFactory
{
    //public IStepScheduler CreateScheduler() => new StackBasedStepScheduler();
    public IStepScheduler CreateScheduler() => new QueueBasedStepScheduler();
}