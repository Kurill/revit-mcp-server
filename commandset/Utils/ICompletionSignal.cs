namespace RevitMCPCommandSet.Utils
{
    /// <summary>Exposes the event a handler sets when its Execute finishes.</summary>
    public interface ICompletionSignal
    {
        ManualResetEvent CompletionSignal { get; }
    }
}
