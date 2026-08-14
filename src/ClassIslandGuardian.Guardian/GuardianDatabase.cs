using System.Runtime.InteropServices;
using System.Text;
using ClassIslandGuardian.Common;

namespace ClassIslandGuardian.Guardian;

public sealed partial class GuardianDatabase
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadWrite = 0x00000002;
    private const int SqliteOpenCreate = 0x00000004;
    private const int SqliteTransient = -1;

    private readonly GuardianPaths _paths;
    private readonly FileLog _log;
    private readonly string _dataDirectory;

    public GuardianDatabase(GuardianPaths paths, FileLog log, string? dataDirectory = null)
    {
        _paths = paths;
        _log = log;
        _dataDirectory = dataDirectory ?? paths.GuardianDataDirectory;
    }

    public string DatabasePath => Path.Combine(_dataDirectory, "guardian_config.db");

    public bool TryRead(out GuardianConfiguration configuration)
    {
        configuration = new GuardianConfiguration(string.Empty, GuardianConfiguration.DefaultProcessName, GuardianConfiguration.DefaultLauncherName, string.Empty);
        if (!File.Exists(DatabasePath))
        {
            return false;
        }

        try
        {
            using var database = Open(DatabasePath, create: false);
            var path = ReadSingleText(database.Handle, "SELECT classisland_path FROM paths WHERE id=1") ?? string.Empty;
            var processName = ReadSingleText(database.Handle, "SELECT classisland_process_name FROM paths WHERE id=1") ?? GuardianConfiguration.DefaultProcessName;
            var launcherName = ReadSingleText(database.Handle, "SELECT classisland_launcher_name FROM paths WHERE id=1") ?? GuardianConfiguration.DefaultLauncherName;
            var password = ReadSingleText(database.Handle, "SELECT password FROM config WHERE id=1") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            configuration = new GuardianConfiguration(path, processName, launcherName, password);
            return true;
        }
        catch (Exception exception)
        {
            _log.Error("读取配置失败", exception);
            return false;
        }
    }

    public void CreateOrReplace(GuardianConfiguration configuration)
    {
        Directory.CreateDirectory(_dataDirectory);
        using var database = Open(DatabasePath, create: true);
        Execute(database.Handle, "CREATE TABLE IF NOT EXISTS paths(id INTEGER PRIMARY KEY, classisland_path TEXT, classisland_process_name TEXT DEFAULT 'ClassIsland.Desktop.exe', classisland_launcher_name TEXT DEFAULT 'ClassIsland.exe');");
        Execute(database.Handle, "CREATE TABLE IF NOT EXISTS config(id INTEGER PRIMARY KEY, password TEXT);");
        ExecuteBound(database.Handle,
            "INSERT OR REPLACE INTO paths (id, classisland_path, classisland_process_name, classisland_launcher_name) VALUES (1, ?, ?, ?);",
            configuration.ClassIslandPath,
            configuration.ClassIslandProcessName,
            configuration.ClassIslandLauncherName);
        ExecuteBound(database.Handle,
            "INSERT OR REPLACE INTO config (id, password) VALUES (1, ?);",
            configuration.PasswordHash);
    }

    private static SqliteDatabase Open(string path, bool create)
    {
        var flags = SqliteOpenReadWrite | (create ? SqliteOpenCreate : 0);
        var code = sqlite3_open_v2(path, out var handle, flags, IntPtr.Zero);
        if (code != SqliteOk || handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not open SQLite database '{path}', code {code}.");
        }

        return new SqliteDatabase(handle);
    }

    private static string? ReadSingleText(IntPtr database, string sql)
    {
        using var statement = Prepare(database, sql);
        var code = sqlite3_step(statement.Handle);
        if (code == SqliteDone)
        {
            return null;
        }
        if (code != SqliteRow)
        {
            ThrowSqlite(database, code);
        }

        var value = sqlite3_column_text(statement.Handle, 0);
        return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
    }

    private static void Execute(IntPtr database, string sql)
    {
        using var statement = Prepare(database, sql);
        var code = sqlite3_step(statement.Handle);
        if (code != SqliteDone)
        {
            ThrowSqlite(database, code);
        }
    }

    private static void ExecuteBound(IntPtr database, string sql, params string[] values)
    {
        using var statement = Prepare(database, sql);
        for (var index = 0; index < values.Length; index++)
        {
            var code = sqlite3_bind_text(statement.Handle, index + 1, values[index], -1, new IntPtr(SqliteTransient));
            if (code != SqliteOk)
            {
                ThrowSqlite(database, code);
            }
        }

        var result = sqlite3_step(statement.Handle);
        if (result != SqliteDone)
        {
            ThrowSqlite(database, result);
        }
    }

    private static SqliteStatement Prepare(IntPtr database, string sql)
    {
        var code = sqlite3_prepare_v2(database, sql, -1, out var statement, out _);
        if (code != SqliteOk || statement == IntPtr.Zero)
        {
            ThrowSqlite(database, code);
        }

        return new SqliteStatement(statement);
    }

    private static void ThrowSqlite(IntPtr database, int code)
    {
        var message = Marshal.PtrToStringUTF8(sqlite3_errmsg(database)) ?? "Unknown SQLite error";
        throw new InvalidOperationException($"SQLite error {code}: {message}");
    }

    private sealed class SqliteDatabase : IDisposable
    {
        public SqliteDatabase(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
        public void Dispose() => sqlite3_close_v2(Handle);
    }

    private sealed class SqliteStatement : IDisposable
    {
        public SqliteStatement(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
        public void Dispose() => sqlite3_finalize(Handle);
    }

    [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_open_v2(string filename, out IntPtr database, int flags, IntPtr vfs);

    [LibraryImport("winsqlite3.dll")]
    private static partial int sqlite3_close_v2(IntPtr database);

    [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_prepare_v2(IntPtr database, string sql, int length, out IntPtr statement, out IntPtr tail);

    [LibraryImport("winsqlite3.dll")]
    private static partial int sqlite3_step(IntPtr statement);

    [LibraryImport("winsqlite3.dll")]
    private static partial int sqlite3_finalize(IntPtr statement);

    [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_bind_text(IntPtr statement, int index, string value, int length, IntPtr destructor);

    [LibraryImport("winsqlite3.dll")]
    private static partial IntPtr sqlite3_column_text(IntPtr statement, int column);

    [LibraryImport("winsqlite3.dll")]
    private static partial IntPtr sqlite3_errmsg(IntPtr database);
}
