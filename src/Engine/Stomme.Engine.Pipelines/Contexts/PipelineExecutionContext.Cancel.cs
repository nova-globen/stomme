namespace Stomme.Engine.Pipelines;

/// <summary>
/// Provides context to the currently executing pipeline.
/// </summary>
public partial class PipelineExecutionContext
{
    private ICollection<CancellationTokenSource> _cancellationTokenSources = new List<CancellationTokenSource>();
    private ICollection<CancellationTokenRegistration> _cancellationRegistrations = new List<CancellationTokenRegistration>();

    /// <summary>
    /// Cancels the pipeline and all it's children.
    /// </summary>
    public void Cancel()
    {
        foreach (var source in _cancellationTokenSources)
            source.Cancel();
        
        _cancellationTokenSources.Clear();
    }
    
    private void CancelPipeline()
    {
        Bookmarks.Clear();
        _completionCallbackEntries.Clear();

        if (!CanTransitionTo(PipelineSubStatus.Cancelled))
            return;

        AddExecutionLogEntry("Pipeline cancelled");
        
        TransitionTo(PipelineSubStatus.Cancelled);
        
        foreach (var registration in _cancellationRegistrations)
            registration.Dispose();
    }
}