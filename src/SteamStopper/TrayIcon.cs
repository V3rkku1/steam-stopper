using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace SteamStopper;

/// <summary>Notification-area icon with quick toggles for when the window is hidden.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _block;
    private readonly Forms.ToolStripMenuItem _bond;
    private readonly Forms.ToolStripMenuItem _ask;

    public TrayIcon(
        Func<bool> isBlocked, Func<bool> isBonding, Func<bool> asksOnClose,
        Action open, Func<Task> toggleBlock, Func<Task> toggleBond, Action toggleAsk, Action quit)
    {
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        var openItem = new Forms.ToolStripMenuItem("Open Steam Stopper", null, (_, _) => open())
        {
            Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold)
        };
        _block = new Forms.ToolStripMenuItem("Block Steam internet", null, async (_, _) => await toggleBlock());
        _bond = new Forms.ToolStripMenuItem("Bonding", null, async (_, _) => await toggleBond());
        _ask = new Forms.ToolStripMenuItem("Ask what the X button does", null, (_, _) => toggleAsk());
        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            openItem,
            new Forms.ToolStripSeparator(),
            _block,
            _bond,
            new Forms.ToolStripSeparator(),
            _ask,
            new Forms.ToolStripMenuItem("Quit", null, (_, _) => quit())
        });
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = Drawing.Color.FromArgb(0xED, 0xED, 0xED);
            item.Padding = new Forms.Padding(2, 4, 2, 4);
        }
        menu.Opening += (_, _) =>
        {
            _block.Checked = isBlocked();
            _bond.Checked = isBonding();
            _ask.Checked = asksOnClose();
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Steam Stopper",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void ShowTip(string title, string text)
        => _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    // Match the app's dark theme instead of the light Windows menu.
    private sealed class DarkRenderer() : Forms.ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Drawing.Color.FromArgb(0xED, 0xED, 0xED);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(0x8B, 0x7C, 0xF6), 2f);
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new Drawing.PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
                new Drawing.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
                new Drawing.PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f)
            });
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Back = Drawing.Color.FromArgb(0x1B, 0x1C, 0x20);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2A, 0x2B, 0x31);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x2E, 0x2F, 0x35);
        public override Drawing.Color ToolStripDropDownBackground => Back;
        public override Drawing.Color ImageMarginGradientBegin => Back;
        public override Drawing.Color ImageMarginGradientMiddle => Back;
        public override Drawing.Color ImageMarginGradientEnd => Back;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Back;
        public override Drawing.Color CheckBackground => Back;
        public override Drawing.Color CheckSelectedBackground => Hover;
        public override Drawing.Color CheckPressedBackground => Hover;
    }
}
