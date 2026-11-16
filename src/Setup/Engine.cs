using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace PodPanel.Setup;

/// <summary>Bu programa gömülü kurulum dosyaları (payload.zip). Kaldir.exe derlemesinde yoktur.</summary>
internal static class Payload
{
    private const string ResourceName = "payload.zip";

    public static bool Exists => Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) != null;

    /// <summary>
    /// Süzgece uyan dosyaları hedef klasöre açar. Hedef, yalnızca yöneticilerin yazabildiği bir klasör olmalıdır:
    /// açılan dosyalar sonradan yönetici olarak çalıştırılır.
    /// </summary>
    public static void Extract(string target, Func<string, bool> filter, Action<double>? progress)
    {
        string root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Kurulum dosyaları bu programın içinde yok.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        long total = 0, done = 0;
        var wanted = new List<ZipArchiveEntry>();
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('/', '\\');
            if (name.EndsWith("\\", StringComparison.Ordinal) || !filter(name)) continue;
            wanted.Add(entry);
            total += entry.Length;
        }

        var buffer = new byte[1 << 16];
        foreach (ZipArchiveEntry entry in wanted)
        {
            string path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', '\\')));
            // Arşivdeki bir ad hedef klasörün dışını gösteremez.
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Geçersiz dosya adı: " + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (Stream input = entry.Open())
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    done += read;
                    progress?.Invoke(total == 0 ? 1 : (double)done / total);
                }
            }
        }
        progress?.Invoke(1);
    }
}

/// <summary>Bir betiğin sonucu: "@@SON kod yeniden-başlat metin" satırından.</summary>
internal sealed class ScriptResult
{
    public int Code { get; set; } = -1;
    public bool Restart { get; set; }
    public string Message { get; set; } = "";

    /// <summary>Betik son satırını yazdı (yazmadan bittiyse beklenmedik biçimde durmuştur).</summary>
    public bool Reported { get; set; }
}

/// <summary>
/// Kurulum betiklerini gizli bir Windows PowerShell sürecinde çalıştırır ve çıktısını satır satır iletir.
/// Betik dosya olarak değil metin olarak yüklenir; böylece bilgisayardaki betik çalıştırma ilkesi kurulumu engellemez.
/// </summary>
internal static class ScriptRunner
{
    public static Task<ScriptResult> RunAsync(string scriptPath, IDictionary<string, string> environment, Action<string> onLine)
    {
        return Task.Run(() =>
        {
            var result = new ScriptResult();
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var info = new ProcessStartInfo(powershell,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"& ([scriptblock]::Create([IO.File]::ReadAllText($env:PODPANEL_BETIK,[Text.Encoding]::UTF8)))\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.EnvironmentVariables["PODPANEL_BETIK"] = scriptPath;
            foreach (KeyValuePair<string, string> pair in environment) info.EnvironmentVariables[pair.Key] = pair.Value;

            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                string line = e.Data.TrimStart('\uFEFF');
                if (line.StartsWith("@@SON ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(new[] { ' ' }, 4);
                    if (parts.Length >= 3 && int.TryParse(parts[1], out int code))
                    {
                        result.Code = code;
                        result.Restart = parts[2] == "1";
                        result.Message = parts.Length > 3 ? parts[3] : "";
                        result.Reported = true;
                    }
                }
                onLine(line);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) onLine("! " + e.Data);
            };
            try
            {
                process.Start();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                result.Message = "Windows PowerShell başlatılamadı: " + e.Message;
                return result;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            if (!result.Reported)
            {
                result.Code = process.ExitCode == 0 ? -1 : process.ExitCode;
                result.Message = "İşlem beklenmedik biçimde durdu (çıkış kodu " + process.ExitCode + ").";
            }
            return result;
        });
    }
}
