using System.Globalization;
using System.Text;
using GeoVisionPro.Domain;

namespace GeoVisionPro.WellLog;

/// <summary>Writes LAS 2.0 (unwrapped) files. Curves are resampled onto the union depth index of the first curve when depth grids differ.</summary>
public static class LasWriter
{
    public static string Write(Well well, IReadOnlyList<LogCurve>? curves = null, double nullValue = -999.25)
    {
        curves ??= well.Curves;
        if (curves.Count == 0) throw new InvalidOperationException("No curves to export.");
        var depths = curves[0].Depths;
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        string du = well.DepthUnit == Core.Units.DepthUnit.Feet ? "F" : "M";
        sb.AppendLine("~Version Information");
        sb.AppendLine(" VERS.                  2.0 : CWLS LOG ASCII STANDARD - VERSION 2.0");
        sb.AppendLine(" WRAP.                   NO : One line per depth step");
        sb.AppendLine("~Well Information");
        double step = depths.Length > 1 ? depths[1] - depths[0] : 0;
        bool regular = depths.Length > 2 && depths.Zip(depths.Skip(1), (a, b) => b - a).All(d => Math.Abs(d - step) < 1e-6 * Math.Max(1, Math.Abs(step)));
        sb.AppendLine(string.Create(ci, $" STRT.{du}  {depths.FirstOrDefault(),14:0.0000} : START DEPTH"));
        sb.AppendLine(string.Create(ci, $" STOP.{du}  {depths.LastOrDefault(),14:0.0000} : STOP DEPTH"));
        sb.AppendLine(string.Create(ci, $" STEP.{du}  {(regular ? step : 0),14:0.0000} : STEP"));
        sb.AppendLine(string.Create(ci, $" NULL.     {nullValue,14:0.00} : NULL VALUE"));
        sb.AppendLine($" WELL.     {well.Name} : WELL");
        if (!string.IsNullOrEmpty(well.Uwi)) sb.AppendLine($" UWI.      {well.Uwi} : UNIQUE WELL ID");
        if (!string.IsNullOrEmpty(well.Field)) sb.AppendLine($" FLD.      {well.Field} : FIELD");
        if (!string.IsNullOrEmpty(well.Operator)) sb.AppendLine($" COMP.     {well.Operator} : COMPANY");
        sb.AppendLine(string.Create(ci, $" XCOORD.{du} {well.SurfaceX:0.00} : SURFACE X"));
        sb.AppendLine(string.Create(ci, $" YCOORD.{du} {well.SurfaceY:0.00} : SURFACE Y"));
        sb.AppendLine(string.Create(ci, $" EKB.{du}    {well.KellyBushing:0.00} : KELLY BUSHING ELEVATION"));
        sb.AppendLine(" SRVC.     GeoVision Pro : EXPORTED BY");
        sb.AppendLine("~Curve Information");
        sb.AppendLine($" DEPT.{du}                 : MEASURED DEPTH");
        foreach (var c in curves)
            sb.AppendLine($" {c.Mnemonic}.{c.Unit.Replace(' ', '_')}                 : {c.Description ?? c.Family.ToString()}");
        sb.AppendLine("~ASCII");
        for (int i = 0; i < depths.Length; i++)
        {
            sb.Append(depths[i].ToString("0.0000", ci).PadLeft(12));
            foreach (var c in curves)
            {
                double v = ReferenceEquals(c.Depths, depths) || (c.Depths.Length == depths.Length && c.Depths[0] == depths[0] && c.Depths[^1] == depths[^1])
                    ? c.Values[i] : c.ValueAtDepth(depths[i]);
                sb.Append(' ');
                sb.Append((double.IsNaN(v) ? nullValue : v).ToString("0.000000", ci).PadLeft(14));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static void WriteFile(string path, Well well, IReadOnlyList<LogCurve>? curves = null) => File.WriteAllText(path, Write(well, curves));
}
