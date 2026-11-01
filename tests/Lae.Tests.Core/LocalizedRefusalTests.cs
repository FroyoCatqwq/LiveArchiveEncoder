using Lae.Core;
using Lae.Core.Settings;
using Lae.Media.Localization;

namespace Lae.Tests.Core;

/// <summary>
/// The sentences a rejected plan comes back with, in the language it was asked in.
///
/// <para>This is the half of the translation work that lived inside
/// <c>Lae.Core</c> until now: the plan's problems, the subtitle refusals and the
/// argument checks are produced too far from the command line to have been
/// translated there. A translated <c>--help</c> over an English refusal is worse
/// than no translation at all, because it tells the reader the program speaks
/// their language right up to the moment it matters.</para>
///
/// <para>Asserted through the real resource files, in a project that has them:
/// a stub that echoes keys would pass while the shipped file said something
/// else.</para>
/// </summary>
public sealed class LocalizedRefusalTests
{
    private static EncodeRequest Request(string? destination = null) => new()
    {
        SourcePath = "/media/tape.avi",
        DestinationPath = destination,
        Encode = new EncodeDefaults
        {
            Width = 320,
            Height = 240,
            Fps = 13,
            Encoder = "mpeg4",
        },
    };

    private static SourceInfo Source() => new()
    {
        DurationSeconds = 10,
        Width = 320,
        Height = 240,
        PixelFormat = "yuv420p",
        HasAudio = false,
        Streams = [new SourceStream(0, "video", "mpeg4", null, null, new StreamInfo { Width = 320, Height = 240 })],
    };

    private static EncoderInfo Encoder() => new()
    {
        Name = "mpeg4",
        Extension = "avi",
        FormatName = "avi",
    };

    /// <summary>
    /// One refusal, two languages, from the same request. The point is not
    /// "translation exists" but "the same code path answers in whichever language
    /// the caller speaks", which is what makes <c>LAE_LANG</c> work at all.
    /// </summary>
    [Test]
    public async Task ARefusedRequestAnswersInTheLanguageItWasAsked()
    {
        EncodeRequest request = Request(destination: "/media/tape.avi");

        EncodePlan english = EncodePlan.Create(request, Source(), Encoder(), TestMessages.En);
        EncodePlan chinese = EncodePlan.Create(
            request, Source(), Encoder(), I18NextMessages.Create("zh"));

        await Assert.That(english.Problems[0]).Contains("the output path is the input path");
        await Assert.That(chinese.Problems[0]).Contains("输出路径就是输入路径");
    }

    /// <summary>
    /// A refusal produced by a helper rather than by the planner: the argument
    /// builder throws with a key, and the planner turns it into a sentence. Those
    /// were the sentences most likely to stay English, because they are thrown
    /// three layers down from anything that knows the language.
    /// </summary>
    [Test]
    public async Task ARefusalFromAnArgumentBuilderIsTranslatedToo()
    {
        var subtitles = new SubtitleDefaults
        {
            Mode = "burn",
            Files = ["/media/a.srt"],
            BoxAspect = "wide",
        };

        SubtitleBurn english = SubtitleChain.Build(subtitles, 0, null, TestMessages.En);
        SubtitleBurn chinese = SubtitleChain.Build(subtitles, 0, null, I18NextMessages.Create("zh"));

        await Assert.That(english.Problems[0]).Contains("unknown subtitle box aspect");
        await Assert.That(chinese.Problems[0]).Contains("不认识的字幕框形状");
    }

    /// <summary>
    /// Notes are translated as well, not just problems: a run that succeeds with a
    /// note about a rounded frame size reads as half-translated otherwise.
    /// </summary>
    [Test]
    public async Task APlanNoteIsTranslated()
    {
        EncodeRequest request = Request() with
        {
            Encode = Request().Encode with { Width = 321, Height = 241 },
        };

        EncodePlan chinese = EncodePlan.Create(request, Source(), Encoder(), I18NextMessages.Create("zh"));

        await Assert.That(string.Join('\n', chinese.Notes)).Contains("对");
        await Assert.That(string.Join('\n', chinese.Notes)).Contains("不合法");
    }
}
