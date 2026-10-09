using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class CreateGridEventHandler : IExternalEventHandler, IWaitableExternalEventHandler, RevitMCPCommandSet.Utils.ICompletionSignal
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;

        /// <summary>
        /// Event synchronization object
        /// </summary>
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        public ManualResetEvent CompletionSignal => _resetEvent;

        /// <summary>
        /// Grid creation parameters
        /// </summary>
        public GridCreationInfo Parameters { get; private set; }

        /// <summary>
        /// Execution result
        /// </summary>
        public AIResult<List<GridCreationResult>> Result { get; private set; }

        /// <summary>
        /// Set parameters for grid creation
        /// </summary>
        public void SetParameters(GridCreationInfo parameters)
        {
            Parameters = parameters;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                // Validate parameters
                if (!Parameters.Validate(out string validationError))
                {
                    Result = new AIResult<List<GridCreationResult>>
                    {
                        Success = false,
                        Message = $"Validation failed: {validationError}",
                        Response = null
                    };
                    return;
                }

                List<GridCreationResult> createdGrids = new List<GridCreationResult>();

                // Get existing grid names for duplicate checking
                var existingGridNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Select(g => g.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                using (Transaction trans = new Transaction(doc, "Create Grid System"))
                {
                    trans.Start();

                    // Create X-axis grids (vertical lines, parallel to Y-axis)
                    List<double> xPositions = GeneratePositions(
                        Parameters.XCount,
                        Parameters.XSpacing,
                        Parameters.XStartPosition
                    );

                    List<string> xLabels = GenerateLabels(
                        Parameters.XCount,
                        Parameters.XStartLabel,
                        Parameters.XNamingStyle
                    );

                    for (int i = 0; i < xPositions.Count; i++)
                    {
                        double xPos = xPositions[i];
                        string label = xLabels[i];

                        // Handle duplicate names with auto-increment
                        string uniqueLabel = GetUniqueGridName(label, existingGridNames);
                        existingGridNames.Add(uniqueLabel);

                        // X-axis grids run parallel to Y-axis (vertical lines)
                        XYZ startPoint = new XYZ(
                            xPos / 304.8,  // mm to ft
                            Parameters.YExtentMin / 304.8,
                            Parameters.Elevation / 304.8
                        );

                        XYZ endPoint = new XYZ(
                            xPos / 304.8,
                            Parameters.YExtentMax / 304.8,
                            Parameters.Elevation / 304.8
                        );

                        Line gridLine = Line.CreateBound(startPoint, endPoint);
                        Grid grid = Grid.Create(doc, gridLine);
                        grid.Name = uniqueLabel;

#if REVIT2024_OR_GREATER
                        long gridId = grid.Id.Value;
#else
                        long gridId = grid.Id.IntegerValue;
#endif

                        createdGrids.Add(new GridCreationResult
                        {
                            ElementId = gridId,
                            Name = uniqueLabel,
                            OriginalName = label,
                            WasRenamed = label != uniqueLabel,
                            Axis = "X",
                            Position = xPos
                        });
                    }

                    // Create Y-axis grids (horizontal lines, parallel to X-axis)
                    List<double> yPositions = GeneratePositions(
                        Parameters.YCount,
                        Parameters.YSpacing,
                        Parameters.YStartPosition
                    );

                    List<string> yLabels = GenerateLabels(
                        Parameters.YCount,
                        Parameters.YStartLabel,
                        Parameters.YNamingStyle
                    );

                    for (int i = 0; i < yPositions.Count; i++)
                    {
                        double yPos = yPositions[i];
                        string label = yLabels[i];

                        // Handle duplicate names with auto-increment
                        string uniqueLabel = GetUniqueGridName(label, existingGridNames);
                        existingGridNames.Add(uniqueLabel);

                        // Y-axis grids run parallel to X-axis (horizontal lines)
                        XYZ startPoint = new XYZ(
                            Parameters.XExtentMin / 304.8,  // mm to ft
                            yPos / 304.8,
                            Parameters.Elevation / 304.8
                        );

                        XYZ endPoint = new XYZ(
                            Parameters.XExtentMax / 304.8,
                            yPos / 304.8,
                            Parameters.Elevation / 304.8
                        );

                        Line gridLine = Line.CreateBound(startPoint, endPoint);
                        Grid grid = Grid.Create(doc, gridLine);
                        grid.Name = uniqueLabel;

#if REVIT2024_OR_GREATER
                        long gridId = grid.Id.Value;
#else
                        long gridId = grid.Id.IntegerValue;
#endif

                        createdGrids.Add(new GridCreationResult
                        {
                            ElementId = gridId,
                            Name = uniqueLabel,
                            OriginalName = label,
                            WasRenamed = label != uniqueLabel,
                            Axis = "Y",
                            Position = yPos
                        });
                    }

                    RevitMCPCommandSet.Utils.TransactionGuard.EnsureCommitted(trans.Commit());
                }

                int renamedCount = createdGrids.Count(g => g.WasRenamed);
                string message = $"Successfully created {createdGrids.Count} grids " +
                                 $"({Parameters.XCount} X-axis + {Parameters.YCount} Y-axis)";

                if (renamedCount > 0)
                {
                    message += $". {renamedCount} grid(s) were renamed because the name already exists in the model: " +
                               string.Join(", ", createdGrids.Where(g => g.WasRenamed).Select(g => $"'{g.OriginalName}' -> '{g.Name}'"));
                }

                Result = new AIResult<List<GridCreationResult>>
                {
                    Success = true,
                    Message = message,
                    Response = createdGrids
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<GridCreationResult>>
                {
                    Success = false,
                    Message = $"Failed to create grids: {ex.Message}",
                    Response = null
                };
            }
            finally
            {
                _resetEvent.Set(); // Signal completion
            }
        }

        /// <summary>
        /// Generate positions based on count, spacing, and start position
        /// </summary>
        private List<double> GeneratePositions(int count, double spacing, double startPosition)
        {
            List<double> positions = new List<double>();
            for (int i = 0; i < count; i++)
            {
                positions.Add(startPosition + (i * spacing));
            }
            return positions;
        }

        /// <summary>
        /// Generate labels based on count, start label, and naming style
        /// </summary>
        private List<string> GenerateLabels(int count, string startLabel, string namingStyle)
        {
            List<string> labels = new List<string>();
            startLabel = (startLabel ?? "").Trim();

            if (namingStyle == "numeric")
            {
                // Split into prefix + trailing digit run: "T1" -> ("T", "1"), "1" -> ("", "1"), "T" -> ("T", "")
                int digitStart = startLabel.Length;
                while (digitStart > 0 && startLabel[digitStart - 1] >= '0' && startLabel[digitStart - 1] <= '9')
                {
                    digitStart--;
                }
                string prefix = startLabel.Substring(0, digitStart);
                string digits = startLabel.Substring(digitStart);

                int startNum = 1; // Default to 1 if the label has no trailing number
                if (digits.Length > 0 && !int.TryParse(digits, out startNum))
                {
                    startNum = 1;
                }
                // Preserve zero padding, e.g. "01" -> 01, 02, ...
                string format = digits.Length > 1 && digits[0] == '0' ? new string('0', digits.Length) : "0";

                for (int i = 0; i < count; i++)
                {
                    labels.Add(prefix + (startNum + i).ToString(format));
                }
            }
            else // alphabetic
            {
                // The last Latin letter is the incrementing part, everything before it is a prefix:
                // "A" -> A, B, ...; "TA" -> TA, TB, ...
                string prefix = "";
                char startChar = 'A';
                if (startLabel.Length > 0)
                {
                    char last = char.ToUpperInvariant(startLabel[startLabel.Length - 1]);
                    if (last >= 'A' && last <= 'Z')
                    {
                        startChar = last;
                        prefix = startLabel.Substring(0, startLabel.Length - 1);
                    }
                    else
                    {
                        // No Latin letter to increment; keep the whole label as prefix and start at 'A'
                        prefix = startLabel;
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    labels.Add(prefix + GenerateAlphabeticLabel(startChar, i));
                }
            }

            return labels;
        }

        /// <summary>
        /// Generate alphabetic label (A, B, C, ..., Z, AA, AB, ...)
        /// </summary>
        private string GenerateAlphabeticLabel(char startChar, int offset)
        {
            int charIndex = (startChar - 'A') + offset;

            if (charIndex < 26)
            {
                // Single letter (A-Z)
                return ((char)('A' + charIndex)).ToString();
            }
            else
            {
                // Multiple letters (AA, AB, ...)
                string result = "";
                int remaining = charIndex;

                while (remaining >= 0)
                {
                    int mod = remaining % 26;
                    result = ((char)('A' + mod)) + result;
                    remaining = (remaining / 26) - 1;

                    if (remaining < 0) break;
                }

                return result;
            }
        }

        /// <summary>
        /// Get unique grid name by auto-incrementing if duplicate exists
        /// </summary>
        private string GetUniqueGridName(string baseName, HashSet<string> existingNames)
        {
            string candidateName = baseName;
            int counter = 1;

            while (existingNames.Contains(candidateName))
            {
                candidateName = $"{baseName}{counter}";
                counter++;
            }

            return candidateName;
        }

        /// <summary>
        /// Wait for grid creation to complete
        /// </summary>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
        return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// Get handler name
        /// </summary>
        public string GetName()
        {
            return "Create Grid System";
        }
    }

    /// <summary>
    /// Result model for individual grid creation
    /// </summary>
    public class GridCreationResult
    {
        [Newtonsoft.Json.JsonProperty("elementId")]
        public long ElementId { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }

        [Newtonsoft.Json.JsonProperty("originalName")]
        public string OriginalName { get; set; }

        [Newtonsoft.Json.JsonProperty("wasRenamed")]
        public bool WasRenamed { get; set; }

        [Newtonsoft.Json.JsonProperty("axis")]
        public string Axis { get; set; }

        [Newtonsoft.Json.JsonProperty("position")]
        public double Position { get; set; }
    }
}
