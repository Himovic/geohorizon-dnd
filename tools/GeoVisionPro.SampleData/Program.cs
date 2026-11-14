using System.Diagnostics;
using System.Globalization;
using GeoVisionPro.Application.Samples;

// Usage: GeoVisionPro.SampleData <output-folder> [--inlines N] [--crosslines N] [--samples N] [--noise F] [--seed N]
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("GeoVision Pro synthetic sample data generator");
    Console.WriteLine("usage: GeoVisionPro.SampleData <output-folder> [--inlines 60] [--crosslines 80] [--samples 376] [--noise 0.02] [--seed 42]");
    return 1;
}
int Arg(string name, int def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : def; }
double ArgD(string name, double def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], CultureInfo.InvariantCulture) : def; }
var gen = new SyntheticSurveyGenerator
{
    Inlines = Arg("--inlines", 60), Crosslines = Arg("--crosslines", 80), Samples = Arg("--samples", 376),
    NoiseLevel = ArgD("--noise", 0.02), Seed = Arg("--seed", 42)
};
var sw = Stopwatch.StartNew();
var files = gen.WriteSampleFiles(args[0]);
foreach (var f in files) Console.WriteLine($"{new FileInfo(f).Length,12:N0}  {f}");
Console.WriteLine($"Generated {files.Count} files in {sw.Elapsed.TotalSeconds:0.0} s.");
return 0;
