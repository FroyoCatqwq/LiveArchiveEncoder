using System.Globalization;
using Lae.Core.History;
using Microsoft.Data.Sqlite;

namespace Lae.Media.History;

/// <summary>
/// Run history in a SQLite file next to the settings.
///
/// <para><b>Why a database and not a text file.</b> This is the one part of the
/// program's state that only grows, and that gets queried ("what did I use for
/// episode 3", "how many runs failed this week"). Appending to a JSON array means
/// reading and rewriting everything on every run; SQLite appends one row and
/// answers the query without loading the file.</para>
///
/// <para><b>Why not a config file.</b> Configuration is intent and belongs to the
/// person; history is fact and belongs to the program. Keeping them in one place
/// is how you end up with a file nobody dares edit.</para>
///
/// <para><b>Why the failure is returned rather than thrown.</b> A full disk here
/// would otherwise fail an encode that already succeeded. That includes the
/// coarsest failure of all -- the native library not loading at all, which is what
/// a machine with an older glibc than the bundled SQLite was built for does. A
/// history that cannot open is a history that says so, never a failed encode.</para>
/// </summary>
public sealed class SqliteHistoryStore(string path) : IRunHistory
{
    /// <summary>
    /// Set once the table exists. Connections are short-lived on purpose: a
    /// long-lived one would keep the file open across an entire encode, and on
    /// Windows that is enough to make the user's backup tool skip it.
    /// </summary>
    private bool _ready;

    /// <summary>The first reason this store could not answer; sticky on purpose.</summary>
    private string? _problem;

    public string Location => path;

    public string? Problem => _problem;

    public string? Append(RunRecord record)
    {
        if (TryOpen(out string error) is not { } connection) return error;

        using (connection)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO runs (
                    started_at, source, source_bytes, encoder, container, preset, quality, limit_kbps,
                    output, output_bytes, span_seconds, avg_kbps, frames, repeated, dropped,
                    effective_fps, final_rung, result, failure)
                VALUES (
                    $started, $source, $sourceBytes, $encoder, $container, $preset, $quality, $limit,
                    $output, $outputBytes, $spanSeconds, $avgKbps, $frames, $repeated, $dropped,
                    $effectiveFps, $finalRung, $result, $failure);
                """;

            Add(command, "$started", record.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Add(command, "$source", record.SourcePath);
            Add(command, "$sourceBytes", record.SourceBytes);
            Add(command, "$encoder", record.Encoder);
            Add(command, "$container", record.Container);
            Add(command, "$preset", record.PresetId);
            Add(command, "$quality", record.Quality);
            Add(command, "$limit", record.LimitKbps);
            Add(command, "$output", record.OutputPath);
            Add(command, "$outputBytes", record.OutputBytes);
            Add(command, "$spanSeconds", record.SpanSeconds);
            Add(command, "$avgKbps", record.AverageKbps);
            Add(command, "$frames", record.Frames);
            Add(command, "$repeated", record.Repeated);
            Add(command, "$dropped", record.Dropped);
            Add(command, "$effectiveFps", record.EffectiveFps);
            Add(command, "$finalRung", record.FinalRung);
            Add(command, "$result", record.Result);
            Add(command, "$failure", record.Failure);

            try
            {
                command.ExecuteNonQuery();
                return null;
            }
            catch (Exception exc) when (IsEnvironmentFailure(exc))
            {
                return Remember(exc);
            }
        }
    }

    public IReadOnlyList<RunRecord> Recent(int limit)
    {
        if (limit <= 0 || !File.Exists(path)) return [];
        if (TryOpen(out _) is not { } connection) return [];

        using (connection)
        {
            try
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM runs ORDER BY id DESC LIMIT $limit;";
                Add(command, "$limit", limit);

                var records = new List<RunRecord>(Math.Min(limit, 256));
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) records.Add(Read(reader));
                return records;
            }
            catch (Exception exc) when (IsEnvironmentFailure(exc))
            {
                // An unreadable history is not worth failing a command over; the
                // caller shows an empty list and the reason, and the file stays
                // where it is.
                Remember(exc);
                return [];
            }
        }
    }

    public long Count()
    {
        // Reading must not create the file: `lae history list` on a machine that
        // has never encoded anything should print "nothing yet", not a new empty
        // database beside the settings.
        if (!File.Exists(path)) return 0;
        if (TryOpen(out _) is not { } connection) return 0;

        using (connection)
        {
            try
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM runs;";
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            catch (Exception exc) when (IsEnvironmentFailure(exc))
            {
                Remember(exc);
                return 0;
            }
        }
    }

    /// <summary>
    /// Every way this can fail because of the machine rather than the data: a full
    /// disk, a locked file, a path that is a directory -- and the loader's own
    /// verdicts, which is the case this list exists for. <c>SQLitePCL</c>
    /// initialises its provider in a static constructor, so a native library that
    /// is missing, built for another architecture, or built against a newer glibc
    /// arrives as <see cref="TypeInitializationException"/> wrapping
    /// <see cref="DllNotFoundException"/> or <see cref="BadImageFormatException"/>.
    /// </summary>
    private static bool IsEnvironmentFailure(Exception exc)
        => exc is SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException
            or DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or TypeInitializationException;

    private string Remember(Exception exc)
    {
        _problem ??= exc.Message;
        return exc.Message;
    }

    /// <summary>
    /// A connection with the table in place, or null and the reason. Null is the
    /// normal answer on a machine whose SQLite cannot load at all.
    /// </summary>
    private SqliteConnection? TryOpen(out string error)
    {
        error = "";
        SqliteConnection? connection = null;

        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,

                // Read-write-create, and no shared cache: a second terminal running
                // `lae history` while an encode writes must not deadlock the encode.
                Mode = SqliteOpenMode.ReadWriteCreate,

                // No pooling. One row per encode does not need a pool, and a pooled
                // handle keeps the file open after the run is over -- on Windows that
                // is enough to make the user's backup tool skip it, or to make the
                // file impossible to delete until the process exits. Opening SQLite
                // costs microseconds; a locked file costs a support question.
                Pooling = false,
            }.ToString());

            connection.Open();

            using (SqliteCommand pragma = connection.CreateCommand())
            {
                // Wait for a concurrent writer instead of failing immediately. Three
                // seconds is longer than a row insert and shorter than a user's
                // patience.
                pragma.CommandText = "PRAGMA busy_timeout = 3000;";
                pragma.ExecuteNonQuery();
            }

            if (!_ready)
            {
                using SqliteCommand create = connection.CreateCommand();
                create.CommandText = Schema;
                create.ExecuteNonQuery();
                _ready = true;
            }

            return connection;
        }
        catch (Exception exc) when (IsEnvironmentFailure(exc))
        {
            connection?.Dispose();
            error = Remember(exc);
            return null;
        }
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static RunRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        StartedAt = DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal("started_at")),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        SourcePath = reader.GetString(reader.GetOrdinal("source")),
        SourceBytes = Nullable<long>(reader, "source_bytes"),
        Encoder = Text(reader, "encoder"),
        Container = Text(reader, "container"),
        PresetId = Text(reader, "preset"),
        Quality = Nullable<int>(reader, "quality"),
        LimitKbps = Nullable<double>(reader, "limit_kbps"),
        OutputPath = Text(reader, "output"),
        OutputBytes = Nullable<long>(reader, "output_bytes"),
        SpanSeconds = Nullable<double>(reader, "span_seconds"),
        AverageKbps = Nullable<double>(reader, "avg_kbps"),
        Frames = Nullable<long>(reader, "frames"),
        Repeated = Nullable<long>(reader, "repeated"),
        Dropped = Nullable<long>(reader, "dropped"),
        EffectiveFps = Nullable<double>(reader, "effective_fps"),
        FinalRung = Nullable<int>(reader, "final_rung"),
        Result = reader.GetString(reader.GetOrdinal("result")),
        Failure = Text(reader, "failure"),
    };

    private static string? Text(SqliteDataReader reader, string column)
    {
        int index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static T? Nullable<T>(SqliteDataReader reader, string column) where T : struct
    {
        int index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : (T)Convert.ChangeType(reader.GetValue(index), typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// No indexes beyond the primary key yet: the table is written once per encode
    /// and read newest-first, which the rowid order already answers. An index would
    /// be a guess about a query nobody has asked for.
    ///
    /// <para><c>user_version</c> is set so a later change has something to branch
    /// on -- the alternative is guessing the schema from the columns that happen to
    /// exist.</para>
    /// </summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS runs (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at    TEXT    NOT NULL,
            source        TEXT    NOT NULL,
            source_bytes  INTEGER,
            encoder       TEXT,
            container     TEXT,
            preset        TEXT,
            quality       INTEGER,
            limit_kbps    REAL,
            output        TEXT,
            output_bytes  INTEGER,
            span_seconds  REAL,
            avg_kbps      REAL,
            frames        INTEGER,
            repeated      INTEGER,
            dropped       INTEGER,
            effective_fps REAL,
            final_rung    INTEGER,
            result        TEXT    NOT NULL,
            failure       TEXT
        );
        PRAGMA user_version = 1;
        """;
}
