using System.Text;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Events;

namespace LiveArchiveEncoder.Diagnostics;

/// <summary>
/// v7.9.9: one structured, correlated log per process run, for reproducing and
/// tracing bugs.
///
/// <para>Design constraints, all measured against this codebase:</para>
/// <list type="bullet">
/// <item>Every product type here is a static utility (no Host, no DI container).
/// Changing 663 Console.* call sites to take an ILogger would be invasive and
/// would break the e2e suite, which asserts on their exact text. Instead there
/// are two layers: a Console mirror that forwards <em>byte for byte</em> while
/// recording every line, plus explicit structured events at the ~20 points
/// where something happens that the user never sees.</item>
/// <item>The realtime encode loop paces against a high-resolution timer. No
/// synchronous file I/O may happen on that thread. Producers only do
/// <see cref="ChannelWriter{T}.TryWrite"/>; a single background consumer writes
/// through Serilog. The channel is unbounded so logging can never apply
/// back-pressure to the encoder.</item>
/// <item>Both Microsoft.Extensions.Logging (ILoggerFactory / ILogger) and
/// Serilog are real: the factory is a SerilogLoggerFactory, so a future DI
/// migration can hand out the same logger. Serilog is configured in code, not
/// via XML/JSON binding, so the Release trimming switches stay safe.</item>
/// <item>Every entry carries RunId (one per process) and OperationId (one per
/// Interrupt.BeginOperation), so a CUI session with several encodes can still
/// be untangled.</item>
/// </list>
/// </summary>
public static class AppLog
{
    /// <summary>Short, stable per-process correlation id (pid + 8 hex chars).</summary>
    public static string RunId { get; } =
        $"{Environment.ProcessId:x}{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Full path of the rolling log file, or null when logging is off/unavailable.</summary>
    /// <remarks>
    /// C# 14's <c>field</c> keyword keeps a one-line guard on the setter
    /// without declaring a separate backing field.
    /// </remarks>
    public static string? LogPath
    {
        get => field;
        private set => field = value is null ? null : Path.GetFullPath(value);
    }

    /// <summary>Microsoft.Extensions.Logging factory, kept for future DI use.</summary>
    public static ILoggerFactory? Factory { get; private set; }

    /// <summary>Current operation id (one encode inside a longer CUI session).</summary>
    public static string CurrentOperation => _operation;

    private static readonly Channel<Entry> Queue = Channel.CreateUnbounded<Entry>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private static Serilog.Core.Logger? _serilog;
    private static Microsoft.Extensions.Logging.ILogger? _logger;
    private static Task? _drain;
    private static RateLimiter? _sampler;
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;
    private static int _started;
    private static int _closed;
    private static int _suppressed;
    private static string _operation = "startup";

    private readonly record struct Entry(
        LogLevel Level, string Template, object?[] Args,
        Exception? Exception, string Operation);

    /// <summary>
    /// Initialise logging. Safe to call more than once; never throws (a broken
    /// log directory must not stop an encode). Honours LAE_LOG_DIR,
    /// LAE_LOG_LEVEL, LAE_LOG_MIRROR=0 and LAE_LOG=0.
    /// </summary>
    public static void Init(bool mirrorConsole = true)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        if (Environment.GetEnvironmentVariable("LAE_LOG") == "0") return;

        try
        {
            string dir = LaePaths.LogDirectory;
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "lae-.log");

            _serilog = new LoggerConfiguration()
                .MinimumLevel.Is(ParseLevel())
                .Enrich.WithProperty("RunId", RunId)
                .WriteTo.File(
                    file,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    // The self-update applier briefly runs alongside the parent
                    // process, and a user may open two shells. A shared sink
                    // lets both write the same daily file without an IOException
                    // that would otherwise downgrade one of them to the temp dir.
                    shared: true,
                    outputTemplate:
                    "{Timestamp:o} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Factory = new SerilogLoggerFactory(_serilog, dispose: false);
            _logger = Factory.CreateLogger("LiveArchiveEncoder");
            LogPath = file;

            _sampler = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = 40,
                TokensPerPeriod = 40,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                AutoReplenishment = true,
                QueueLimit = 0,
            });

            _drain = Task.Run(DrainAsync);
        }
        catch (Exception)
        {
            // Fall back to a temp directory; if that fails too, logging is
            // simply unavailable and the program carries on unchanged.
            try
            {
                string fallback = Path.Combine(Path.GetTempPath(), "lae-.log");
                _serilog = new LoggerConfiguration()
                    .MinimumLevel.Is(LogEventLevel.Information)
                    .Enrich.WithProperty("RunId", RunId)
                    .WriteTo.File(fallback, rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 3)
                    .CreateLogger();
                Factory = new SerilogLoggerFactory(_serilog, dispose: false);
                _logger = Factory.CreateLogger("LiveArchiveEncoder");
                LogPath = fallback;
                _drain = Task.Run(DrainAsync);
            }
            catch (Exception) { _logger = null; }
        }

        if (mirrorConsole && _logger is not null
            && Environment.GetEnvironmentVariable(LaePaths.LogMirrorVariable) != "0")
            InstallConsoleMirror();

        Info("run.start args={Args} pid={Pid} os={Os} log={LogPath}",
            Redact(string.Join(' ', Environment.GetCommandLineArgs().Skip(1))),
            Environment.ProcessId,
            OperatingSystem.IsWindows() ? "windows" : "linux",
            LogPath ?? "unavailable");
    }

    /// <summary>Mark the beginning of one interactive operation (one encode).</summary>
    public static void BeginOperation(string? name = null)
    {
        _operation = string.IsNullOrEmpty(name)
            ? Guid.NewGuid().ToString("N")[..8]
            : name;
        Info("operation.begin id={OperationId}", _operation);
    }

    /// <summary>Human-readable structured event. Never blocks, never throws.</summary>
    public static void Info(string template, params object?[] args) =>
        Write(LogLevel.Information, template, args, null);

    public static void Debug(string template, params object?[] args) =>
        Write(LogLevel.Debug, template, args, null);

    public static void Warn(string template, params object?[] args) =>
        Write(LogLevel.Warning, template, args, null);

    public static void Error(Exception? exc, string template, params object?[] args) =>
        Write(LogLevel.Error, template, args, exc);

    public static void Error(string template, params object?[] args) =>
        Write(LogLevel.Error, template, args, null);

    /// <summary>
    /// Rate-limited diagnostic channel for high-frequency repeated events
    /// (FFmpeg stderr lines, retry attempts). Key events and UI output are
    /// never throttled -- those go through Info()/Ui() and are always recorded.
    /// </summary>
    public static void Sampled(string template, params object?[] args)
    {
        RateLimiter? limiter = _sampler;
        if (limiter is null) { Debug(template, args); return; }

        using RateLimitLease lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            int dropped = Interlocked.Increment(ref _suppressed);
            if (dropped == 1 || dropped % 100 == 0)
                Info("log.sampled.suppressed count={Count}", dropped);
            return;
        }
        Debug(template, args);
    }

    /// <summary>Record a line the user saw, without adding or changing any output.</summary>
    public static void Ui(string stream, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Write(LogLevel.Information, "ui.{Stream} {Text}", [stream, Redact(text)], null);
    }

    private static void Write(LogLevel level, string template, object?[] args, Exception? exc)
    {
        if (_logger is null || Volatile.Read(ref _closed) != 0) return;
        // Redact credentials from every argument too; tokens must never be
        // stored (SelfUpdate's LAE_UPDATE_TOKEN is documented as never logged).
        for (int i = 0; i < args.Length; i++)
            if (args[i] is string s) args[i] = Redact(s);
        Queue.Writer.TryWrite(new Entry(level, template, args, exc, _operation));
    }

    private static async Task DrainAsync()
    {
        await foreach (Entry entry in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                // Serilog's message template syntax is the same as MEL's, so
                // properties survive the SerilogLoggerFactory bridge. The
                // template is dynamic (callers supply it), so a cached
                // LoggerMessage delegate is not applicable -- CA1848 does not
                // fit this one dynamic sink.
#pragma warning disable CA1848, CA1873, CA2254
                Microsoft.Extensions.Logging.LoggerExtensions.Log(
                    _logger!, entry.Level, default(EventId), entry.Exception,
                    "{RunId}/{OperationId} " + entry.Template,
                    [RunId, entry.Operation, .. entry.Args]);
#pragma warning restore CA1848, CA1873, CA2254
            }
            catch (Exception)
            {
                // A broken sink must never take down the encode.
            }
        }
    }

    private static void InstallConsoleMirror()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(new MirrorWriter(_originalOut, "stdout"));
        Console.SetError(new MirrorWriter(_originalError, "stderr"));
    }

    /// <summary>Drain the queue and restore the console. Called once from Main's finally.</summary>
    public static void Close()
    {
        if (Volatile.Read(ref _closed) != 0) return;

        // Write the terminating event BEFORE closing the queue: Write() drops
        // everything once _closed is set.
        if (_logger is not null)
            Info("run.end suppressed={Suppressed}", Volatile.Read(ref _suppressed));

        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        if (_originalOut is not null) Console.SetOut(_originalOut);
        if (_originalError is not null) Console.SetError(_originalError);

        if (_logger is not null)
        {
            try
            {
                Queue.Writer.TryComplete();
                _drain?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception) { /* best effort */ }
        }

        Factory?.Dispose();
        _serilog?.Dispose();
        _sampler?.Dispose();
    }

    private static LogEventLevel ParseLevel()
    {
        string? raw = Environment.GetEnvironmentVariable(LaePaths.LogLevelVariable);
        return raw?.Trim().ToLowerInvariant() switch
        {
            "verbose" or "trace" or "debug" => LogEventLevel.Verbose,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            _ => LogEventLevel.Information,
        };
    }

    private static string Redact(string value)
    {
        // Tokens and proxy credentials must never reach the log file.
        string? token = Environment.GetEnvironmentVariable("LAE_UPDATE_TOKEN");
        if (!string.IsNullOrEmpty(token)) value = value.Replace(token, "***", StringComparison.Ordinal);

        int scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return value;
        int at = value.IndexOf('@', scheme + 3);
        int slash = value.IndexOf('/', scheme + 3);
        if (at <= 0 || (slash >= 0 && slash < at)) return value;
        int colon = value.IndexOf(':', scheme + 3);
        if (colon < 0 || colon > at) return value;
        return string.Concat(value.AsSpan(0, colon + 1), "***", value.AsSpan(at));
    }

    /// <summary>
    /// Forwards every character unchanged to the original writer while
    /// collecting complete lines (split on CR/LF, so '\r' progress redraws do
    /// not become thousands of one-character entries).
    /// </summary>
    private sealed class MirrorWriter(TextWriter inner, string stream) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            if (value is '\n' or '\r') FlushLine();
            else _line.Append(value);
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            if (value is null) return;
            foreach (char c in value)
            {
                if (c is '\n' or '\r') FlushLine();
                else _line.Append(c);
            }
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            if (value is not null)
                foreach (char c in value)
                {
                    if (c is '\n' or '\r') FlushLine();
                    else _line.Append(c);
                }
            FlushLine();
        }

        public override void WriteLine() { inner.WriteLine(); FlushLine(); }

        public override void Flush() { FlushLine(); inner.Flush(); }

        private void FlushLine()
        {
            if (_line.Length == 0) return;
            string text = _line.ToString();
            _line.Clear();
            Ui(stream, text);
        }

        protected override void Dispose(bool disposing)
        {
            FlushLine();
            base.Dispose(disposing);
        }
    }
}
