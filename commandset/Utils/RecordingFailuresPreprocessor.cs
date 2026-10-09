using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Keeps Revit failures away from the user: warnings are dismissed, errors roll the
    /// transaction back instead of showing a modal dialog. All messages are recorded so the
    /// caller can return them as the tool error.
    /// Usage: var failures = RecordingFailuresPreprocessor.AttachTo(transaction);
    ///        then after Commit() != Committed, report failures.Errors.
    /// </summary>
    public class RecordingFailuresPreprocessor : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var failures = failuresAccessor.GetFailureMessages();
            if (failures.Count == 0)
                return FailureProcessingResult.Continue;

            bool hasErrors = false;
            foreach (var failure in failures)
            {
                string text = failure.GetDescriptionText();
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    Warnings.Add(text);
                    failuresAccessor.DeleteWarning(failure);
                }
                else
                {
                    Errors.Add(text);
                    hasErrors = true;
                }
            }

            return hasErrors
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }

        /// <summary>
        /// Installs a new recorder on the transaction (before or after Start()).
        /// </summary>
        public static RecordingFailuresPreprocessor AttachTo(Transaction transaction)
        {
            var recorder = new RecordingFailuresPreprocessor();
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(recorder);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            return recorder;
        }
    }
}
