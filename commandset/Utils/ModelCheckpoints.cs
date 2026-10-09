using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Full copies of the model file under mcp-checkpoints\&lt;model name&gt;\ next to the model.
    /// Must run on Revit's API thread.
    /// </summary>
    public static class ModelCheckpoints
    {
        private const int Keep = 10;
        private static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(30);
        private static readonly Dictionary<string, DateTime> LastCreated =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static object Create(Document doc, string label)
        {
            string modelPath = RequireFileOnDisk(doc);
            if (doc.IsModified) doc.Save();

            string folder = FolderFor(modelPath);
            Directory.CreateDirectory(folder);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + LabelSuffix(label) + ".rvt";
            string dest = Path.Combine(folder, name);
            try
            {
                doc.Application.CopyModel(ModelPathUtils.ConvertUserVisiblePathToModelPath(modelPath), dest, true);
            }
            catch (Exception)
            {
                File.Copy(modelPath, dest, true);
            }
            LastCreated[modelPath] = DateTime.Now;

            return new
            {
                checkpoint = name,
                path = dest,
                sizeMb = Math.Round(new FileInfo(dest).Length / 1048576.0, 1),
                pruned = Prune(folder)
            };
        }

        /// <summary>Returns null when the model is not a file on disk or a checkpoint is recent.</summary>
        public static object CreateIfDue(Document doc)
        {
            if (doc == null || doc.IsModelInCloud || doc.IsReadOnly || string.IsNullOrEmpty(doc.PathName))
                return null;
            if (LastCreated.TryGetValue(doc.PathName, out var last) && DateTime.Now - last < AutoInterval)
                return null;
            return Create(doc, "auto");
        }

        public static object List(Document doc)
        {
            string folder = FolderFor(RequireFileOnDisk(doc));
            var items = Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.rvt")
                    .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                    .Select(f => new
                    {
                        checkpoint = Path.GetFileName(f),
                        created = File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm:ss"),
                        sizeMb = Math.Round(new FileInfo(f).Length / 1048576.0, 1)
                    })
                    .ToList<object>()
                : new List<object>();
            return new { folder, count = items.Count, checkpoints = items };
        }

        public static object Restore(UIApplication app, string checkpoint)
        {
            var doc = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active Revit document.");
            string modelPath = RequireFileOnDisk(doc);

            // GetFileName keeps the lookup inside the checkpoint folder.
            string source = Path.Combine(FolderFor(modelPath), Path.GetFileName(checkpoint ?? ""));
            if (!File.Exists(source))
                throw new ArgumentException($"No checkpoint '{checkpoint}' for this model; call list_checkpoints.");

            // Opening the checkpoint itself would turn it into the working file.
            string restored = Path.Combine(Path.GetDirectoryName(modelPath),
                $"{Path.GetFileNameWithoutExtension(modelPath)}_restored_{DateTime.Now:yyyyMMdd-HHmmss}.rvt");
            File.Copy(source, restored);

            var options = new OpenOptions();
            if (doc.IsWorkshared)
                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            app.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(restored), options, false);

            return new
            {
                restoredFrom = Path.GetFileName(source),
                openedAs = restored,
                detachedFromCentral = doc.IsWorkshared,
                next = "The model you were working in is still open. Close it without saving to keep the restored copy, " +
                       "or close the restored copy to discard it."
            };
        }

        private static string RequireFileOnDisk(Document doc)
        {
            if (doc.IsModelInCloud)
                throw new InvalidOperationException("Checkpoints work on model files on disk; cloud models keep their own version history.");
            if (string.IsNullOrEmpty(doc.PathName))
                throw new InvalidOperationException("Save the model to a file first.");
            if (doc.IsReadOnly)
                throw new InvalidOperationException("The model is read-only.");
            return doc.PathName;
        }

        private static string FolderFor(string modelPath) =>
            Path.Combine(Path.GetDirectoryName(modelPath), "mcp-checkpoints", Path.GetFileNameWithoutExtension(modelPath));

        private static string LabelSuffix(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            string clean = Regex.Replace(label.Trim(), @"[^\w\-]+", "_");
            return "_" + (clean.Length > 40 ? clean.Substring(0, 40) : clean);
        }

        private static int Prune(string folder)
        {
            var old = Directory.GetFiles(folder, "*.rvt")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Skip(Keep)
                .ToList();
            foreach (var file in old)
            {
                File.Delete(file);
                // CopyModel of a workshared file can leave a <name>_backup folder beside it.
                string backup = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "_backup");
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
            }
            return old.Count;
        }
    }
}
