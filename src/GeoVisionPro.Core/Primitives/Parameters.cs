using System.Globalization;
using System.Text.Json;
using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.Core.Parameters;

public enum ParameterKind { Number, Integer, Boolean, Text, Choice }

/// <summary>Describes one user-configurable parameter of an algorithm (used to generate UI and validate input).</summary>
public sealed record ParameterDescriptor(
    string Key,
    string Label,
    ParameterKind Kind,
    string Default,
    string Unit = "",
    double Min = double.NegativeInfinity,
    double Max = double.PositiveInfinity,
    string[]? Choices = null,
    string Description = "");

/// <summary>Parameter values keyed by descriptor key, stored as invariant-culture strings for reproducible serialization.</summary>
public sealed class ParameterSet : Dictionary<string, string>
{
    public ParameterSet() : base(StringComparer.OrdinalIgnoreCase) { }
    public ParameterSet(IDictionary<string, string> values) : base(values, StringComparer.OrdinalIgnoreCase) { }

    public static ParameterSet Defaults(IEnumerable<ParameterDescriptor> descriptors)
    {
        var p = new ParameterSet();
        foreach (var d in descriptors) p[d.Key] = d.Default;
        return p;
    }

    public double GetDouble(string key, double fallback = double.NaN) =>
        TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public int GetInt(string key, int fallback = 0) =>
        TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public bool GetBool(string key, bool fallback = false) =>
        TryGetValue(key, out var s) && bool.TryParse(s, out var v) ? v : fallback;
    public string GetString(string key, string fallback = "") => TryGetValue(key, out var s) ? s : fallback;

    public ParameterSet Set(string key, double value) { this[key] = value.ToString("R", CultureInfo.InvariantCulture); return this; }
    public ParameterSet Set(string key, int value) { this[key] = value.ToString(CultureInfo.InvariantCulture); return this; }
    public ParameterSet Set(string key, bool value) { this[key] = value.ToString(); return this; }
    public ParameterSet Set(string key, string value) { this[key] = value; return this; }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, string>(this));
    public static ParameterSet FromJson(string json) => new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);

    /// <summary>Validates values against descriptors (type and range).</summary>
    public ValidationReport Validate(IEnumerable<ParameterDescriptor> descriptors)
    {
        var r = new ValidationReport();
        foreach (var d in descriptors)
        {
            if (!TryGetValue(d.Key, out var s)) { r.Error($"Missing parameter '{d.Label}'."); continue; }
            switch (d.Kind)
            {
                case ParameterKind.Number:
                case ParameterKind.Integer:
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) r.Error($"'{d.Label}' must be numeric.");
                    else if (v < d.Min || v > d.Max) r.Error($"'{d.Label}' = {v} outside [{d.Min}, {d.Max}] {d.Unit}.");
                    break;
                case ParameterKind.Boolean:
                    if (!bool.TryParse(s, out _)) r.Error($"'{d.Label}' must be true/false.");
                    break;
                case ParameterKind.Choice:
                    if (d.Choices is not null && !d.Choices.Contains(s, StringComparer.OrdinalIgnoreCase)) r.Error($"'{d.Label}' must be one of {string.Join(", ", d.Choices)}.");
                    break;
            }
        }
        return r;
    }
}


/// <summary>Factory helpers for parameter descriptors.</summary>
public static class Param
{
    public static ParameterDescriptor Num(string key, string label, double def, string unit = "", double min = double.NegativeInfinity, double max = double.PositiveInfinity, string desc = "") =>
        new(key, label, ParameterKind.Number, def.ToString("R", CultureInfo.InvariantCulture), unit, min, max, null, desc);
    public static ParameterDescriptor Int(string key, string label, int def, string unit = "", double min = double.NegativeInfinity, double max = double.PositiveInfinity, string desc = "") =>
        new(key, label, ParameterKind.Integer, def.ToString(CultureInfo.InvariantCulture), unit, min, max, null, desc);
    public static ParameterDescriptor Bool(string key, string label, bool def, string desc = "") => new(key, label, ParameterKind.Boolean, def.ToString(), Description: desc);
    public static ParameterDescriptor Text(string key, string label, string def, string desc = "") => new(key, label, ParameterKind.Text, def, Description: desc);
    public static ParameterDescriptor Choice(string key, string label, string def, string[] choices, string desc = "") => new(key, label, ParameterKind.Choice, def, Choices: choices, Description: desc);
}
