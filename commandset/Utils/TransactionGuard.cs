using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Commit() returns RolledBack instead of throwing when Revit's failure handling
    /// (or the user, in an error dialog) cancels the transaction.
    /// </summary>
    public static class TransactionGuard
    {
        public static void EnsureCommitted(TransactionStatus status)
        {
            if (status != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    $"Revit did not commit the change (status: {status}); the model was left unchanged.");
        }

        public static void EnsureCommitted(TransactionStatus? status)
        {
            if (status.HasValue) EnsureCommitted(status.Value);
        }

        // Rolling back a transaction Revit already rolled back throws and hides the
        // original error.
        public static void RollBackIfStarted(Transaction t)
        {
            if (t != null && t.GetStatus() == TransactionStatus.Started) t.RollBack();
        }

        public static void RollBackIfStarted(SubTransaction t)
        {
            if (t != null && t.GetStatus() == TransactionStatus.Started) t.RollBack();
        }

        public static void RollBackIfStarted(TransactionGroup t)
        {
            if (t != null && t.GetStatus() == TransactionStatus.Started) t.RollBack();
        }
    }
}
