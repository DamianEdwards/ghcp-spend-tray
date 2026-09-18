namespace GHSpend.App;

internal static class Diagnostics
{
    private static readonly object Gate = new();
    private static string? _directory;
    internal static void Initialize(string dataDirectory) => _directory = Path.Combine(dataDirectory, "logs");
    // Callers provide only fixed diagnostic categories, never server responses or exception messages.
    internal static void Record(string category)
    {
        lock (Gate)
        {
            try
            {
                if (_directory is null) return;
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, "diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024)
                    File.Move(path, Path.Combine(_directory, "diagnostics.previous.log"), true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {category}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Native.Win32.MessageBox(0, "GHSpend cannot write its diagnostic log. Check data-folder permissions and free space.",
                    "GHSpend storage error", Native.Win32.MB_ICONERROR);
            }
        }
    }
}
