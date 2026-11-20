using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PodPanel.Core;

/// <summary>Penceresi açık bir uygulama: süreç adı (uzantısız) ve kullanıcıya gösterilecek ad.</summary>
public sealed record RunningApp(string Process, string Label);

/// <summary>
/// Görev çubuğunda penceresi olan uygulamaları listeler; oyun modu listesine ad yazmadan ekleme yapmak için.
/// Sıra pencerelerin üst üste sırasıdır: en son kullanılan uygulama başta gelir.
/// </summary>
public static class RunningApps
{
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxLabel = 60;

    /// <summary>Kabuğun ve Windows'un kendi pencereleri; oyun olarak eklenmeleri anlamsız.</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "LockApp", "PodPanel",
    };

    public static List<RunningApp> List(int max = 12)
    {
        var found = new List<RunningApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint own = (uint)Environment.ProcessId;

        EnumWindows((window, _) =>
        {
            if (found.Count >= max) return false;
            // Alt+Tab'da görünen pencereler: görünür, sahipsiz, araç penceresi değil, gizlenmemiş (arka plandaki
            // mağaza uygulamaları "cloaked" durur), başlığı var.
            if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return true;
            if (DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            string title = Title(window);
            if (title.Length == 0) return true;

            GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0 || pid == own) return true;
            string name;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return true; // süreç bu arada kapandı
            }
            if (name.Length == 0 || Hidden.Contains(name) || !seen.Add(name)) return true;

            found.Add(new RunningApp(name, Label(pid, title, name)));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Gösterilecek ad: dosyanın açıklaması (ör. "Google Chrome"); yoksa pencere başlığı. Oyunların çoğunda açıklama
    /// boştur (cs2.exe, TslGame.exe) ve pencere başlığı oyunun adıdır.
    /// </summary>
    private static string Label(uint pid, string title, string name)
    {
        string description = "";
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    description = (FileVersionInfo.GetVersionInfo(buffer.ToString()).FileDescription ?? "").Trim();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Dosya okunamıyorsa pencere başlığı kullanılır.
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        string label = description.Length > 0 ? description : title;
        if (label.Length == 0) label = name;
        return label.Length > MaxLabel ? label[..MaxLabel].TrimEnd() + "…" : label;
    }

    private static string Title(IntPtr window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var buffer = new StringBuilder(length + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
