namespace Hawker.Web.Editions;

/// <summary>Tells open pages that a run started, finished or changed, so they can refresh without polling.</summary>
public sealed class RunEvents
{
    public event Action<int>? RunChanged;

    public void Notify(int runId)
    {
        try
        {
            RunChanged?.Invoke(runId);
        }
        catch
        {
            // A page that failed to refresh must not break the run that raised the event.
        }
    }
}
