// TrayService は System.Windows.Forms.NotifyIcon を使用します。
// .csproj に <UseWindowsForms>true</UseWindowsForms> が必要です。
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace Curia.Services;

public class TrayService : IDisposable
{
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ToolStripMenuItem? _hotkeyMenuItem;
    private WinForms.ToolStripMenuItem? _proposalsMenuItem;
    private Icon? _currentIcon;
    private IntPtr _currentIconHandle;
    private int _badgeCount;
    private bool _disposed;

    public Action? OnActivated { get; set; }
    public Action? OnCaptureActivated { get; set; }
    public Action? OnProposalsActivated { get; set; }

    public BitmapSource? DiamondBitmapSource { get; private set; }

    public void Initialize(Window window)
    {
        DiamondBitmapSource = CreateDiamondBitmapSource();
        SwapIcon(CreateTrayIcon(badgeCount: 0));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _currentIcon,
            Text = "Curia",
            Visible = true,
        };

        var contextMenu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("Show");
        showItem.Click += (_, _) => OnActivated?.Invoke();
        contextMenu.Items.Add(showItem);

        var quickCaptureItem = new WinForms.ToolStripMenuItem("Quick Capture");
        quickCaptureItem.Click += (_, _) => OnCaptureActivated?.Invoke();
        contextMenu.Items.Add(quickCaptureItem);

        _proposalsMenuItem = new WinForms.ToolStripMenuItem("Proposals (0)") { Enabled = false };
        _proposalsMenuItem.Click += (_, _) => OnProposalsActivated?.Invoke();
        contextMenu.Items.Add(_proposalsMenuItem);

        contextMenu.Items.Add(new WinForms.ToolStripSeparator());

        _hotkeyMenuItem = new WinForms.ToolStripMenuItem("Hotkey: (none)") { Enabled = false };
        contextMenu.Items.Add(_hotkeyMenuItem);

        contextMenu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            Dispose();
            System.Windows.Application.Current.Shutdown();
        };
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                OnActivated?.Invoke();
        };
    }

    public void UpdateHotkeyDisplay(string hotkeyText)
    {
        if (_hotkeyMenuItem != null)
            _hotkeyMenuItem.Text = $"Hotkey: {hotkeyText}";
    }

    /// <summary>
    /// Proposal Inbox の Pending 件数バッジを更新する。
    /// NotifyIcon を生成した UI スレッドから呼ぶこと。
    /// </summary>
    public void UpdateBadge(int count)
    {
        if (_disposed || _notifyIcon == null) return;
        count = Math.Max(0, count);
        if (count == _badgeCount) return;
        _badgeCount = count;

        SwapIcon(CreateTrayIcon(count));
        _notifyIcon.Icon = _currentIcon;
        _notifyIcon.Text = count > 0 ? $"Curia - {count} proposal(s) pending" : "Curia";

        if (_proposalsMenuItem != null)
        {
            _proposalsMenuItem.Text = $"Proposals ({count})";
            _proposalsMenuItem.Enabled = count > 0;
        }
    }

    public void ShowBalloonTip(string title, string text, int timeoutMs = 3000)
    {
        _notifyIcon?.ShowBalloonTip(timeoutMs, title, text, WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        SwapIcon(null);
    }

    /// <summary>現在のトレイ Icon を差し替え、旧 Icon の GDI ハンドルを解放する。</summary>
    private void SwapIcon((Icon icon, IntPtr handle)? next)
    {
        var oldIcon = _currentIcon;
        var oldHandle = _currentIconHandle;

        _currentIcon = next?.icon;
        _currentIconHandle = next?.handle ?? IntPtr.Zero;

        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero)
            NativeMethods.DestroyIcon(oldHandle);
    }

    // -----------------------------------------------------------------------
    // アイコン描画
    // -----------------------------------------------------------------------

    // GitHub Blue (#58a6ff)
    private static readonly Color DiamondFill = Color.FromArgb(255, 0x58, 0xa6, 0xff);
    private static readonly Color DiamondEdge = Color.FromArgb(200, 0x1f, 0x6f, 0xed);

    /// <summary>
    /// WPF 表示用のダイヤモンド BitmapSource を 32x32 で生成する。
    /// タイトルバー上で文字と重心を合わせるため、少し上に寄せて描く。
    /// </summary>
    private static BitmapSource CreateDiamondBitmapSource()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(DiamondFill);
            var diamond = new System.Drawing.Point[]
            {
                new(size / 2, 0),              // top
                new(size - 2, (size / 2) - 2), // right
                new(size / 2, size - 4),       // bottom
                new(2, (size / 2) - 2),        // left
            };
            g.FillPolygon(brush, diamond);
            using var pen = new Pen(DiamondEdge, 1.5f);
            g.DrawPolygon(pen, diamond);
        }

        var hBitmap = bmp.GetHbitmap();
        var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
            hBitmap, IntPtr.Zero, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        bitmapSource.Freeze();
        NativeMethods.DeleteObject(hBitmap);
        return bitmapSource;
    }

    /// <summary>
    /// トレイ用のダイヤモンド Icon を生成する。badgeCount > 0 のとき右下に
    /// オレンジのバッジ (1 桁なら数字入り) を重ねる。
    /// </summary>
    private static (Icon icon, IntPtr handle) CreateTrayIcon(int badgeCount)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(DiamondFill);
            var diamond = new System.Drawing.Point[]
            {
                new(size / 2, 2),           // top
                new(size - 2, size / 2),    // right
                new(size / 2, size - 2),    // bottom
                new(2, size / 2),           // left
            };
            g.FillPolygon(brush, diamond);
            using var pen = new Pen(DiamondEdge, 1.5f);
            g.DrawPolygon(pen, diamond);

            if (badgeCount > 0)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                const int badgeSize = 18;
                var badgeRect = new Rectangle(size - badgeSize, size - badgeSize, badgeSize, badgeSize);
                using var badgeBrush = new SolidBrush(Color.FromArgb(255, 0xf0, 0x88, 0x3e)); // orange
                g.FillEllipse(badgeBrush, badgeRect);

                if (badgeCount <= 9)
                {
                    using var font = new Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                    using var textBrush = new SolidBrush(Color.White);
                    var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                    };
                    g.DrawString(badgeCount.ToString(), font, textBrush, badgeRect, format);
                }
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        return (Icon.FromHandle(hIcon), hIcon);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
