using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace PodPanel;

public partial class FlyoutWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    /// <summary>Ekran görüntüsü modunda odak kaybında gizlenmeyi kapatır.</summary>
    public bool StayOpen { get; set; }

    public event Action? Hidden;

    public FlyoutWindow()
    {
        InitializeComponent();
        Theme.Apply(Resources);
        Deactivated += (_, _) => HideFlyout();
        SizeChanged += (_, _) => Reposition();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
            else if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox) CommitText();
        };
        SourceInitialized += (_, _) =>
        {
            // Alt+Tab listesinde görünmesin.
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExToolWindow));
        };
    }

    public void ShowFlyout()
    {
        Theme.Apply(Resources);
        Show();
        Reposition();
        Activate();
    }

    public void HideFlyout()
    {
        if (StayOpen || !IsVisible) return;
        CommitText();
        Hide();
        Hidden?.Invoke();
    }

    /// <summary>Metin kutusundaki değişikliği odak değişmeden de kaydet.</summary>
    private void CommitText() => AutoGameBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    /// <summary>Paneli çalışma alanının sağ alt köşesine (görev çubuğunun üstüne) yerleştirir.</summary>
    private void Reposition()
    {
        if (StayOpen) return;
        Rect area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
