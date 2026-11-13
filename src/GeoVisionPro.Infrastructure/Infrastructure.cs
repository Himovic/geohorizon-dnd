using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeoVisionPro.Application;
using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.DataAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GeoVisionPro.Infrastructure;

/// <summary>User settings persisted as JSON in %APPDATA%/GeoVisionPro/settings.json (or $XDG_CONFIG_HOME).</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public List<string> RecentProjects { get; set; } = [];
    public string DefaultColorMap { get; set; } = "Seismic (red-white-black)";
    public double DefaultClipPercentile { get; set; } = 99;
    public long VolumeCacheMegabytes { get; set; } = 512;
    public int MaxWorkerThreads { get; set; } = Environment.ProcessorCount;
    public bool UseNativeEngine { get; set; } = true;
    public bool UseVtkRenderer { get; set; } = true;
    public string? LastImportFolder { get; set; }

    public static string Directory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Path.GetTempPath(), "GeoVisionPro");
            var d = Path.Combine(root, "GeoVisionPro");
            System.IO.Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings() : new AppSettings(); }
        catch (JsonException) { return new AppSettings(); }
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    public void AddRecent(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 12) RecentProjects.RemoveRange(12, RecentProjects.Count - 12);
    }
}

/// <summary>Structured, rolling text-file logger (one file per day, kept for 14 days).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _sync = new();
    public FileLoggerProvider(string directory)
    {
        _dir = directory;
        System.IO.Directory.CreateDirectory(_dir);
        foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "geovision-*.log"))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) try { File.Delete(f); } catch (IOException) { }
    }
    public string CurrentFile => Path.Combine(_dir, $"geovision-{DateTime.Now:yyyyMMdd}.log");
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    internal void Write(string line) { lock (_sync) File.AppendAllText(CurrentFile, line + Environment.NewLine); }
    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
        }
    }
}

public enum LicenseEdition { Community, Professional, Enterprise }

/// <summary>License payload (signed JSON).</summary>
public sealed record LicenseInfo(string Licensee, LicenseEdition Edition, DateTime Expires, string[] Modules, string MachineId);

/// <summary>
/// License validation architecture: a license file contains a JSON payload and an RSA-SHA256 signature (base64), verified
/// against the vendor public key embedded in the application. Without a valid license the Community edition applies.
/// Module gating is advisory in this build (all modules enabled) until commercial packaging is finalised.
/// </summary>
public sealed class LicenseService
{
    private readonly string? _publicKeyPem;
    public LicenseInfo Current { get; private set; } = new("Community user", LicenseEdition.Community, DateTime.MaxValue, ["*"], "*");
    public string? LastError { get; private set; }

    public LicenseService(string? vendorPublicKeyPem = null) => _publicKeyPem = vendorPublicKeyPem;

    public static string MachineId()
    {
        var raw = $"{Environment.MachineName}|{Environment.ProcessorCount}|{Environment.OSVersion.Platform}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    /// <summary>Validates "payload-base64.signature-base64" license text.</summary>
    public bool TryActivate(string licenseText)
    {
        LastError = null;
        try
        {
            if (_publicKeyPem is null) { LastError = "No vendor public key configured in this build."; return false; }
            var parts = licenseText.Trim().Split('.');
            if (parts.Length != 2) { LastError = "Malformed license."; return false; }
            var payload = Convert.FromBase64String(parts[0]);
            var sig = Convert.FromBase64String(parts[1]);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_publicKeyPem);
            if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) { LastError = "Invalid license signature."; return false; }
            var info = JsonSerializer.Deserialize<LicenseInfo>(payload) ?? throw new InvalidDataException("Empty license payload.");
            if (info.Expires < DateTime.UtcNow) { LastError = "License expired."; return false; }
            if (info.MachineId != "*" && info.MachineId != MachineId()) { LastError = "License is bound to another machine."; return false; }
            Current = info;
            return true;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException or InvalidDataException)
        {
            LastError = e.Message;
            return false;
        }
    }

    public bool IsModuleEnabled(string module) => Current.Modules.Contains("*") || Current.Modules.Contains(module, StringComparer.OrdinalIgnoreCase);
}

public static class InfrastructureRegistration
{
    public static IServiceCollection AddGeoVisionInfrastructure(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.AddSingleton(new LicenseService());
        services.AddGeoVisionApplication();
        Seismic.Storage.ZarrVolume.DefaultCacheBytes = Math.Max(64, settings.VolumeCacheMegabytes) * 1024 * 1024;
        SeismicProcessing.ProcessingEngine.MaxDegreeOfParallelism = Math.Max(1, settings.MaxWorkerThreads);
        if (!settings.UseNativeEngine) Environment.SetEnvironmentVariable("GEOVISION_DISABLE_NATIVE", "1");
        return services;
    }
}
