using System.Runtime.InteropServices;
using System.Text.Json;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GeoVisionPro.DataAccess;

/// <summary>
/// SQLite project database. Bulk arrays (log samples, horizon grids, wavelets) are stored as little-endian binary blobs;
/// small nested structures (deviation surveys, checkshot points, fault sticks) as JSON. Seismic sample data is never stored here.
/// </summary>
public sealed class GeoVisionDbContext : DbContext
{
    private readonly string _path;
    public const int SchemaVersion = 1;

    public GeoVisionDbContext(string databasePath) => _path = databasePath;

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<SeismicDataset> Seismic => Set<SeismicDataset>();
    public DbSet<Well> Wells => Set<Well>();
    public DbSet<LogCurve> Curves => Set<LogCurve>();
    public DbSet<WellTop> Tops => Set<WellTop>();
    public DbSet<CheckshotSurvey> Checkshots => Set<CheckshotSurvey>();
    public DbSet<Horizon> Horizons => Set<Horizon>();
    public DbSet<Fault> Faults => Set<Fault>();
    public DbSet<WaveletEntity> Wavelets => Set<WaveletEntity>();
    public DbSet<WellTieRecord> WellTies => Set<WellTieRecord>();
    public DbSet<ProcessingRecord> History => Set<ProcessingRecord>();
    public DbSet<WorkflowDefinitionEntity> Workflows => Set<WorkflowDefinitionEntity>();
    public DbSet<ReportEntity> Reports => Set<ReportEntity>();
    public DbSet<GeologicalModelEntity> Models => Set<GeologicalModelEntity>();
    public DbSet<CrossplotEntity> Crossplots => Set<CrossplotEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={_path};Pooling=False");

    internal static byte[] ToBytes(float[] v) => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
    internal static float[] ToFloats(byte[] b) => MemoryMarshal.Cast<byte, float>(b.AsSpan()).ToArray();
    internal static byte[] ToBytes(double[] v) => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
    internal static double[] ToDoubles(byte[] b) => MemoryMarshal.Cast<byte, double>(b.AsSpan()).ToArray();
    internal static bool SameArray<T>(T[]? a, T[]? b) where T : struct, IEquatable<T> => ReferenceEquals(a, b) || (a != null && b != null && a.AsSpan().SequenceEqual(b));

    private static ValueConverter<float[], byte[]> FloatBlob() => new(v => ToBytes(v), b => ToFloats(b));
    private static ValueConverter<double[], byte[]> DoubleBlob() => new(v => ToBytes(v), b => ToDoubles(b));
    private static ValueComparer<T[]> ArrayComparer<T>() where T : struct, IEquatable<T> => new(
        (a, b) => SameArray(a, b), a => a.Length, a => a.ToArray());

    private static readonly JsonSerializerOptions Json = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private static ValueConverter<List<T>, string> JsonList<T>() => new(v => JsonSerializer.Serialize(v, Json), s => JsonSerializer.Deserialize<List<T>>(s, Json) ?? new List<T>());
    private static ValueComparer<List<T>> ListComparer<T>() => new((a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json), a => a.Count, a => a.ToList());

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>().ToTable("Project");
        b.Entity<SeismicDataset>(e => { e.ToTable("SeismicDatasets"); e.Ignore(x => x.Geometry); e.Ignore(x => x.Offsets); });
        b.Entity<Well>(e =>
        {
            e.ToTable("Wells");
            e.HasMany(x => x.Curves).WithOne().HasForeignKey(c => c.WellId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tops).WithOne().HasForeignKey(c => c.WellId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Checkshots).WithOne().HasForeignKey(c => c.WellId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Deviation).HasConversion(JsonList<DeviationStation>(), ListComparer<DeviationStation>());
            e.Ignore(x => x.ActiveTimeDepth);
        });
        b.Entity<LogCurve>(e =>
        {
            e.ToTable("LogCurves");
            e.Property(x => x.Depths).HasConversion(DoubleBlob(), ArrayComparer<double>());
            e.Property(x => x.Values).HasConversion(FloatBlob(), ArrayComparer<float>());
        });
        b.Entity<WellTop>().ToTable("WellTops");
        b.Entity<CheckshotSurvey>(e =>
        {
            e.ToTable("TimeDepthSurveys");
            e.Property(x => x.Points).HasConversion(JsonList<TimeDepthPoint>(), ListComparer<TimeDepthPoint>());
        });
        b.Entity<Horizon>(e =>
        {
            e.ToTable("Horizons");
            e.Property(x => x.Values).HasConversion(DoubleBlob(), ArrayComparer<double>());
            e.Property(x => x.Sources).HasConversion(v => v, v => v, ArrayComparer<byte>());
        });
        b.Entity<Fault>(e =>
        {
            e.ToTable("Faults");
            e.Property(x => x.Sticks).HasConversion(
                new ValueConverter<List<FaultStick>, string>(
                    v => JsonSerializer.Serialize(v.Select(s => s.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToList()).ToList(), Json),
                    s => (JsonSerializer.Deserialize<List<List<double[]>>>(s, Json) ?? new List<List<double[]>>())
                        .Select(st => new FaultStick { Points = st.Select(p => new Point3D(p[0], p[1], p[2])).ToList() }).ToList()),
                new ValueComparer<List<FaultStick>>((a, c) => ReferenceEquals(a, c), a => a.Count, a => a.ToList()));
        });
        b.Entity<WaveletEntity>(e => { e.ToTable("Wavelets"); e.Property(x => x.Samples).HasConversion(FloatBlob(), ArrayComparer<float>()); });
        b.Entity<WellTieRecord>().ToTable("WellTies");
        b.Entity<ProcessingRecord>().ToTable("ProcessingHistory");
        b.Entity<WorkflowDefinitionEntity>().ToTable("Workflows");
        b.Entity<ReportEntity>().ToTable("Reports");
        b.Entity<GeologicalModelEntity>().ToTable("GeologicalModels");
        b.Entity<CrossplotEntity>().ToTable("Crossplots");
    }
}
