using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Reads and writes parameter values in the project's display units, the form
    /// AsValueString() shows to users and exports put in spreadsheets.
    /// Parameter.Set(double) takes internal units (feet, radians), so writing a
    /// display string through it scales lengths by 304.8 on a metric project.
    /// </summary>
    public static class DisplayValueParameters
    {
        public static string Read(Parameter param)
        {
            if (param == null || !param.HasValue) return "";
            return param.StorageType == StorageType.String
                ? param.AsString() ?? ""
                : param.AsValueString() ?? param.AsString() ?? "";
        }

        /// <summary>Returns null on success, otherwise the reason the value was not written.</summary>
        public static string Write(Parameter param, string displayValue)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.Set(displayValue) ? null : "Revit rejected the value";
                case StorageType.Double:
                case StorageType.Integer:
                    if (param.StorageType == StorageType.Integer && int.TryParse(displayValue, out int intVal))
                        return param.Set(intVal) ? null : "Revit rejected the value";
                    try
                    {
                        return param.SetValueString(displayValue)
                            ? null
                            : $"'{displayValue}' is not a valid value in the project's display units";
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                default:
                    return $"{param.StorageType} parameters are not supported";
            }
        }
    }
}
