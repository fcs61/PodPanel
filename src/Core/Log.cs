namespace PodPanel.Core;

/// <summary>%LOCALAPPDATA%\PodPanel altına yazan küçük günlük.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodPanel");

    public static string FilePath => Path.Combine(Dir, "podpanel.log");

    /// <summary>Tanı aracında günlük satırlarını konsola da yazar.</summary>
    public static bool Echo { get; set; }

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        if (Echo) Console.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Günlük yazılamıyorsa uygulamayı durdurma.
        }
    }
}
