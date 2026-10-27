namespace LiveArchiveEncoder.Core;

/// <summary>
/// C# 14 extension members for the "did that step actually produce a file"
/// question, which every stage of the pipeline asks once.
///
/// It used to be spelled out at each site as <c>Refresh(); Exists &amp;&amp;
/// Length &gt; 0</c>, and one of those sites had already forgotten the refresh.
/// </summary>
public static class FileExtensions
{
    extension(FileInfo file)
    {
        /// <summary>
        /// Exists and holds at least one byte, read from disk rather than from
        /// the snapshot this <see cref="FileInfo"/> took when it was created.
        ///
        /// The refresh lives inside because forgetting it is invisible: the
        /// cached answer is whatever the file looked like when the object was
        /// constructed, which for an output that FFmpeg has just written is
        /// always "did not exist".
        /// </summary>
        public bool HasContent
        {
            get
            {
                file.Refresh();
                return file.Exists && file.Length > 0;
            }
        }
    }
}
