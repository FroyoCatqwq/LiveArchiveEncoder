using System.Collections.Concurrent;
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
/// <item>v7.9.9.1: the file threshold is <b>Error by default</b> -- an ordinary
/// run should not grow a log. Structured events are still collected in a
/// 200-entry in-memory ring, and the first error of a run flushes that ring
/// into the file, so a bug report has context without paying for it every time.
/// LAE_LOG_LEVEL=verbose restores the full trace (including the UI mirror).</item>
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
    /// v7.9.9.1: the file no longer carries a date suffix (rolling is by size),
    /// so this path is the file the user will actually find. It used to report
    /// <c>lae-.log</c>, which Serilog expanded to <c>lae-YYYYMMDD.log</c> and
    /// which therefore never existed.
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
    private static ContextSink? _context;
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;
    private static int _started;
    private static int _closing;
    private static int _closed;
    private static int _flushing;
    private static int _suppressed;
    private static string _operation = "startup";

    /// <summary>
    /// Threshold the file sink uses. Error by default; the ring sink always
    /// sees Verbose so context exists when an error is finally written.
    /// </summary>
    private static LogEventLevel _fileLevel = LogEventLevel.Error;

    private readonly record struct Entry(
        LogLevel Level, string Template, object?[] Args,
        string? ExceptionText, string Operation);

    /// <summary>
    /// Initialise logging. Safe to call more than once; never throws (a broken
    /// log directory must not stop an encode). Honours LAE_LOG_DIR,
    /// LAE_LOG_LEVEL, LAE_LOG_MIRROR=0 and LAE_LOG=0.
    /// </summary>
    public static void Init(bool mirrorConsole = true)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        if (Environment.GetEnvironmentVariable("LAE_LOG") == "0") return;

        _fileLevel = ParseLevel();

        try
        {
            string dir = LaePaths.LogDirectory;
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "lae.log");
            _context = new ContextSink();

            _serilog = Build(file);
            Factory = new SerilogLoggerFactory(_serilog, dispose: false);
            _logger = Factory.CreateLogger("LiveArchiveEncoder");
            LogPath = file;
            _sampler = NewSampler();
            _drain = Task.Run(DrainAsync);
        }
        catch (Exception)
        {
            // v7.9.9.3: tear down the half-built logger first. Build() reads
            // the static _context, so reusing it would register the SAME sink
            // on both loggers (double-counted events, a leaked file handle and
            // a sampler that never sees a clean state).
            try { _serilog?.Dispose(); } catch (Exception) { /* best effort */ }
            try { _sampler?.Dispose(); } catch (Exception) { /* best effort */ }
            _serilog = null;
            _sampler = null;
            _logger = null;
            _context = null;

            // Fall back to a temp directory; if that fails too, logging is
            // simply unavailable and the program carries on unchanged.
            try
            {
                string fallback = Path.Combine(Path.GetTempPath(), "lae.log");
                _context ??= new ContextSink();
                _serilog = Build(fallback);
                Factory = new SerilogLoggerFactory(_serilog, dispose: false);
                _logger = Factory.CreateLogger("LiveArchiveEncoder");
                LogPath = fallback;
                _sampler = NewSampler();
                _drain = Task.Run(DrainAsync);
            }
            catch (Exception) { _logger = null; }
        }

        if (mirrorConsole && _logger is not null
            && Environment.GetEnvironmentVariable(LaePaths.LogMirrorVariable) != "0")
            InstallConsoleMirror();

        Info("run.start args={Args} pid={Pid} os={Os} level={Level} log={LogPath}",
            Redact(string.Join(' ', Environment.GetCommandLineArgs().Skip(1))),
            Environment.ProcessId,
            OperatingSystem.IsWindows() ? "windows" : "linux",
            _fileLevel.ToString(),
            LogPath ?? "unavailable");
    }

    private static Serilog.Core.Logger Build(string file) =>
        new LoggerConfiguration()
            // v7.9.9.1: the logger itself stays verbose so the ring sink can
            // collect context; the file sink is the one that filters.
            .MinimumLevel.Verbose()
            .Enrich.WithProperty("RunId", RunId)
            .WriteTo.Sink(_context!, restrictedToMinimumLevel: LogEventLevel.Verbose)
            .WriteTo.File(
                file,
                restrictedToMinimumLevel: _fileLevel,
                // Size rolling keeps LogPath honest: there is no date suffix to
                // guess, so the path reported at startup is the real one.
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: 16 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7,
                // The self-update applier briefly runs alongside the parent
                // process, and a user may open two shells. A shared sink lets
                // both write the same file without an IOException.
                shared: true,
                outputTemplate:
                "{Timestamp:o} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

    private static RateLimiter NewSampler() =>
        new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 40,
            TokensPerPeriod = 40,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = 0,
        });

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

    public static void Error(Exception? exc, string template, params object?[] args)
    {
        Write(LogLevel.Error, template, args, exc);
        FlushContext();
    }

    public static void Error(string template, params object?[] args)
    {
        Write(LogLevel.Error, template, args, null);
        FlushContext();
    }

    /// <summary>
    /// Rate-limited diagnostic channel for high-frequency repeated events
    /// (FFmpeg stderr lines, retry attempts). Key events and UI output are
    /// never throttled.
    /// </summary>
    public static void Sampled(string template, params object?[] args)
    {
        // Below Debug nothing would reach the file anyway; skip the limiter.
        if (_fileLevel > LogEventLevel.Debug) return;

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

    /// <summary>
    /// Record a line the user saw, without adding or changing any output.
    /// v7.9.9.1: only when the threshold is Information or lower. Otherwise the
    /// mirror would pay for a queue write per line (and per \r progress redraw)
    /// for records the file sink is going to drop.
    /// </summary>
    public static void Ui(string stream, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_fileLevel > LogEventLevel.Information) return;
        Write(LogLevel.Information, "ui.{Stream} {Text}", [stream, Redact(text)], null);
    }

    private static void Write(LogLevel level, string template, object?[] args, Exception? exc)
    {
        if (_logger is null || Volatile.Read(ref _closed) != 0) return;
        // Redact credentials from every argument too; tokens must never be
        // stored (SelfUpdate's LAE_UPDATE_TOKEN is documented as never logged).
        for (int i = 0; i < args.Length; i++)
            if (args[i] is string s) args[i] = Redact(s);
        // v7.9.9.1: an exception message can carry a URL or proxy credentials,
        // and Serilog renders {Exception} verbatim. Redact it as one string and
        // log it as a message argument instead.
        string? exceptionText = exc is null ? null : Redact(exc.ToString());
        Queue.Writer.TryWrite(new Entry(level, template, args, exceptionText, _operation));
    }

    /// <summary>
    /// Write the recent in-memory context once, at Error level, so the file
    /// sink (which filters everything below Error by default) still records it.
    /// </summary>
    private static void FlushContext()
    {
        ContextSink? context = _context;
        if (context is null) return;
        if (Interlocked.Exchange(ref _flushing, 1) != 0) return;
        try
        {
            foreach ((LogEventLevel level, string message) in context.Snapshot())
                Write(LogLevel.Error, "ctx[{Level}] {Message}", [level.ToString(), message], null);
        }
        finally { Volatile.Write(ref _flushing, 0); }
    }

    private static async Task DrainAsync()
    {
        await foreach (Entry entry in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                string message = entry.Template;
                object?[] args = entry.Args;
                if (entry.ExceptionText is { Length: > 0 } text)
                {
                    message = entry.Template + Environment.NewLine + "{ExceptionText}";
                    args = [.. entry.Args, text];
                }

                // Serilog's message template syntax is the same as MEL's, so
                // properties survive the SerilogLoggerFactory bridge. The
                // template is dynamic (callers supply it), so a cached
                // LoggerMessage delegate is not applicable -- CA1848 does not
                // fit this one dynamic sink.
#pragma warning disable CA1848, CA1873, CA2254
                Microsoft.Extensions.Logging.LoggerExtensions.Log(
                    _logger!, entry.Level, default(EventId), null,
                    "{RunId}/{OperationId} " + message,
                    [RunId, entry.Operation, .. args]);
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
        // v7.9.9.1: CAS first. Two racing callers used to both pass the check
        // and write run.end twice; correctness should not depend on the caller.
        if (Interlocked.CompareExchange(ref _closing, 1, 0) != 0) return;
        if (Volatile.Read(ref _closed) != 0) return;

        // Write the terminating event BEFORE closing the queue: Write() drops
        // everything once _closed is set.
        if (_logger is not null)
            Info("run.end suppressed={Suppressed}", Volatile.Read(ref _suppressed));

        Interlocked.Exchange(ref _closed, 1);

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
            "info" or "information" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            // v7.9.9.1: an ordinary run must not grow a log. Error is the
            // default; everything above is opt-in.
            _ => LogEventLevel.Error,
        };
    }

    /// <summary>
    /// In-memory ring of recent events. v7.9.9.1: with the file sink at Error,
    /// this is what makes an error report readable -- FlushContext re-emits the
    /// ring at Error level.
    /// </summary>
    private sealed class ContextSink : Serilog.Core.ILogEventSink
    {
        private const int Capacity = 200;
        private readonly ConcurrentQueue<(LogEventLevel Level, string Message)> _items = new();

        public void Emit(LogEvent logEvent)
        {
            // v7.9.9.3: FlushContext writes the ring back through this SAME
            // logger. Without this guard every ctx[...] becomes a fresh ring
            // item, the next error re-emits those copies, and the 200-slot
            // ring fills with echoes of itself instead of real context.
            if (logEvent.MessageTemplate.Text.StartsWith("ctx[", StringComparison.Ordinal))
                return;

            _items.Enqueue((logEvent.Level, logEvent.RenderMessage()));
            while (_items.Count > Capacity) _items.TryDequeue(out _);
        }

        /// <summary>Take everything collected so far, so a second error does not repeat it.</summary>
        public List<(LogEventLevel Level, string Message)> Snapshot()
        {
            var taken = new List<(LogEventLevel, string)>(_items.Count);
            while (_items.TryDequeue(out (LogEventLevel Level, string Message) item))
                taken.Add(item);
            return taken;
        }
    }

    /// <summary>
    /// Forwards console output unchanged and records each line as a structured
    /// event. Writes are buffered until a line terminator so a <c>\r</c>
    /// progress redraw becomes one record instead of one per repaint.
    /// </summary>
    private sealed class MirrorWriter(TextWriter inner, string stream) : TextWriter
    {
        private const int MaxLine = 8 * 1024;
        private readonly StringBuilder _line = new();
        // v7.9.9.5: shard progress, the slot loop and stderr readers all write
        // to the mirrored console concurrently. StringBuilder is not thread
        // safe: interleaved writes produced garbled log lines and, under load,
        // ArgumentOutOfRangeException from a concurrent Append/Clear.
        private readonly object _gate = new();

        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                inner.Write(value);
                if (value is '\n' or '\r') FlushLine();
                else if (_line.Length < MaxLine) _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            lock (_gate)
            {
                inner.Write(value);
                foreach (char c in value)
                {
                    if (c is '\n' or '\r') FlushLine();
                    else if (_line.Length < MaxLine) _line.Append(c);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                inner.WriteLine(value);
                if (value is not null && _line.Length < MaxLine) _line.Append(value);
                FlushLine();
            }
        }

        public override void Flush() => inner.Flush();

        private void FlushLine()
        {
            if (_line.Length == 0) return;
            string text = _line.ToString();
            _line.Clear();
            Ui(stream, text);
        }
    }

    private static string Redact(string value)
    {
        // Never let the update token or a URL's userinfo reach the log.
        string? token = Environment.GetEnvironmentVariable("LAE_UPDATE_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            // v7.9.9.3: SelfUpdate trims the token before use. A value with
            // surrounding spaces used to leave the actually-sent credential
            // ("abc") unredacted in URLs and exception text.
            string trimmed = token.Trim();
            value = value.Replace(token, "***", StringComparison.Ordinal);
            if (trimmed.Length > 0)
            {
                if (!string.Equals(trimmed, token, StringComparison.Ordinal))
                    value = value.Replace(trimmed, "***", StringComparison.Ordinal);
                value = value.Replace(Uri.EscapeDataString(trimmed), "***",
                    StringComparison.Ordinal);
            }
        }

        // v7.9.9.2: userinfo may have no scheme at all ("--proxy
        // user:pass@host:8080"), and a URL may contain several '@'. Use the
        // last one and only redact when the segment before it looks like
        // credentials (contains ':'), so plain "name@host" is left readable.
        int at = value.LastIndexOf('@');
        if (at <= 0) return value;

        int schemeEnd = 0;
        int scheme = value.LastIndexOf("://", at, StringComparison.Ordinal);
        if (scheme >= 0) schemeEnd = scheme + 3;
        if (!value[schemeEnd..at].Contains(':', StringComparison.Ordinal)) return value;
        return value[..schemeEnd] + "***" + value[at..];
    }
}
