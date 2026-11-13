using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Domain;
using Microsoft.EntityFrameworkCore;

namespace GeoVisionPro.DataAccess;

/// <summary>
/// SQLite implementation of <see cref="IProjectRepository"/>. Saving writes the full snapshot in one transaction
/// (rows are replaced), which keeps the database exactly consistent with the in-memory project.
/// A backup copy of the previous database (&lt;file&gt;.bak) is kept for error recovery.
/// </summary>
public sealed class SqliteProjectRepository : IProjectRepository
{
    public async Task CreateAsync(string projectFile, ProjectSnapshot snapshot, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectFile));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(projectFile)) File.Delete(projectFile);
        await using (var db = new GeoVisionDbContext(projectFile))
        {
            await db.Database.EnsureCreatedAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version = {GeoVisionDbContext.SchemaVersion};", ct);
        }
        await SaveAsync(projectFile, snapshot, ct);
    }

    public async Task<ProjectSnapshot> LoadAsync(string projectFile, CancellationToken ct = default)
    {
        if (!File.Exists(projectFile)) throw new FileNotFoundException("Project file not found.", projectFile);
        await using var db = new GeoVisionDbContext(projectFile);
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            if (version > GeoVisionDbContext.SchemaVersion)
                throw new InvalidDataException($"Project schema version {version} is newer than this application supports ({GeoVisionDbContext.SchemaVersion}).");
        }
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(ct) ?? throw new InvalidDataException("The project database contains no project record.");
        var wells = await db.Wells.AsNoTracking().Include(w => w.Curves).Include(w => w.Tops).Include(w => w.Checkshots).AsSplitQuery().ToListAsync(ct);
        foreach (var w in wells)
        {
            w.Curves = w.Curves.OrderBy(c => c.CreatedUtc).ToList();
            w.Tops = w.Tops.OrderBy(t => t.MeasuredDepth).ToList();
        }
        return new ProjectSnapshot
        {
            Project = project,
            Seismic = await db.Seismic.AsNoTracking().OrderBy(s => s.CreatedUtc).ToListAsync(ct),
            Wells = wells.OrderBy(w => w.Name).ToList(),
            Horizons = await db.Horizons.AsNoTracking().OrderBy(h => h.CreatedUtc).ToListAsync(ct),
            Faults = await db.Faults.AsNoTracking().OrderBy(f => f.CreatedUtc).ToListAsync(ct),
            Wavelets = await db.Wavelets.AsNoTracking().ToListAsync(ct),
            WellTies = await db.WellTies.AsNoTracking().ToListAsync(ct),
            History = await db.History.AsNoTracking().OrderBy(h => h.StartedUtc).ToListAsync(ct),
            Workflows = await db.Workflows.AsNoTracking().ToListAsync(ct),
            Reports = await db.Reports.AsNoTracking().ToListAsync(ct),
            Models = await db.Models.AsNoTracking().ToListAsync(ct),
            Crossplots = await db.Crossplots.AsNoTracking().ToListAsync(ct),
        };
    }

    public async Task SaveAsync(string projectFile, ProjectSnapshot s, CancellationToken ct = default)
    {
        if (File.Exists(projectFile))
        {
            try { File.Copy(projectFile, projectFile + ".bak", true); } catch (IOException) { /* backup is best-effort */ }
        }
        await using var db = new GeoVisionDbContext(projectFile);
        await db.Database.EnsureCreatedAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // replace all rows (children first because of foreign keys)
        await db.Curves.ExecuteDeleteAsync(ct);
        await db.Tops.ExecuteDeleteAsync(ct);
        await db.Checkshots.ExecuteDeleteAsync(ct);
        await db.Wells.ExecuteDeleteAsync(ct);
        await db.Seismic.ExecuteDeleteAsync(ct);
        await db.Horizons.ExecuteDeleteAsync(ct);
        await db.Faults.ExecuteDeleteAsync(ct);
        await db.Wavelets.ExecuteDeleteAsync(ct);
        await db.WellTies.ExecuteDeleteAsync(ct);
        await db.History.ExecuteDeleteAsync(ct);
        await db.Workflows.ExecuteDeleteAsync(ct);
        await db.Reports.ExecuteDeleteAsync(ct);
        await db.Models.ExecuteDeleteAsync(ct);
        await db.Crossplots.ExecuteDeleteAsync(ct);
        await db.Projects.ExecuteDeleteAsync(ct);

        db.Projects.Add(s.Project);
        db.Seismic.AddRange(s.Seismic);
        foreach (var w in s.Wells)
        {
            foreach (var c in w.Curves) c.WellId = w.Id;
            foreach (var t in w.Tops) t.WellId = w.Id;
            foreach (var c in w.Checkshots) c.WellId = w.Id;
        }
        db.Wells.AddRange(s.Wells);
        db.Horizons.AddRange(s.Horizons);
        db.Faults.AddRange(s.Faults);
        db.Wavelets.AddRange(s.Wavelets);
        db.WellTies.AddRange(s.WellTies);
        db.History.AddRange(s.History);
        db.Workflows.AddRange(s.Workflows);
        db.Reports.AddRange(s.Reports);
        db.Models.AddRange(s.Models);
        db.Crossplots.AddRange(s.Crossplots);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }
}
