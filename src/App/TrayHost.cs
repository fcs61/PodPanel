using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PodPanel.Core;

namespace PodPanel;

/// <summary>Görev çubuğu bildirim alanındaki simge, sağ tık menüsü ve panelin açılıp kapanması.</summary>
internal sealed class TrayHost : IDisposable
{
    private readonly PodMonitor _monitor;
    private readonly MainViewModel _viewModel;
    private readonly FlyoutWindow _flyout;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _gameItem;
    private readonly ToolStripMenuItem[] _modeItems;
    private DateTime _hiddenAt = DateTime.MinValue;
    private string _iconKey = "";
    private Icon? _currentIcon;

    public TrayHost(PodMonitor monitor, MainViewModel viewModel, Action exit)
    {
        _monitor = monitor;
        _viewModel = viewModel;
        viewModel.ExitCommand = new RelayCommand(_ => exit());

        _flyout = new FlyoutWindow { DataContext = viewModel };
        _flyout.Hidden += () =>
        {
            _hiddenAt = DateTime.Now;
            _monitor.UiOpen = false;
            // Panel her açılışta sade haliyle gelsin.
            viewModel.ShowDetails = false;
        };

        _gameItem = new ToolStripMenuItem("Oyun modu");
        _gameItem.Click += (_, _) => viewModel.GameMode = !viewModel.GameMode;

        // Dinleme modu kısayolları: kontrol kanalı kapalıyken gizli, kulaklık kulakta değilken soluk.
        _modeItems = new[]
        {
            new ToolStripMenuItem("Şeffaf") { Tag = "transparency" },
            new ToolStripMenuItem("Uyarlanabilir") { Tag = "adaptive" },
            new ToolStripMenuItem("Gürültü Engelleme") { Tag = "nc" },
        };
        foreach (ToolStripMenuItem item in _modeItems)
        {
            item.Click += (_, _) => viewModel.SetModeCommand.Execute(item.Tag);
        }
        var modeSeparator = new ToolStripSeparator();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Paneli aç", null, (_, _) => ShowFlyout());
        menu.Items.Add(modeSeparator);
        menu.Items.AddRange(_modeItems);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_gameItem);
        menu.Opening += (_, _) =>
        {
            modeSeparator.Visible = viewModel.ShowModes;
            _modeItems[0].Checked = viewModel.IsModeTransparency;
            _modeItems[1].Checked = viewModel.IsModeAdaptive;
            _modeItems[2].Checked = viewModel.IsModeNoiseCancellation;
            foreach (ToolStripMenuItem item in _modeItems)
            {
                item.Visible = viewModel.ShowModes;
                item.Enabled = viewModel.ModesEnabled;
            }
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => exit());

        _icon = new NotifyIcon { Text = "PodPanel", ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleFlyout();
        };

        viewModel.PropertyChanged += OnViewModelChanged;
        UpdateIcon();
        _icon.Visible = true;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsConnected):
            case nameof(MainViewModel.IsMono):
            case nameof(MainViewModel.TrayTooltip):
                UpdateIcon();
                break;
            case nameof(MainViewModel.GameMode):
                _gameItem.Checked = _viewModel.GameMode;
                break;
        }
    }

    private void ToggleFlyout()
    {
        if (_flyout.IsVisible)
        {
            _flyout.HideFlyout();
            return;
        }
        // Simgeye tıklamak önce paneli odak kaybıyla kapatır; aynı tıklama yeniden açmasın.
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return;
        ShowFlyout();
    }

    private void ShowFlyout()
    {
        _monitor.UiOpen = true;
        _flyout.ShowFlyout();
    }

    /// <summary>Dışarıdan (ikinci kopyadan gelen sinyalle) paneli açar.</summary>
    public void ShowPanel() => ShowFlyout();

    public void HidePanel() => _flyout.HideFlyout();

    private void UpdateIcon()
    {
        string tooltip = _viewModel.TrayTooltip;
        _icon.Text = tooltip.Length > 120 ? tooltip[..120] : tooltip;

        bool lightTaskbar = Theme.TaskbarUsesLightTheme();
        string key = $"{_viewModel.IsConnected}|{_viewModel.IsMono}|{lightTaskbar}";
        if (key == _iconKey) return;
        _iconKey = key;

        Icon next = RenderIcon(_viewModel.IsConnected, _viewModel.IsMono, lightTaskbar);
        _icon.Icon = next;
        _currentIcon?.Dispose();
        _currentIcon = next;
    }

    /// <summary>Kulaklık simgesi; sağ altta durum noktası (yeşil: stereo, turuncu: mono).</summary>
    internal static Icon RenderIcon(bool connected, bool mono, bool lightTaskbar)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color foreground = lightTaskbar ? Color.FromArgb(28, 28, 28) : Color.White;
            if (!connected) foreground = Color.FromArgb(130, foreground);

            using var font = new Font(IconFontName(), 24f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(foreground);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("\uE7F6", font, brush, new RectangleF(0, 0, 32, 32), format);

            if (connected)
            {
                using var ring = new SolidBrush(lightTaskbar ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
                using var dot = new SolidBrush(mono ? Color.FromArgb(255, 170, 60) : Color.FromArgb(90, 200, 90));
                g.FillEllipse(ring, 19, 19, 13, 13);
                g.FillEllipse(dot, 21, 21, 9, 9);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static string IconFontName()
    {
        try
        {
            using var family = new FontFamily("Segoe Fluent Icons");
            return family.Name;
        }
        catch
        {
            return "Segoe MDL2 Assets";
        }
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        _flyout.StayOpen = false;
        _flyout.Close();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
