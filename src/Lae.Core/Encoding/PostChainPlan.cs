using Lae.Core.Settings;

namespace Lae.Core;

/// <summary>One hop of the post-encode chain, worked out ahead of time.</summary>
public sealed record PostChainStep
{
    /// <summary>1-based position in the chain; also what the "_web2" suffix counts.</summary>
    public required int Number { get; init; }

    /// <summary>What this hop reads: the live product for the first, the previous hop after that.</summary>
    public required string InputPath { get; init; }

    public required string OutputPath { get; init; }

    /// <summary>Container extension without the dot.</summary>
    public required string Container { get; init; }

    /// <summary>Whether this step runs FFmpeg or a plugin.</summary>
    public required ChainStepKind Kind { get; init; }

    /// <summary>The user's fragment, already split into tokens and checked. FFmpeg hops only.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The plugin's id. Plugin hops only.</summary>
    public string? Plugin { get; init; }

    /// <summary>The plugin's own stage id, for <c>run --stage</c>. Plugin hops only.</summary>
    public string? Stage { get; init; }

    /// <summary>Plugin parameters, still as strings. Plugin hops only.</summary>
    public IReadOnlyDictionary<string, string>? Params { get; init; }

    /// <summary>
    /// The command line, with the program's own ends around the user's middle.
    ///
    /// <para><c>-nostdin</c> is not decoration: without it FFmpeg reads the
    /// terminal's standard input when it is not given a pipe, which in
    /// <c>lae repl</c> means the pass swallows the next few keystrokes. A pass
    /// reading only named inputs has no business on the terminal.</para>
    /// </summary>
    public List<string> Command(string ffmpeg, string outputPath)
    {
        if (Kind != ChainStepKind.Ffmpeg)
            throw new InvalidOperationException("this step does not run FFmpeg");

        return [ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", InputPath, .. Args, outputPath];
    }
}

/// <summary>
/// The post-encode chain: what a video site did to an upload, hop by hop.
///
/// <para>A recording can be published more than once -- a 2007 portal first,
/// then the 2011 one -- and each hop is a real transcode of the previous
/// product, so the chain is a list rather than one fragment. Every intermediate
/// survives: they are each a real artefact of the process, and only the last one
/// is the delivery.</para>
///
/// <para>Since a hop may be a plugin rather than an FFmpeg command, this type
/// plans both and stops caring which. What remains of the difference is in
/// <see cref="PostChainStep.Kind"/>: a fragment has to be split and its codec
/// checked against the container, and a plugin has neither.</para>
///
/// <para>Naming is derived from the <em>live product</em>, never from the hop
/// before: <c>clip.avi</c> gives <c>clip_web.mp4</c>, then <c>clip_web2.webm</c>.
/// Deriving hop 2 from hop 1's name gives <c>clip_web_web2</c>, which says
/// nothing about the order the hops joined the chain in.</para>
///
/// <para>Pure, like <see cref="EncodePlan"/>: everything a bad chain can be --
/// an empty fragment, a step that names both a plugin and a fragment, a codec
/// that cannot be written to the container asked for, a stray output name --
/// comes back in <see cref="Problems"/> rather than as an exception, because the
/// CLI has to answer with a sentence and exit 2.</para>
/// </summary>
public sealed record PostChainPlan
{
    /// <summary>The recording the live pass wrote; never modified by any hop.</summary>
    public required string LivePath { get; init; }

    public required IReadOnlyList<PostChainStep> Steps { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool IsRunnable => Problems.Count == 0;

    /// <summary>True when there is at least one hop to run.</summary>
    public bool Any => Steps.Count > 0;

    /// <summary>The path to play or hand on: the last hop's, or the live product.</summary>
    public string FinalPath => Steps.Count > 0 ? Steps[^1].OutputPath : LivePath;

    /// <summary>
    /// Works out the chain against a live product. Never throws for user input.
    /// An unarmed chain plans nothing and says nothing -- that is the normal case.
    /// </summary>
    public static PostChainPlan Create(
        ChainSettings chain, string livePath, Localization.IMessages messages)
    {
        string live = livePath;
        var problems = new List<string>();
        var notes = new List<string>();

        if (!chain.Enabled) return new PostChainPlan { LivePath = live, Steps = [] };

        IReadOnlyList<ChainStep> steps = chain.Active();

        // Armed with only blank steps: the user typed --recompress with nothing
        // usable in it, which is a request that cannot be honoured and must not
        // be mistaken for "no chain".
        if (steps.Count == 0 && chain.Steps.Count > 0)
        {
            problems.Add(messages["plan.error.chainAllBlank"]);
            return new PostChainPlan { LivePath = live, Steps = [], Problems = problems };
        }

        var planned = new List<PostChainStep>(steps.Count);
        string input = live;

        // A hop that does not re-encode -- "-c:v copy", or a filter-only pass --
        // keeps the container it was handed. Starting from the live product's
        // own extension is what makes that the default rather than a guess.
        string container = Path.GetExtension(live).TrimStart('.').ToLowerInvariant();

        for (int i = 0; i < steps.Count; i++)
        {
            int number = i + 1;
            ChainStep step = steps[i];

            if (step.Kind == ChainStepKind.Ambiguous)
            {
                problems.Add(messages.Format("plan.error.chainStepAmbiguous", new { number }));
                continue;
            }

            if (step.Kind == ChainStepKind.Plugin)
            {
                // A plugin hop keeps the container it was handed unless the user
                // named one, because a plugin that filters a video normally writes
                // the same kind of file it read. Guessing a container here would
                // mean a "webm" hop whose plugin actually wrote mp4, and the
                // extension would be the only thing that lied.
                string pluginContainer = step.Container.HasText
                    ? step.Container.Trim().TrimStart('.').ToLowerInvariant()
                    : container;

                string pluginOutput = DeriveOutputPath(live, pluginContainer, number);

                planned.Add(new PostChainStep
                {
                    Number = number,
                    InputPath = input,
                    OutputPath = pluginOutput,
                    Container = pluginContainer,
                    Kind = ChainStepKind.Plugin,
                    Plugin = step.Plugin,
                    Stage = step.Stage,
                    Params = step.Params,
                });

                input = pluginOutput;
                container = pluginContainer;
                continue;
            }

            if (step.Args is not { } fragment)
            {
                problems.Add(messages["plan.error.chainAllBlank"]);
                continue;
            }

            (List<string> tokens, string? error) = ArgsFragment.Parse(fragment, messages);

            if (error is not null)
            {
                problems.Add(messages.Format("plan.error.chainPass", new { number, detail = error }));
                continue;
            }

            (string next, string? containerError) = ResolveContainer(step, tokens, container, number, messages);

            if (containerError is not null)
            {
                problems.Add(containerError);
                continue;
            }

            // No "would this overwrite the recording" check here, because the naming
            // rule already makes it impossible: every hop's name is the live
            // product's stem plus "_web"/"_webN", and N differs per hop. A guard was
            // carried here for a while and could never fire -- which made the
            // comment above it a claim about a defence that did not exist. The real
            // protection is that the live product is never a hop's output, only ever
            // its first input.
            string output = DeriveOutputPath(live, next, number);

            planned.Add(new PostChainStep
            {
                Number = number,
                InputPath = input,
                OutputPath = output,
                Container = next,
                Kind = ChainStepKind.Ffmpeg,
                Args = tokens,
            });

            input = output;
            container = next;
        }

        if (planned.Count > 0)
        {
            notes.Add(messages.Format("plan.note.chain", new
            {
                hops = string.Join(" > ", planned.Select(s => $"{Path.GetFileName(s.OutputPath)} ({s.Container})")),
            }));
        }

        return new PostChainPlan { LivePath = live, Steps = planned, Notes = notes, Problems = problems };
    }

    /// <summary>
    /// What this hop writes into: the container the user named, else the one the
    /// fragment's codec pair implies, else whatever the hop before was.
    ///
    /// <para>The middle case is the one that matters. Leaving it to FFmpeg means
    /// the extension has to be guessed before the codec is known, which is how
    /// `-c:v libvpx-vp9` ends up inside a file called <c>_web.mp4</c> and fails
    /// several seconds in with a muxer message.</para>
    /// </summary>
    private static (string Container, string? Error) ResolveContainer(
        ChainStep step, IReadOnlyList<string> tokens, string previous, int number,
        Localization.IMessages messages)
    {
        string? video = RecompressCodecs.DetectVideoCodec(tokens);
        string? audio = RecompressCodecs.DetectAudioCodec(tokens);

        if (step.Container.HasText)
        {
            string wanted = step.Container.Trim().TrimStart('.').ToLowerInvariant();

            // Only checked when the fragment names a codec the catalogue knows;
            // a fragment doing something it cannot reason about (a filter that
            // picks the codec, a codec from a newer FFmpeg) is taken at its word.
            if (video is not null)
            {
                string? error = RecompressCodecs.ValidateContainer(video, audio, wanted, messages);
                if (error is not null)
                {
                    return (previous, messages.Format(
                        "plan.error.chainPass", new { number, detail = error }));
                }
            }

            return (wanted, null);
        }

        if (video is null) return (previous, null);

        string? derived = RecompressCodecs.DefaultContainer(video, audio);
        return derived is not null
            ? (derived, null)
            : (previous, messages.Format("plan.error.chainNoContainer", new
            {
                number,
                video,
                audio,
            }));
    }

    /// <summary>
    /// <c>clip_live.rm</c> becomes <c>clip_live_web.mp4</c>: "_web" marks the
    /// product a site would have served, and later hops number themselves
    /// <c>_web2</c>, <c>_web3</c> from the live product's own name.
    ///
    /// <para>Siblinging is done on the path as given rather than on a resolved
    /// one: the plan is built before the run, and the run resolves its own
    /// output. Resolving here as well would make a plan and its dry run disagree
    /// about what they are printing on any platform that shortens paths.</para>
    /// </summary>
    public static string DeriveOutputPath(string livePath, string container, int pass)
    {
        string folder = Path.GetDirectoryName(livePath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(livePath);
        string extension = container.Or("mp4").TrimStart('.').ToLowerInvariant();
        return Path.Combine(folder, $"{stem}{Suffix(pass)}.{extension}");
    }

    /// <summary>The "_web" / "_web2" part of a hop's name.</summary>
    public static string Suffix(int pass) => pass <= 1 ? "_web" : $"_web{pass}";
}
