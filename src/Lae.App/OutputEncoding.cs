using System.Text;

namespace Lae.App;

/// <summary>
/// What encoding the program writes in when nobody is watching a console.
///
/// <para>With a console attached the inherited code page is the right answer:
/// on a Chinese Windows the console is 936 and the user's font expects 936.
/// Redirected, it is the wrong answer -- <c>lae encode &gt; log.txt</c> and
/// every script that captures stdout were getting the machine's OEM code page,
/// so a Chinese message arrived as bytes no UTF-8 reader could decode. The
/// end-to-end suite caught it: <c>assert "来源" in result.stdout</c> failed on a
/// run that printed exactly the right word.</para>
///
/// <para>So: redirected means UTF-8, always, on every platform. That is a
/// promise a pipeline can rely on, and it is what makes a Chinese error message
/// readable in a CI log. Interactive output is left alone.</para>
/// </summary>
public static class OutputEncoding
{
    /// <summary>Call once, before anything can write.</summary>
    public static void EnsureUtf8WhenRedirected()
    {
        if (!Console.IsOutputRedirected) return;

        try
        {
            // No BOM: a log file that starts with EF BB BF breaks every shell
            // tool that reads its first byte.
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception exc) when (exc is IOException or PlatformNotSupportedException
                                        or UnauthorizedAccessException)
        {
            // A process with no console at all can refuse to be told. Writing
            // continues in whatever the runtime picked; that is better than
            // failing a command over an encoding preference.
        }
    }
}
