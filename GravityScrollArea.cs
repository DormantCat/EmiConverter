using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EmiConverter;

internal readonly record struct ScrollMetrics(int Position, int Content, int Page)
{
    public int Limit => Math.Max(0, Content - Page);
}

internal interface IScrollViewport
{
    event EventHandler? ViewChanged;
    ScrollMetrics ReadScroll();
    void ScrollTo(int position);
}

internal sealed class GravityScrollArea : TableLayoutPanel
{
    private readonly Control content;
    private readonly IScrollViewport viewport;
    private readonly GravityScrollBar bar;
    private int wheelRemainder;

    public GravityScrollArea(Control content)
    {
        this.content = content;
        viewport = (IScrollViewport)content;
        bar = new GravityScrollBar(viewport);
        ColumnCount = 2;
        RowCount = 1;
        Dock = DockStyle.Fill;
        Margin = content.Margin;
        content.Margin = Padding.Empty;
        content.Dock = DockStyle.Fill;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
        Controls.Add(content, 0, 0);
        Controls.Add(bar, 1, 0);
        content.MouseWheel += Wheel;
        bar.MouseWheel += Wheel;
        content.BackColorChanged += (_, _) => RefreshColors();
        content.ForeColorChanged += (_, _) => RefreshColors();
        VisibleChanged += (_, _) => bar.RefreshRange();
        RefreshColors();
    }

    private void RefreshColors()
    {
        bar.BackColor = content.BackColor;
        bar.ForeColor = content.ForeColor;
        bar.Invalidate();
    }

    private void Wheel(object? sender, MouseEventArgs e)
    {
        var state = viewport.ReadScroll();
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines == 0 || state.Limit == 0)
            return;
        if (e is HandledMouseEventArgs handled)
            handled.Handled = true;

        wheelRemainder += e.Delta;
        int steps = wheelRemainder / SystemInformation.MouseWheelScrollDelta;
        wheelRemainder %= SystemInformation.MouseWheelScrollDelta;
        if (steps != 0)
            viewport.ScrollTo(state.Position - steps * (lines < 0 ? state.Page : lines));
    }

    private sealed class GravityScrollBar : Control
    {
        private readonly IScrollViewport viewport;
        private bool hover;
        private int? dragOffset;

        public GravityScrollBar(IScrollViewport viewport)
        {
            this.viewport = viewport;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            TabStop = false;
            AccessibleRole = AccessibleRole.ScrollBar;
            AccessibleName = "Вертикальная прокрутка";
            DoubleBuffered = true;
            viewport.ViewChanged += ViewChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                viewport.ViewChanged -= ViewChanged;
            base.Dispose(disposing);
        }

        private void ViewChanged(object? sender, EventArgs e) => RefreshRange();

        internal void RefreshRange()
        {
            Visible = viewport.ReadScroll().Limit > 0;
            Invalidate();
        }

        private Rectangle Track => Rectangle.Inflate(ClientRectangle, -LogicalToDeviceUnits(3), -LogicalToDeviceUnits(2));

        private Rectangle Thumb(ScrollMetrics state)
        {
            var track = Track;
            if (track.Width <= 0 || track.Height <= 0 || state.Limit <= 0)
                return Rectangle.Empty;

            int length = Math.Min(track.Height, Math.Max(LogicalToDeviceUnits(20),
                (int)Math.Round(track.Height * state.Page / (double)state.Content)));
            int offset = (int)Math.Round((track.Height - length) * Math.Clamp(state.Position, 0, state.Limit) / (double)state.Limit);
            return new Rectangle(track.X, track.Y + offset, track.Width, length);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var thumb = Thumb(viewport.ReadScroll());
            if (thumb.IsEmpty)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Blend(BackColor, ForeColor, dragOffset.HasValue ? .58 : hover ? .46 : .30));
            using var path = new GraphicsPath();
            var diameter = Math.Min(thumb.Width, thumb.Height);
            path.AddArc(thumb.X, thumb.Y, thumb.Width, diameter, 180, 180);
            path.AddArc(thumb.X, thumb.Bottom - diameter, thumb.Width, diameter, 0, 180);
            path.CloseFigure();
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            var state = viewport.ReadScroll();
            var thumb = Thumb(state);
            if (thumb.IsEmpty)
                return;
            if (thumb.Contains(e.Location))
            {
                dragOffset = e.Y - thumb.Y;
                Capture = true;
                Invalidate();
            }
            else
                viewport.ScrollTo(state.Position + (e.Y < thumb.Y ? -state.Page : state.Page));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragOffset is not int offset)
                return;
            var state = viewport.ReadScroll();
            var thumb = Thumb(state);
            var track = Track;
            int travel = track.Height - thumb.Height;
            if (travel > 0)
                viewport.ScrollTo((int)Math.Round(Math.Clamp(e.Y - track.Y - offset, 0, travel) * state.Limit / (double)travel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (Capture)
                Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            dragOffset = null;
            Invalidate();
            base.OnMouseCaptureChanged(e);
        }

        private static Color Blend(Color background, Color ink, double weight)
        {
            int Mix(int a, int b) => (int)Math.Round(a + (b - a) * weight);
            return Color.FromArgb(Mix(background.R, ink.R), Mix(background.G, ink.G), Mix(background.B, ink.B));
        }
    }
}

internal sealed class GravityTextBox : TextBox, IScrollViewport
{
    private const int GetRect = 0x00B2;
    private const int GetLineCount = 0x00BA;
    private const int LineScroll = 0x00B6;
    private const int LineIndex = 0x00BB;
    private const int GetFirstVisibleLine = 0x00CE;
    private const int PositionFromChar = 0x00D6;
    private bool refreshPending;

    public event EventHandler? ViewChanged;

    public ScrollMetrics ReadScroll()
    {
        if (!IsHandleCreated || ClientSize.Height <= 0)
            return default;
        var rect = new NativeRect();
        SendRect(Handle, GetRect, 0, ref rect);
        int position = (int)Send(Handle, GetFirstVisibleLine, 0, 0);
        int count = (int)Send(Handle, GetLineCount, 0, 0);
        int lineHeight = Font.Height;
        if (position + 1 < count && LineY(position) is int top && LineY(position + 1) is int next && next > top)
            lineHeight = next - top;
        int page = Math.Max(1, (rect.Bottom - rect.Top) / Math.Max(1, lineHeight));
        return new(position, count, page);
    }

    private int? LineY(int line)
    {
        var index = Send(Handle, LineIndex, line, 0);
        if (index < 0)
            return null;
        var position = Send(Handle, PositionFromChar, index, 0);
        return position == -1 ? null : unchecked((short)((int)position >> 16));
    }

    public void ScrollTo(int position)
    {
        var state = ReadScroll();
        int delta = Math.Clamp(position, 0, state.Limit) - state.Position;
        if (delta == 0 || !IsHandleCreated)
            return;
        Send(Handle, LineScroll, 0, delta);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is 0x0005 or 0x000C or 0x0030 or 0x0100 or 0x0102 or 0x0115 or
            0x0200 or 0x0202 or 0x020A or 0x0302 or 0x0304 or 0x00B1 or 0x00B7 or 0x00C2)
            QueueRefresh();
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); QueueRefresh(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); QueueRefresh(); }
    protected override void OnHandleDestroyed(EventArgs e) { refreshPending = false; base.OnHandleDestroyed(e); }

    private void QueueRefresh()
    {
        if (!Multiline || ViewChanged is null || !IsHandleCreated || refreshPending || Disposing)
            return;
        refreshPending = true;
        BeginInvoke(() =>
        {
            refreshPending = false;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint Send(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendRect(nint hwnd, int message, nint wParam, ref NativeRect rect);
}

internal sealed class GravityGrid : DataGridView, IScrollViewport
{
    public GravityGrid() => ScrollBars = ScrollBars.None;

    public event EventHandler? ViewChanged;

    public ScrollMetrics ReadScroll() => new(
        Math.Max(0, FirstDisplayedScrollingRowIndex), RowCount, Math.Max(1, DisplayedRowCount(false)));

    public void ScrollTo(int position)
    {
        var state = ReadScroll();
        int next = Math.Clamp(position, 0, state.Limit);
        if (RowCount > 0 && next != state.Position)
            FirstDisplayedScrollingRowIndex = next;
    }

    protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); Notify(); }
    protected override void OnRowsAdded(DataGridViewRowsAddedEventArgs e) { base.OnRowsAdded(e); Notify(); }
    protected override void OnRowsRemoved(DataGridViewRowsRemovedEventArgs e) { base.OnRowsRemoved(e); Notify(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Notify(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Notify(); }
    private void Notify() => ViewChanged?.Invoke(this, EventArgs.Empty);
}
