using System.Text.Json;
using LMS.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Setup;

/// <summary>The databases the LMS can run on, selectable at first-run deployment.</summary>
public enum DbProvider { Sqlite = 0, SqlServer = 1, PostgreSql = 2, MySql = 3 }

/// <summary>Mutable, process-wide holder of the active database configuration. The DbContext
/// registration reads it each time a context is built, so the first-run wizard can apply a
/// chosen provider without a restart.</summary>
public class DbSettings
{
    public DbProvider Provider { get; set; } = DbProvider.Sqlite;
    public string ConnectionString { get; set; } = "";
    public bool Configured { get; set; }
    public ServerVersion? MySqlVersion { get; set; }   // cached for Pomelo (avoid re-detecting per context)
}

/// <summary>Whether the app is running in first-run setup mode, and where the choice is persisted.</summary>
public class SetupState
{
    /// <summary>True until the installation wizard has been completed.</summary>
    public bool SetupMode { get; set; }
    public string SetupFilePath { get; set; } = "";
}

/// <summary>Applies the active provider to a DbContextOptionsBuilder.</summary>
public static class DbProviderConfigurator
{
    public static void Apply(DbContextOptionsBuilder options, DbSettings s)
    {
        switch (s.Provider)
        {
            case DbProvider.SqlServer:
                options.UseSqlServer(s.ConnectionString, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
                break;
            case DbProvider.PostgreSql:
                options.UseNpgsql(s.ConnectionString, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
                break;
            case DbProvider.MySql:
                s.MySqlVersion ??= ServerVersion.AutoDetect(s.ConnectionString);
                options.UseMySql(s.ConnectionString, s.MySqlVersion, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
                break;
            default:
                options.UseSqlite(s.ConnectionString, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
                break;
        }
    }
}

/// <summary>Reads/writes the persisted database choice (a small JSON file on a writable,
/// volume-backed path) and derives an initial configuration from environment/appsettings for
/// headless deployments that want to skip the wizard.</summary>
public static class SetupStore
{
    public record Persisted(string Provider, string ConnectionString, bool Completed = false, string? OllamaUrl = null);

    public static Persisted? LoadFile(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public static void SaveFile(string path, Persisted p) =>
        File.WriteAllText(path, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));

    public static string FilePath(IConfiguration cfg, IWebHostEnvironment env)
    {
        var dir = cfg["LMS_SETUP_DIR"] ?? Environment.GetEnvironmentVariable("LMS_SETUP_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Directory.Exists("/data") ? "/data" : Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "db-setup.json");
    }

    public static DbSettings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path));
            if (p == null || string.IsNullOrWhiteSpace(p.ConnectionString)) return null;
            return new DbSettings { Provider = ParseProvider(p.Provider), ConnectionString = p.ConnectionString, Configured = true };
        }
        catch { return null; }
    }

    public static void Save(string path, DbProvider provider, string connectionString,
        bool completed = false, string? ollamaUrl = null) =>
        SaveFile(path, new Persisted(provider.ToString(), connectionString, completed, ollamaUrl));

    /// <summary>Headless escape hatch: an explicit `DatabaseProvider` in env/appsettings skips
    /// the wizard (used by automated/CI deployments and the Development environment).</summary>
    public static DbSettings? FromConfig(IConfiguration cfg)
    {
        var pv = cfg["DatabaseProvider"];
        if (string.IsNullOrWhiteSpace(pv)) return null;
        var provider = ParseProvider(pv);
        var conn = cfg.GetConnectionString(provider.ToString()) ?? cfg.GetConnectionString(pv) ?? "";
        if (provider == DbProvider.Sqlite && string.IsNullOrWhiteSpace(conn)) conn = "Data Source=lms.db";
        return string.IsNullOrWhiteSpace(conn) ? null
            : new DbSettings { Provider = provider, ConnectionString = conn, Configured = true };
    }

    public static DbProvider ParseProvider(string s) => s?.Trim().ToLowerInvariant() switch
    {
        "sqlserver" or "mssql" or "microsoft sql server" => DbProvider.SqlServer,
        "postgresql" or "postgres" or "npgsql" => DbProvider.PostgreSql,
        "mysql" or "mariadb" => DbProvider.MySql,
        _ => DbProvider.Sqlite
    };
}

/// <summary>Builds a connection string from the wizard's individual fields.</summary>
public static class ConnStringFactory
{
    public static string Build(DbProvider p, string? host, string? port, string? database, string? user, string? pass, string? sqlitePath) => p switch
    {
        DbProvider.SqlServer => $"Server={host}{(string.IsNullOrWhiteSpace(port) ? "" : "," + port)};Database={database};User Id={user};Password={pass};TrustServerCertificate=True;MultipleActiveResultSets=true",
        DbProvider.PostgreSql => $"Host={host};Port={(string.IsNullOrWhiteSpace(port) ? "5432" : port)};Database={database};Username={user};Password={pass}",
        DbProvider.MySql => $"Server={host};Port={(string.IsNullOrWhiteSpace(port) ? "3306" : port)};Database={database};User={user};Password={pass};",
        _ => $"Data Source={(string.IsNullOrWhiteSpace(sqlitePath) ? (Directory.Exists("/data") ? "/data/lms.db" : "lms.db") : sqlitePath)}"
    };
}

/// <summary>Opens a raw ADO.NET connection to validate the chosen database before committing.</summary>
public static class ConnectionTester
{
    public static async Task<(bool ok, string message)> TestAsync(DbProvider p, string conn)
    {
        try
        {
            System.Data.Common.DbConnection c = p switch
            {
                DbProvider.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(conn),
                DbProvider.PostgreSql => new Npgsql.NpgsqlConnection(conn),
                DbProvider.MySql => new MySqlConnector.MySqlConnection(conn),
                _ => new Microsoft.Data.Sqlite.SqliteConnection(conn)
            };
            await using (c) { await c.OpenAsync(); }
            return (true, "Connection successful.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>Creates the schema (provider-agnostic EF `EnsureCreated`), applies the additive
/// grading upgrade for existing SQLite/SQL Server databases, and seeds. Shared by startup
/// (when already configured) and the first-run wizard's Save step.</summary>
public static class DbInitializer
{
    /// <summary>Schema only — used by the installation wizard, which seeds separately
    /// (the customer's own organisation, or the demo dataset, per their choice).</summary>
    public static async Task EnsureSchemaAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        await EnsureSchemaCoreAsync(sp, db);
    }

    private static async Task EnsureSchemaCoreAsync(IServiceProvider sp, AppDbContext db)
    {
        var created = db.Database.EnsureCreated();
        if (!created)
        {
            bool rebuild;
            try
            {
                var version = await db.SiteSettings.FindAsync("SeedVersion");
                rebuild = version == null || version.Value != DbSeeder.SeedVersion;
            }
            catch { rebuild = true; }
            if (rebuild) { db.Database.EnsureDeleted(); db.Database.EnsureCreated(); }
        }

        LMS.Web.Services.Grading.SchemaUpgrader.EnsureGradingSchema(
            db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("SchemaUpgrader"));
    }

    /// <summary>Schema + demo seed — the startup path for an already-configured installation.</summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        await EnsureSchemaCoreAsync(sp, sp.GetRequiredService<AppDbContext>());
        await DbSeeder.SeedAsync(sp);
    }
}
