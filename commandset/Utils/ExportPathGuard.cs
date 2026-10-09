namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Export paths come from the AI agent. Restricting the extension keeps an export
    /// from overwriting a model, family or other user file.
    /// </summary>
    public static class ExportPathGuard
    {
        public static void Check(string path, params string[] allowedExtensions)
        {
            if (!Path.IsPathRooted(path))
                throw new ArgumentException($"Export path must be absolute: '{path}'");

            string ext = Path.GetExtension(path);
            if (!allowedExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(
                    $"Export path must end with {string.Join(" or ", allowedExtensions)} (got '{path}').");
        }
    }
}
