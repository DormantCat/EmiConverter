using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text;

namespace EmiConverter;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(245, 245, 247);
    private static readonly Color Surface = Color.White;
    private static readonly Color Field = Color.FromArgb(240, 240, 243);
    private static readonly Color Ink = Color.FromArgb(27, 27, 30);
    private static readonly Color Muted = Color.FromArgb(91, 91, 96);
    private static readonly Color Accent = Color.FromArgb(255, 201, 52);
    private static readonly Color AccentSoft = Color.FromArgb(255, 239, 190);
    private const string Empty = "—";
    private const int PreviewLimit = 1_000_000;

    private readonly TextBox subject = ReadOnlyText(multiline: true, size: 17);
    private readonly TextBox date = ReadOnlyText();
    private readonly TextBox from = ReadOnlyText();
    private readonly Label to = RecipientValue();
    private readonly Label copyRecipients = RecipientValue();
    private readonly ToolTip recipientToolTip = new();
    private readonly Button recipientList = ActionButton("Все адреса…");
    private string recipientsReport = "";
    private IReadOnlyList<string> messageLinks = [];
    private readonly TextBox selectedLink = ReadOnlyText(multiline: true);
    private readonly Button copyAllLinks = ActionButton("Копировать все");
    private readonly Button inspectLink = ActionButton("Куда ведёт");
    private readonly TextBox replyTo = ReadOnlyText();
    private readonly TextBox body = ReadOnlyText(multiline: true, size: 12);
    private readonly TextBox source = ReadOnlyText(multiline: true, monospaced: true);
    private readonly TextBox summaryDomain = ReadOnlyText(size: 13);
    private readonly TextBox summaryRegistrar = ReadOnlyText(size: 11);
    private readonly TextBox summaryAbuse = ReadOnlyText(multiline: true);
    private readonly Label lookupStatus = Caption("Введите домен или выберите адрес из письма.", 9);
    private readonly ToolTip abuseSourceTip = new();
    private readonly TextBox dossierInput = new() { BorderStyle = BorderStyle.None, BackColor = Field, ForeColor = Ink,
        PlaceholderText = "Домен, URL, email или IP" };
    private readonly HtmlMailPreview htmlPreview = new();
    private readonly DossierLookup dossier;
    private CancellationTokenSource? dossierRequest;
    private DomainRecord? currentRecord;
    private string? currentRecordTarget;
    private readonly Label status = Caption("Файл не открыт");
    private readonly TextBox domainInput = new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Field, ForeColor = Ink,
        BorderStyle = BorderStyle.None, PlaceholderText = "example.org",
    };
    private readonly Button domainPicker = new StudioButton("", icon: ButtonIcon.Chevron) { AccessibleName = "Выбрать домен из письма" };
    private string[] domainChoices = [];
    private StudioButton? maximizeButton;
    private readonly PageDeck letterTabs = new();
    private readonly PageDeck sideTabs = new();
    private readonly Button lookupButton = ActionButton("Проверить", true);
    private readonly Button linkLookup = ActionButton("Проверить домен", true);
    private readonly Button linkCopy = ActionButton("Копировать");
    private readonly Button fromLookup = ActionButton("WHOIS");
    private readonly Button toLookup = ActionButton("WHOIS");
    private readonly GravityGrid links = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly DomainLookup lookup;
    private Control letterContent = null!;
    private Control letterEmpty = null!;
    private Control sourcesEmpty = null!;
    private CancellationTokenSource? activeLookup;
    private bool loading;

    private static readonly string ThemeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmiConverter", "theme.txt");
    private bool darkTheme = ReadTheme();
    private Button themeButton = null!;
    private readonly Dictionary<Control, PaintColors> themeColors = [];
    private ToolbarPalette currentPalette = null!;

    private static bool ReadTheme()
    {
        try
        {
            var choice = File.Exists(ThemeFile) ? File.ReadAllText(ThemeFile).Trim() : null;
            if (choice is "dark" or "light")
                return choice == "dark";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private void ApplyTheme()
    {
        var palette = darkTheme
            ? new ToolbarPalette(Color.FromArgb(30, 29, 28), Color.FromArgb(40, 38, 36), Color.FromArgb(52, 49, 46),
                Color.FromArgb(244, 241, 237), Color.FromArgb(184, 179, 173),
                Color.FromArgb(88, 75, 39), Color.FromArgb(255, 215, 107))
            : new ToolbarPalette(Background, Surface, Field, Ink, Muted, AccentSoft, Color.FromArgb(112, 76, 0));
        currentPalette = palette;
        themeButton.Text = darkTheme ? "☀  Светлая" : "☾  Тёмная";
        themeButton.AccessibleName = darkTheme ? "Включить светлую тему" : "Включить тёмную тему";
        CaptureColors(this);
        Recolor(this);

        links.BackgroundColor = palette.Surface;
        links.BackColor = palette.Surface;
        links.ForeColor = palette.Ink;
        links.GridColor = palette.Field;
        links.ColumnHeadersDefaultCellStyle.BackColor = palette.Field;
        links.ColumnHeadersDefaultCellStyle.ForeColor = palette.Muted;
        links.ColumnHeadersDefaultCellStyle.SelectionBackColor = palette.Field;
        links.ColumnHeadersDefaultCellStyle.SelectionForeColor = palette.Muted;
        links.DefaultCellStyle.BackColor = palette.Surface;
        links.DefaultCellStyle.ForeColor = palette.Ink;
        links.DefaultCellStyle.SelectionBackColor = palette.Selected;
        links.DefaultCellStyle.SelectionForeColor = palette.Ink;
        links.RowsDefaultCellStyle.BackColor = palette.Surface;
        links.RowsDefaultCellStyle.ForeColor = palette.Ink;
        links.AlternatingRowsDefaultCellStyle.BackColor = palette.Field;
        links.AlternatingRowsDefaultCellStyle.ForeColor = palette.Ink;
        links.RowsDefaultCellStyle.SelectionBackColor = palette.Selected;
        links.RowsDefaultCellStyle.SelectionForeColor = palette.Ink;
        links.AlternatingRowsDefaultCellStyle.SelectionBackColor = palette.Selected;
        links.AlternatingRowsDefaultCellStyle.SelectionForeColor = palette.Ink;
        WindowTheme.Apply(this, darkTheme, palette.Background, palette.Ink);
        Invalidate(true);

        void CaptureColors(Control control)
        {
            themeColors.TryAdd(control, new PaintColors(control.BackColor, control.ForeColor));
            foreach (Control child in control.Controls)
                CaptureColors(child);
        }

        void Recolor(Control control)
        {
            var (back, fore) = themeColors[control];

            if (control is SurfacePanel card)
                card.CardBackground = palette.Background;
            if (control is StudioButton button)
                button.Theme = palette;
            if (control is MailMark mark)
                mark.Theme = palette;
            if (control == this)
                control.BackColor = palette.Background;
            else if (back == Surface)
                control.BackColor = palette.Surface;
            else if (back == Field)
                control.BackColor = palette.Field;
            else if (back == Background)
                control.BackColor = palette.Background;
            else if (control is Panel && back != Color.Transparent)
                control.BackColor = control.Parent?.BackColor ?? palette.Surface;

            if (fore == Ink)
                control.ForeColor = palette.Ink;
            else if (fore == Muted)
                control.ForeColor = palette.Muted;
            foreach (Control child in control.Controls)
                Recolor(child);
        }
    }

    private void ToggleTheme()
    {
        darkTheme = !darkTheme;
        ApplyTheme();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);
            File.WriteAllText(ThemeFile, darkTheme ? "dark" : "light");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            status.Text = "Тема применена. Сохранить выбор не удалось.";
        }
    }

    private sealed record PaintColors(Color Back, Color Fore);
    private sealed record ToolbarPalette(Color Background, Color Surface, Color Field, Color Ink,
        Color Muted, Color Selected, Color SelectedInk);

    public MainForm(string? initialPath)
    {
        lookup = new DomainLookup(http);
        dossier = new DossierLookup(http);
        Text = "EMI Converter";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.None;
        Font = AppFont(10);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Background;
        ForeColor = Ink;
        MinimumSize = new Size(920, 620);
        ClientSize = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var layout = Table(1, 3, 24);
        layout.Padding = new Padding(20, 9, 20, 6);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        Controls.Add(layout);
        layout.Controls.Add(BuildToolbar(), 0, 0);

        var workspace = Table(2, 1);
        workspace.Margin = new Padding(0, 9, 0, 0);
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        workspace.Controls.Add(BuildLetter(), 0, 0);
        var tools = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(21, 15, 21, 18), Margin = new Padding(8, 0, 0, 0) };
        tools.Controls.Add(sideTabs);
        sideTabs.Add("Ссылки", BuildLinks());
        sideTabs.Add("WHOIS", BuildLookup());
        sideTabs.Add("Данные", BuildDossier());
        sideTabs.SelectionChanged += (_, _) =>
        {
            if (sideTabs.SelectedIndex == 2 && string.IsNullOrWhiteSpace(dossierInput.Text))
                dossierInput.Text = domainInput.Text;
        };
        workspace.Controls.Add(tools, 1, 0);
        layout.Controls.Add(workspace, 0, 1);

        var footer = Table(2, 1);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.Dock = DockStyle.Fill;
        status.AutoEllipsis = true;
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(Caption($"v{Application.ProductVersion}   ·   EML / EMI / TXT", 9), 1, 0);
        layout.Controls.Add(footer, 0, 2);

        body.AccessibleName = "Содержимое письма";
        source.AccessibleName = "Исходник письма";
        ApplyTheme();
        domainInput.AccessibleName = "Домен, email или URL";
        UpdateLinkActions();

        EnableFileDrop(this);
        KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.O)
            {
                e.SuppressKeyPress = true;
                await ChooseFileAsync();
            }
        };
        if (!string.IsNullOrWhiteSpace(initialPath))
            Shown += async (_, _) => await LoadFileAsync(initialPath);
    }

    private Control BuildToolbar()
    {
        var toolbar = Table(5, 1);
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 57));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var mark = new MailMark(40, Accent, Ink) { Anchor = AnchorStyles.Left };
        toolbar.Controls.Add(mark, 0, 0);
        var title = Caption("EMI Converter", 21, Ink, FontStyle.Bold);
        title.AutoSize = false;
        title.AutoEllipsis = true;
        title.Dock = DockStyle.Fill;
        toolbar.Controls.Add(title, 1, 0);
        foreach (var control in new Control[] { toolbar, mark, title })
        {
            control.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && e.Clicks == 1)
                    WindowTheme.Drag(this);
            };
            control.DoubleClick += (_, _) => ToggleMaximize();
        }
        var paste = ActionButton("Вставить исходник");
        paste.Click += async (_, _) => await PasteAsync();
        themeButton = ActionButton("Тема");
        themeButton.Click += (_, _) => ToggleTheme();
        toolbar.Controls.Add(themeButton, 2, 0);
        toolbar.Controls.Add(paste, 3, 0);
        var open = ActionButton("Открыть файл…", true);
        open.Click += async (_, _) => await ChooseFileAsync();
        var controls = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        controls.Controls.Add(open);
        AddWindowButton(ButtonIcon.Minimize, "Свернуть", () => WindowState = FormWindowState.Minimized);
        maximizeButton = AddWindowButton(ButtonIcon.Maximize, "Развернуть", ToggleMaximize);
        AddWindowButton(ButtonIcon.Close, "Закрыть", Close);
        toolbar.Controls.Add(controls, 4, 0);
        return toolbar;

        StudioButton AddWindowButton(ButtonIcon icon, string label, Action action)
        {
            var button = new StudioButton("", icon: icon);
            button.AutoSize = false;
            button.Size = new Size(36, 36);
            button.Padding = Padding.Empty;
            button.Margin = new Padding(5, 0, 0, 0);
            button.AccessibleName = label;
            button.Click += (_, _) => action();
            controls.Controls.Add(button);
            return button;
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (maximizeButton is { } button)
        {
            bool maximized = WindowState == FormWindowState.Maximized;
            button.IconKind = maximized ? ButtonIcon.Restore : ButtonIcon.Maximize;
            button.AccessibleName = maximized ? "Восстановить окно" : "Развернуть";
            button.Invalidate();
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x0024 && m.LParam != IntPtr.Zero) // WM_GETMINMAXINFO
        {
            var screen = Screen.FromHandle(m.HWnd);
            var work = screen.WorkingArea;
            var limits = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
            // Windows applies the monitor origin when positioning a maximized window.
            limits.MaxPosition = new Point(work.Left - screen.Bounds.Left, work.Top - screen.Bounds.Top);
            limits.MaxSize = new Point(work.Width, work.Height);
            System.Runtime.InteropServices.Marshal.StructureToPtr(limits, m.LParam, false);
            return;
        }
        if (m.Msg != 0x0084 || WindowState != FormWindowState.Normal || m.Result != new IntPtr(1))
            return;

        long position = m.LParam.ToInt64();
        var point = PointToClient(new Point(unchecked((short)position), unchecked((short)(position >> 16))));
        int grip = LogicalToDeviceUnits(7);
        int horizontal = point.X < grip ? 1 : point.X >= ClientSize.Width - grip ? 2 : 0;
        int vertical = point.Y < grip ? 1 : point.Y >= ClientSize.Height - grip ? 2 : 0;
        m.Result = new IntPtr((horizontal, vertical) switch
        {
            (1, 1) => 13, (2, 1) => 14, (1, 2) => 16, (2, 2) => 17,
            (1, 0) => 10, (2, 0) => 11, (0, 1) => 12, (0, 2) => 15, _ => 1,
        });
    }

    private Control BuildLetter()
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(25, 24, 25, 19), Margin = new Padding(0, 0, 8, 0) };
        letterContent = Table(1, 3);
        var layout = (TableLayoutPanel)letterContent;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        subject.AccessibleName = "Тема письма";
        subject.Font = AppFont(17, FontStyle.Bold);
        subject.ScrollBars = ScrollBars.None;
        subject.Margin = new Padding(0, 2, 0, 5);
        layout.Controls.Add(subject, 0, 0);
        layout.Controls.Add(BuildMetadata(), 0, 1);
        letterTabs.Margin = new Padding(0, 8, 0, 0);
        letterTabs.Add("Письмо", htmlPreview);
        letterTabs.Add("Текст", new GravityScrollArea(body));
        letterTabs.Add("Исходник", new GravityScrollArea(source));
        layout.Controls.Add(letterTabs, 0, 2);
        letterContent.Visible = false;

        var open = ActionButton("Выбрать файл", true);
        open.Anchor = AnchorStyles.None;
        open.Click += async (_, _) => await ChooseFileAsync();
        letterEmpty = EmptyState("Откройте письмо", "Перетащите файл сюда или выберите его на диске", open);
        card.Controls.Add(letterContent);
        card.Controls.Add(letterEmpty);
        return card;
    }

    private Control BuildMetadata()
    {
        var metadata = Table(3, 5);
        metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 101));
        metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        AddRow("Отправитель", from, 0, fromLookup);
        AddRow("Получатели", to, 1, toLookup);
        AddRow("Копия", copyRecipients, 2, recipientList);
        AddRow("Ответить на", replyTo, 3);
        AddRow("Дата", date, 4);
        fromLookup.Click += (_, _) => OpenDomainTab((string[])fromLookup.Tag!);
        toLookup.Click += (_, _) => OpenDomainTab((string[])toLookup.Tag!);
        recipientList.Click += (_, _) => ShowRecipientList();
        recipientList.Enabled = false;
        return metadata;

        void AddRow(string label, Control value, int row, Button? button = null)
        {
            metadata.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
            metadata.Controls.Add(Caption(label, 9), 0, row);
            if (value is Label)
                value.Dock = DockStyle.Fill;
            else
                value.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            value.Margin = new Padding(0, 0, 10, 0);
            value.AccessibleName = label;
            metadata.Controls.Add(value, 1, row);
            if (button is not null)
            {
                button.Font = AppFont(8.5f, FontStyle.Bold);
                button.MinimumSize = new Size(70, 23);
                button.Padding = new Padding(4, 0, 4, 0);
                button.Margin = Padding.Empty;
                button.Anchor = AnchorStyles.Right;
                metadata.Controls.Add(button, 2, row);
            }
            else
                metadata.SetColumnSpan(value, 2);
        }
    }

    private static Label RecipientValue() => new()
    {
        AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false, ForeColor = Ink, BackColor = Surface, Font = AppFont(10.5f),
    };

    private Control BuildLinks()
    {
        var pane = Table(1, 4);
        pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        var list = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        links.Dock = DockStyle.Fill;
        links.BackgroundColor = Surface;
        links.BorderStyle = BorderStyle.None;
        links.GridColor = Background;
        links.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        links.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        links.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        links.ColumnHeadersHeight = 37;
        links.EnableHeadersVisualStyles = false;
        links.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Field, ForeColor = Muted, Font = AppFont(9, FontStyle.Bold), Padding = new Padding(8, 0, 0, 0),
        };
        links.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface, ForeColor = Ink, SelectionBackColor = AccentSoft, SelectionForeColor = Ink,
            Padding = new Padding(8, 1, 5, 1),
        };
        links.AlternatingRowsDefaultCellStyle.BackColor = Field;
        links.RowTemplate.Height = 43;
        links.RowHeadersVisible = false;
        links.AllowUserToAddRows = false;
        links.AllowUserToDeleteRows = false;
        links.AllowUserToResizeRows = false;
        links.AllowUserToResizeColumns = false;
        links.ReadOnly = true;
        links.MultiSelect = false;
        links.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        links.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Адрес ссылки", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 61,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        links.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Домен", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 39,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        links.CurrentCellChanged += (_, _) => UpdateLinkActions();
        links.AccessibleName = "Ссылки из письма";
        links.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) UseSelectedLink(); };
        links.Visible = false;
        sourcesEmpty = EmptyState("Ссылок пока нет", "Здесь появятся ссылки из письма");
        list.Controls.Add(new GravityScrollArea(links));
        list.Controls.Add(sourcesEmpty);
        sourcesEmpty.BringToFront();
        pane.Controls.Add(list, 0, 0);

        var linkDetails = Table(1, 2, 6);
        linkDetails.BackColor = Field;
        linkDetails.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        linkDetails.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        linkDetails.Controls.Add(Caption("Полный адрес выбранной ссылки", 9), 0, 0);
        selectedLink.BackColor = Field;
        selectedLink.AccessibleName = "Полный адрес выбранной ссылки";
        selectedLink.ScrollBars = ScrollBars.None;
        linkDetails.Controls.Add(new GravityScrollArea(selectedLink), 0, 1);
        pane.Controls.Add(linkDetails, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        linkLookup.Click += (_, _) => UseSelectedLink();
        linkCopy.Click += (_, _) => CopyText(links.CurrentRow?.Cells[0].Value as string);
        linkLookup.Margin = new Padding(0, 0, 7, 0);
        linkCopy.Margin = Padding.Empty;
        actions.Controls.Add(linkLookup);
        actions.Controls.Add(linkCopy);
        pane.Controls.Add(actions, 0, 2);
        copyAllLinks.Margin = Padding.Empty;
        copyAllLinks.Click += (_, _) => CopyText(string.Join(Environment.NewLine, messageLinks));
        var otherActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        inspectLink.Margin = new Padding(0, 0, 7, 0);
        inspectLink.Click += (_, _) => ShowLinkInspection();
        otherActions.Controls.Add(inspectLink);
        otherActions.Controls.Add(copyAllLinks);
        pane.Controls.Add(otherActions, 0, 3);
        return pane;
    }

    private Control BuildLookup()
    {
        var pane = Table(2, 5);
        pane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pane.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        pane.Controls.Add(Caption("Домен, email или URL", 9, Ink, FontStyle.Bold), 0, 0);
        domainInput.Margin = Padding.Empty;
        domainInput.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await RunLookupAsync();
            }
        };
        var targetRow = Table(2, 1);
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        targetRow.Controls.Add(InputPanel(domainInput), 0, 0);
        domainPicker.MinimumSize = new Size(34, 34);
        domainPicker.Padding = Padding.Empty;
        domainPicker.Margin = Padding.Empty;
        domainPicker.Visible = false;
        domainPicker.Click += (_, _) => ShowDomainChoices();
        targetRow.Controls.Add(domainPicker, 1, 0);
        pane.Controls.Add(targetRow, 0, 1);
        lookupButton.Click += async (_, _) => await RunLookupAsync();
        pane.Controls.Add(lookupButton, 1, 1);
        lookupStatus.Dock = DockStyle.Fill;
        lookupStatus.AutoEllipsis = true;
        pane.Controls.Add(lookupStatus, 0, 2);
        pane.SetColumnSpan(lookupStatus, 2);

        var summary = Table(1, 6, 12);
        summary.BackColor = Field;
        summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        summary.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddField("Домен", summaryDomain, 0);
        AddField("Регистратор", summaryRegistrar, 2);
        AddField("Abuse · почта или форма", summaryAbuse, 4);
        pane.Controls.Add(summary, 0, 3);
        pane.SetColumnSpan(summary, 2);

        var actions = Actions();
        var copy = ActionButton("Копировать сводку");
        copy.Click += (_, _) => CopyText(currentRecord is null ? null :
            $"Домен: {summaryDomain.Text}\r\nРегистратор: {summaryRegistrar.Text}\r\nAbuse: {summaryAbuse.Text}");
        var details = ActionButton("Все данные →");
        details.Click += (_, _) => { dossierInput.Text = domainInput.Text; sideTabs.SelectedIndex = 2; };
        actions.Controls.Add(copy);
        actions.Controls.Add(details);
        pane.Controls.Add(actions, 0, 4);
        pane.SetColumnSpan(actions, 2);
        ShowSummary(null);
        return pane;

        void AddField(string title, TextBox value, int row)
        {
            summary.Controls.Add(Caption(title, 9, Muted, FontStyle.Bold), 0, row);
            value.AccessibleName = title;
            value.BackColor = Field;
            summary.Controls.Add(value.Multiline ? new GravityScrollArea(value) : value, 0, row + 1);
        }
    }

    private Control BuildDossier()
    {
        var pane = Table(1, 6);
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pane.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        pane.Controls.Add(Caption("Домен или IP-адрес", 9, Ink, FontStyle.Bold), 0, 0);
        dossierInput.AccessibleName = "Цель диагностики";
        dossierInput.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        pane.Controls.Add(InputPanel(dossierInput), 0, 1);
        var progress = Caption("Пять проверок запускаются вместе.", 9);
        pane.Controls.Add(progress, 0, 2);

        var titles = new[] { "Регистрация домена", "DNS-записи", "Регистрация IP-сети", "Маршрут", "Доступность сервисов" };
        var sections = Enum.GetValues<DossierSection>();
        var results = new string[titles.Length];
        var cards = Table(1, titles.Length);
        var views = titles.Select(title =>
        {
            var output = ReadOnlyText(multiline: true, monospaced: true);
            output.Text = "Здесь появится результат проверки.";
            output.AccessibleName = title;
            return output;
        }).ToArray();
        string[] tabs = ["Домен", "DNS", "Сеть", "Маршрут", "Сервисы"];
        var display = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, AccessibleName = "Разделы отчёта" };
        var toggles = new StudioButton[titles.Length];
        for (int i = 0; i < titles.Length; i++)
        {
            int index = i;
            var output = views[i];
            var surface = Table(1, 2, 5);
            surface.BackColor = Field;
            surface.Margin = new Padding(0, 0, 0, 5);
            surface.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            surface.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            surface.Controls.Add(Caption(titles[i], 9, Ink, FontStyle.Bold), 0, 0);
            output.BackColor = Field;
            surface.Controls.Add(new GravityScrollArea(output), 0, 1);
            cards.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            cards.Controls.Add(surface, 0, i);
            var toggle = new StudioButton(tabs[i]) { Margin = new Padding(0, 0, 4, 3),
                MinimumSize = new Size(0, 27), Padding = new Padding(7, 0, 7, 0),
                Selected = sections[i] is DossierSection.Dns or DossierSection.NetworkWhois };
            toggles[i] = toggle;
            toggle.Click += (_, _) =>
            {
                if (toggle.Selected && toggles.Count(item => item.Selected) == 1)
                    return;
                toggle.Selected = !toggle.Selected;
                UpdateDisplayedSections();
            };
            display.Controls.Add(toggle);
        }
        UpdateDisplayedSections();
        pane.Controls.Add(display, 0, 3);
        pane.Controls.Add(cards, 0, 4);

        var actions = Actions();
        var run = ActionButton("Проверить всё", true);
        var copy = ActionButton("Копировать");
        copy.AccessibleName = "Копировать весь отчёт";
        copy.Padding = new Padding(8, 0, 8, 0);
        copy.Enabled = false;
        copy.Click += (_, _) => CopyText(string.Join("\r\n\r\n", titles.Zip(results, (title, result) =>
            $"{title}\r\n{result ?? "Ожидание результата…"}")));
        run.Click += async (_, _) =>
        {
            if (dossierRequest is not null)
            {
                dossierRequest.Cancel();
                return;
            }
            var target = DossierLookup.ParseTarget(dossierInput.Text);
            if (target is null)
            {
                progress.Text = "Введите домен, email, URL или IP.";
                return;
            }

            using var request = new CancellationTokenSource();
            dossierRequest = request;
            run.Text = "Отменить";
            dossierInput.Enabled = copy.Enabled = false;
            Array.Clear(results);
            int completed = 0;
            progress.Text = $"Готово: 0 из {sections.Length}";
            foreach (var output in views)
                output.Text = "Выполняется проверка…";
            try
            {
                await Task.WhenAll(sections.Select((section, index) => RunSectionAsync(section, index)));
            }
            finally
            {
                if (!IsDisposed && dossierRequest == request)
                {
                    dossierRequest = null;
                    dossierInput.Enabled = copy.Enabled = true;
                    run.Text = "Проверить всё";
                    progress.Text = request.IsCancellationRequested ? "Проверка отменена." : $"Завершено: {completed} из {sections.Length}";
                }
            }

            async Task RunSectionAsync(DossierSection section, int index)
            {
                string result;
                try
                {
                    result = section == DossierSection.DomainWhois
                        ? await DomainDetailsAsync(target, request.Token)
                        : await dossier.QueryAsync(section, target, request.Token);
                }
                catch (OperationCanceledException)
                {
                    result = request.IsCancellationRequested ? "Проверка отменена." : "Время ожидания истекло.";
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException or
                    Win32Exception or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
                {
                    result = $"Ошибка запроса: {ex.Message}";
                }
                if (IsDisposed || dossierRequest != request)
                    return;
                results[index] = Preview(result);
                views[index].Text = results[index];
                progress.Text = $"Готово: {++completed} из {sections.Length}";
            }
        };
        dossierInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && dossierRequest is null)
            {
                e.SuppressKeyPress = true;
                run.PerformClick();
            }
        };
        actions.Controls.Add(run);
        actions.Controls.Add(copy);
        pane.Controls.Add(actions, 0, 5);
        return pane;

        void UpdateDisplayedSections()
        {
            cards.SuspendLayout();
            for (int i = 0; i < toggles.Length; i++)
            {
                bool shown = toggles[i].Selected;
                toggles[i].AccessibleName = $"{titles[i]}: {(shown ? "показан" : "скрыт")}";
                toggles[i].Invalidate();
                cards.RowStyles[i].SizeType = shown ? SizeType.Percent : SizeType.Absolute;
                cards.RowStyles[i].Height = shown ? 100 : 0;
                cards.GetControlFromPosition(0, i)!.Visible = shown;
            }
            cards.ResumeLayout();
        }
    }

    private async Task<string> DomainDetailsAsync(string target, CancellationToken token)
    {
        if (System.Net.IPAddress.TryParse(target, out _))
            return "Доменная запись WHOIS применима к доменам. Сведения об IP-адресе находятся в разделе «Регистрация IP-сети».";
        if (target == currentRecordTarget && currentRecord is { Message: null } record)
            return record.Report;
        return (await lookup.QueryAsync(target, token)).Report;
    }

    private static FlowLayoutPanel Actions() => new()
    {
        Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0), Margin = Padding.Empty,
    };

    private static Control InputPanel(Control input)
    {
        var panel = new InputSurface
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 34,
            Margin = new Padding(0, 0, 7, 0), Padding = new Padding(10, 0, 10, 0), BackColor = Field,
        };
        input.Margin = Padding.Empty;
        panel.Controls.Add(input, 0, 0);
        input.GotFocus += (_, _) => panel.Invalidate();
        input.LostFocus += (_, _) => panel.Invalidate();
        return panel;
    }

    private sealed class InputSurface : TableLayoutPanel
    {
        public InputSurface()
        {
            ColumnCount = 1;
            RowCount = 1;
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = RoundRect(Rectangle.Inflate(ClientRectangle, -1, -1), LogicalToDeviceUnits(6));
            using var fill = new SolidBrush(BackColor);
            e.Graphics.FillPath(fill, shape);
            if (ContainsFocus)
            {
                using var pen = new Pen(Accent);
                e.Graphics.DrawPath(pen, shape);
            }
        }
    }

    private void ShowSummary(DomainRecord? record)
    {
        currentRecord = record;
        summaryDomain.Text = record?.Domain ?? Empty;
        summaryRegistrar.Text = record?.Registrar ?? Empty;
        summaryAbuse.Text = record is null ? Empty : record.Abuse.Count == 0
            ? "Контакты для жалоб не опубликованы." : string.Join(Environment.NewLine, record.Abuse);
        if (record?.AbuseSource is { } source)
            abuseSourceTip.SetToolTip(summaryAbuse, $"Источник: {source}");
        else
            abuseSourceTip.SetToolTip(summaryAbuse, null);
    }

    private async Task ChooseFileAsync()
    {
        if (loading)
            return;
        using var dialog = new OpenFileDialog
        {
            Title = "Открыть письмо",
            Filter = "Почтовые файлы (*.eml;*.emi;*.txt)|*.eml;*.emi;*.txt|Все файлы (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await LoadFileAsync(dialog.FileName);
    }

    private async Task LoadFileAsync(string path) =>
        await LoadAsync(Path.GetFileName(path), async () =>
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length > MailReader.MaxFileBytes)
                throw new InvalidDataException("Размер файла превышает 32 МБ.");
            var data = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < data.Length)
            {
                int read = await stream.ReadAsync(data.AsMemory(offset));
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return data;
        });

    private async Task PasteAsync()
    {
        if (loading)
            return;
        using var dialog = new Form
        {
            Text = "Вставить исходник письма", ClientSize = new Size(700, 440), MinimumSize = new Size(470, 270),
            StartPosition = FormStartPosition.CenterParent, BackColor = currentPalette.Background, ForeColor = currentPalette.Ink, Font = Font,
        };
        var page = Table(1, 3, 12);
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        dialog.Controls.Add(page);
        page.Controls.Add(Caption("Вставьте письмо с заголовками From, To и Subject.", color: currentPalette.Muted), 0, 0);
        var input = ReadOnlyText(multiline: true, monospaced: true);
        input.ReadOnly = false;
        input.BackColor = currentPalette.Surface;
        input.ForeColor = currentPalette.Ink;
        input.MaxLength = MailReader.MaxFileBytes;
        page.Controls.Add(new GravityScrollArea(input), 0, 1);
        var submit = new StudioButton("Разобрать письмо", true) { Theme = currentPalette };
        submit.Anchor = AnchorStyles.Right;
        submit.Enabled = false;
        submit.DialogResult = DialogResult.OK;
        input.TextChanged += (_, _) => submit.Enabled = !string.IsNullOrWhiteSpace(input.Text);
        page.Controls.Add(submit, 0, 2);
        dialog.HandleCreated += (_, _) => WindowTheme.Apply(dialog, darkTheme, currentPalette.Background, currentPalette.Ink);
        dialog.ActiveControl = input;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var pasted = input.Text;
        await LoadAsync("Вставленный исходник", () => Task.FromResult(Encoding.UTF8.GetBytes(pasted)));
    }

    private async Task LoadAsync(string name, Func<Task<byte[]>> read)
    {
        if (loading)
            return;
        loading = true;
        status.Text = "Разбор письма…";
        UseWaitCursor = true;
        try
        {
            var data = await read();
            if (data.Length == 0 || data.Length > MailReader.MaxFileBytes)
                throw new InvalidDataException("Письмо пустое или его размер превышает 32 МБ.");
            var document = await Task.Run(() => MailReader.Parse(data));
            if (IsDisposed)
                return;
            ShowDocument(document);
            status.Text = $"{name} · ссылок: {document.Links.Count}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            if (!IsDisposed)
            {
                status.Text = "Ошибка чтения · " + name;
                MessageBox.Show(this, ex.Message, "Не удалось открыть письмо", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            loading = false;
            if (!IsDisposed)
                UseWaitCursor = false;
        }
    }

    private void ShowDocument(MailDocument mail)
    {
        letterEmpty.Visible = false;
        letterContent.Visible = true;
        subject.Text = string.IsNullOrWhiteSpace(mail.Subject) ? "Без темы" : mail.Subject;
        date.Text = Display(mail.Date);
        from.Text = Display(mail.From);
        to.Text = AddressSummary(mail.To);
        var copies = string.Join(Environment.NewLine, new[] { mail.Cc, mail.Bcc }.Where(value => value.Length > 0));
        copyRecipients.Text = AddressSummary(copies);
        recipientToolTip.SetToolTip(to, mail.To.Length == 0 ? null : mail.To);
        recipientToolTip.SetToolTip(copyRecipients, copies.Length == 0 ? null : copies);
        var recipientGroups = new[] { ("Кому", mail.To), ("Копия (Cc)", mail.Cc), ("Скрытая копия (Bcc)", mail.Bcc) };
        recipientsReport = string.Join(Environment.NewLine + Environment.NewLine,
            recipientGroups.Where(group => group.Item2.Length > 0).Select(group => group.Item1 + Environment.NewLine + group.Item2));
        recipientList.Enabled = recipientsReport.Length > 0;
        replyTo.Text = Display(mail.ReplyTo);
        body.Text = mail.Body.Length == 0 ? "Текстовая часть письма отсутствует." : Preview(mail.Body);
        source.Text = Preview(mail.Source);

        messageLinks = mail.Links;
        links.Rows.Clear();
        foreach (var url in mail.Links)
        {
            int index = links.Rows.Add(url, DossierLookup.ParseTarget(url) ?? Empty);
            links.Rows[index].Cells[0].ToolTipText = url;
        }
        sideTabs.SetCaption(0, $"Ссылки · {mail.Links.Count}");
        links.Visible = mail.Links.Count > 0;
        sourcesEmpty.Visible = mail.Links.Count == 0;
        var fromDomains = DomainLookup.FromAddresses(mail.From).ToArray();
        var toDomains = DomainLookup.FromAddresses(mail.To).Concat(DomainLookup.FromAddresses(mail.Cc))
            .Concat(DomainLookup.FromAddresses(mail.Bcc)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        fromLookup.Tag = fromDomains;
        toLookup.Tag = toDomains;
        fromLookup.Enabled = fromDomains.Length > 0;
        toLookup.Enabled = toDomains.Length > 0;
        UpdateLinkActions();
        letterTabs.SelectedIndex = mail.Html is null ? 1 : 0;
        _ = htmlPreview.DisplayAsync(mail.Html);
        sideTabs.SelectedIndex = 0;
        ResetLookup();
    }

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? Empty : value;

    private static string AddressSummary(string value)
    {
        var addresses = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return addresses.Length switch { 0 => Empty, 1 => addresses[0], _ => $"{addresses[0]}  (+{addresses.Length - 1})" };
    }

    private void ShowRecipientList()
    {
        using var dialog = new Form { Text = "Получатели письма", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(580, 340), MinimumSize = new Size(360, 240), BackColor = currentPalette.Background,
            ForeColor = currentPalette.Ink, Font = Font, ShowInTaskbar = false };
        dialog.HandleCreated += (_, _) => WindowTheme.Apply(dialog, darkTheme, currentPalette.Background, currentPalette.Ink);
        var content = Table(1, 2, 14);
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var addresses = ReadOnlyText(multiline: true);
        addresses.BackColor = currentPalette.Surface;
        addresses.ForeColor = currentPalette.Ink;
        addresses.Text = recipientsReport;
        content.Controls.Add(new GravityScrollArea(addresses), 0, 0);
        var copy = new StudioButton("Копировать адреса") { Theme = currentPalette, Margin = Padding.Empty };
        copy.Click += (_, _) => CopyText(recipientsReport);
        content.Controls.Add(copy, 0, 1);
        dialog.Controls.Add(content);
        dialog.ShowDialog(this);
    }

    private static string Preview(string value)
    {
        if (value.Length > PreviewLimit)
            value = value[..PreviewLimit] + "\n\n[Предпросмотр сокращён]";
        return value.ReplaceLineEndings(Environment.NewLine);
    }

    private void UseSelectedLink()
    {
        if (links.CurrentRow?.Cells[1].Value is string domain && domain != Empty)
        {
            if (System.Net.IPAddress.TryParse(domain, out _))
            {
                dossierInput.Text = domain;
                sideTabs.SelectedIndex = 2;
                return;
            }
            OpenDomainTab([domain]);
        }
    }

    private void UpdateLinkActions()
    {
        var address = links.CurrentRow?.Cells[0].Value as string;
        selectedLink.Text = address ?? "";
        linkCopy.Enabled = address is not null;
        inspectLink.Enabled = address is not null && RedirectInspector.IsWebLink(address);
        copyAllLinks.Enabled = messageLinks.Count > 0;
        linkLookup.Enabled = links.CurrentRow?.Cells[1].Value is string domain && domain != Empty;
    }

    private void ShowLinkInspection()
    {
        if (links.CurrentRow?.Cells[0].Value is not string address)
            return;

        using var request = new CancellationTokenSource();
        using var dialog = new Form
        {
            Text = "Куда ведёт ссылка", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(630, 380), MinimumSize = new Size(420, 240), BackColor = currentPalette.Background,
            ForeColor = currentPalette.Ink, Font = Font, ShowInTaskbar = false,
        };
        var layout = Table(1, 2, 14);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var report = ReadOnlyText(multiline: true);
        report.BackColor = currentPalette.Surface;
        report.ForeColor = currentPalette.Ink;
        report.Text = "Проверка ссылки…" + Environment.NewLine + address;
        report.AccessibleName = "Перенаправления и ответ сервера";
        layout.Controls.Add(new GravityScrollArea(report), 0, 0);
        var copy = new StudioButton("Копировать результат") { Theme = currentPalette, Enabled = false, Margin = Padding.Empty };
        copy.Click += (_, _) => CopyText(report.Text);
        layout.Controls.Add(copy, 0, 1);
        dialog.Controls.Add(layout);
        dialog.HandleCreated += (_, _) => WindowTheme.Apply(dialog, darkTheme, currentPalette.Background, currentPalette.Ink);
        dialog.FormClosed += (_, _) => request.Cancel();
        dialog.Shown += async (_, _) =>
        {
            try
            {
                var result = await RedirectInspector.InspectAsync(address, request.Token);
                if (!dialog.IsDisposed && !request.IsCancellationRequested)
                {
                    report.Text = Preview(result);
                    copy.Enabled = true;
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        };
        dialog.ShowDialog(this);
    }

    private void ResetLookup()
    {
        activeLookup?.Cancel();
        activeLookup = null;
        lookupButton.Enabled = true;
        domainChoices = [];
        domainPicker.Visible = false;
        domainInput.Text = "";
        currentRecordTarget = null;
        ShowSummary(null);
        lookupStatus.Text = "Введите домен или выберите адрес из письма.";
    }

    private void OpenDomainTab(IEnumerable<string> domains)
    {
        var choices = domains.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (choices.Length == 0)
            return;
        ResetLookup();
        domainChoices = choices;
        domainPicker.Visible = choices.Length > 1;
        domainInput.Text = choices[0];
        sideTabs.SelectedIndex = 1;
        domainInput.Focus();
    }

    private void ShowDomainChoices()
    {
        var palette = currentPalette;
        var choiceGrid = new GravityGrid
        {
            AccessibleName = "Домены из письма", BackColor = palette.Field, ForeColor = palette.Ink,
            BackgroundColor = palette.Field, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.None,
            ColumnHeadersVisible = false, RowHeadersVisible = false, MultiSelect = false, ReadOnly = true,
            AllowUserToAddRows = false, AllowUserToResizeRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        choiceGrid.RowTemplate.Height = LogicalToDeviceUnits(30);
        choiceGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = palette.Field, ForeColor = palette.Ink, SelectionBackColor = palette.Selected,
            SelectionForeColor = palette.Ink, Padding = new Padding(8, 0, 4, 0),
        };
        choiceGrid.Columns.Add(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        foreach (var domain in domainChoices)
            choiceGrid.Rows.Add(domain);

        var viewport = new GravityScrollArea(choiceGrid);
        var host = new ToolStripControlHost(viewport) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty,
            Size = new Size(Math.Max(domainInput.Width, LogicalToDeviceUnits(220)),
                Math.Min(domainChoices.Length, 8) * choiceGrid.RowTemplate.Height) };
        var menu = new ToolStripDropDown { BackColor = palette.Field, Padding = new Padding(4),
            Renderer = new DomainMenuRenderer(palette) };
        menu.Items.Add(host);
        choiceGrid.CellClick += (_, e) => { if (e.RowIndex >= 0) Select(e.RowIndex); };
        choiceGrid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && choiceGrid.CurrentRow is { } row)
            {
                e.SuppressKeyPress = true;
                Select(row.Index);
            }
            else if (e.KeyCode == Keys.Escape)
                menu.Close();
        };
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(domainInput, new Point(0, domainInput.Height + LogicalToDeviceUnits(8)));
        choiceGrid.Focus();

        void Select(int row)
        {
            domainInput.Text = (string?)choiceGrid.Rows[row].Cells[0].Value ?? "";
            menu.Close();
            domainInput.Focus();
        }
    }

    private async Task RunLookupAsync()
    {
        if (activeLookup is not null)
            return;
        var domain = DossierLookup.ParseTarget(domainInput.Text);
        if (domain is null)
        {
            ShowSummary(null);
            lookupStatus.Text = "Введите корректный домен, email или URL.";
            return;
        }

        ShowSummary(null);
        currentRecordTarget = null;
        if (System.Net.IPAddress.TryParse(domain, out _))
        {
            lookupStatus.Text = "Для IP-адреса используйте вкладку «Данные».";
            return;
        }
        using var request = new CancellationTokenSource();
        activeLookup = request;
        lookupButton.Enabled = false;
        lookupStatus.Text = $"Запрашиваю данные для {domain}…";
        try
        {
            var result = await lookup.QueryAsync(domain, request.Token);
            if (!IsDisposed && activeLookup == request)
            {
                currentRecordTarget = domain;
                ShowSummary(result);
                lookupStatus.Text = result.Message?.Split('\r', '\n')[0] ?? string.Empty;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (!IsDisposed && activeLookup == request)
            {
                activeLookup = null;
                lookupButton.Enabled = true;
            }
        }
    }

    private void CopyText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "Буфер обмена занят. Повторите копирование.", "Буфер обмена",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void EnableFileDrop(Control control)
    {
        if (control is WebBrowser) return; // The preview must not navigate to dropped files.
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
                await LoadFileAsync(paths[0]);
        };
        foreach (Control child in control.Controls)
            EnableFileDrop(child);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (currentPalette is { } palette)
            WindowTheme.Apply(this, darkTheme, palette.Background, palette.Ink);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        activeLookup?.Cancel();
        dossierRequest?.Cancel();
        abuseSourceTip.Dispose();
        recipientToolTip.Dispose();
        http.Dispose();
        base.OnFormClosed(e);
    }


    private static Control EmptyState(string title, string hint, Button? action = null)
    {
        var shell = Table(1, 3);
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 49));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 51));

        var content = Table(1, action is null ? 2 : 4);
        content.AutoSize = true;
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.Dock = DockStyle.None;
        content.Anchor = AnchorStyles.None;
        int row = 0;
        if (action is not null)
        {
            var mark = new MailMark(64, AccentSoft, Accent) { Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 16) };
            content.Controls.Add(mark, 0, row++);
        }
        var heading = Caption(title, action is null ? 13 : 22, Ink, FontStyle.Bold);
        heading.Anchor = AnchorStyles.None;
        content.Controls.Add(heading, 0, row++);
        var description = Caption(hint, 10);
        description.Anchor = AnchorStyles.None;
        description.Margin = new Padding(0, 5, 0, 0);
        content.Controls.Add(description, 0, row++);
        if (action is not null)
        {
            action.Margin = new Padding(0, 19, 0, 0);
            content.Controls.Add(action, 0, row);
        }
        shell.Controls.Add(content, 0, 1);
        return shell;
    }

    private static TextBox ReadOnlyText(bool multiline = false, float size = 10.5f, bool monospaced = false) => new GravityTextBox
    {
        Dock = DockStyle.Fill, Multiline = multiline, ReadOnly = true, BackColor = Surface, ForeColor = Ink,
        BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.None,
        Font = monospaced ? new Font("Consolas", 10) : AppFont(size), Margin = Padding.Empty,
        MaxLength = 0, ShortcutsEnabled = true,
    };

    private static Font AppFont(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    private static Label Caption(string text, float size = 10, Color? color = null, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = color ?? Muted, UseMnemonic = false,
        Font = AppFont(size, style), TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
    };

    private static Button ActionButton(string text, bool primary = false) => new StudioButton(text, primary);

    private static TableLayoutPanel Table(int columns, int rows, int padding = 0) => new()
    {
        ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill,
        Margin = Padding.Empty, Padding = new Padding(padding),
    };

    private static GraphicsPath RoundRect(Rectangle bounds, int radius)
    {
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
            return path;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class SurfacePanel : Panel
    {
        internal Color CardBackground = Background;

        public SurfacePanel()
        {
            BackColor = Surface;
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(CardBackground);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(ClientRectangle, LogicalToDeviceUnits(12));
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillPath(brush, path);
        }

    }

    private sealed class MailMark : Control
    {
        private readonly Color fill;
        private readonly Color stroke;
        internal ToolbarPalette? Theme;

        public MailMark(int size, Color fill, Color stroke)
        {
            this.fill = fill;
            this.stroke = stroke;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            Size = new Size(size, size);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var tile = RoundRect(ClientRectangle, Width / 4);
            using var brush = new SolidBrush(fill == AccentSoft ? Theme?.Selected ?? fill : fill);
            e.Graphics.FillPath(brush, tile);
            var icon = new Rectangle(Width * 24 / 100, Height * 31 / 100, Width * 52 / 100, Height * 38 / 100);
            using var path = RoundRect(icon, Width / 16);
            using var pen = new Pen(fill == AccentSoft ? Theme?.SelectedInk ?? stroke : stroke, Math.Max(1.5f, Width / 25f)) { LineJoin = LineJoin.Round };
            e.Graphics.DrawPath(pen, path);
            e.Graphics.DrawLines(pen,
            [
                new Point(icon.Left + 2, icon.Top + 3), new Point(icon.Left + icon.Width / 2, icon.Top + icon.Height * 3 / 5),
                new Point(icon.Right - 2, icon.Top + 3),
            ]);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    private enum ButtonIcon { None, Minimize, Maximize, Restore, Close, Chevron }

    private sealed class DomainMenuRenderer(ToolbarPalette palette) : ToolStripProfessionalRenderer(new DomainMenuColors(palette));

    private sealed class DomainMenuColors(ToolbarPalette palette) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => palette.Field;
        public override Color MenuBorder => palette.Field;
        public override Color MenuItemBorder => palette.Selected;
        public override Color MenuItemSelected => palette.Selected;
    }

    private sealed class StudioButton : Button
    {
        private readonly bool primary;
        private bool hover;
        private bool pressed;
        internal ToolbarPalette? Theme;
        internal ButtonIcon IconKind;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Selected { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsTab { get; init; }

        public StudioButton(string text, bool primary = false, ButtonIcon icon = ButtonIcon.None)
        {
            this.primary = primary;
            IconKind = icon;
            Text = text;
            Font = AppFont(9.5f, FontStyle.Bold);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(0, 36);
            Padding = new Padding(13, 0, 13, 0);
            Margin = new Padding(8, 0, 0, 0);
            Anchor = AnchorStyles.Left;
            Cursor = Cursors.Hand;
            UseMnemonic = false;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Background);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
            using var outline = RoundRect(bounds, LogicalToDeviceUnits(6));
            var field = Theme?.Field ?? Field;
            var selected = Theme?.Selected ?? AccentSoft;
            var fill = !Enabled ? field
                : primary ? pressed ? Color.FromArgb(231, 171, 21) : hover ? Color.FromArgb(247, 188, 33) : Accent
                : !IsTab && (Selected || hover) ? selected
                : IsTab ? hover ? field : Parent?.BackColor ?? Surface : field;
            using var brush = new SolidBrush(fill);
            e.Graphics.FillPath(brush, outline);
            if (IsTab && Selected)
            {
                using var line = new SolidBrush(Accent);
                int height = LogicalToDeviceUnits(3);
                e.Graphics.FillRectangle(line, 4, ClientSize.Height - height, Math.Max(0, ClientSize.Width - 8), height);
            }
            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(primary ? Ink : Accent, LogicalToDeviceUnits(1));
                using var ring = RoundRect(Rectangle.Inflate(bounds, -2, -2), LogicalToDeviceUnits(6));
                e.Graphics.DrawPath(pen, ring);
            }
            var ink = !Enabled ? Theme?.Muted ?? Muted : primary ? Ink : Theme?.Ink ?? Ink;
            if (IconKind == ButtonIcon.None)
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            else
                DrawIcon(e.Graphics, ink);
        }

        private void DrawIcon(Graphics graphics, Color color)
        {
            var state = graphics.Save();
            float scale = DeviceDpi / 96f;
            graphics.TranslateTransform(ClientSize.Width / 2f, ClientSize.Height / 2f);
            graphics.ScaleTransform(scale, scale);
            using var pen = new Pen(color, 1.45f) { LineJoin = LineJoin.Round };
            switch (IconKind)
            {
                case ButtonIcon.Minimize:
                    graphics.DrawLine(pen, -5, 3, 5, 3);
                    break;
                case ButtonIcon.Maximize:
                    graphics.DrawRectangle(pen, -4.5f, -4.5f, 9, 9);
                    break;
                case ButtonIcon.Restore:
                    graphics.DrawLines(pen, [new PointF(-2, -3), new PointF(-2, -5.5f), new PointF(5.5f, -5.5f), new PointF(5.5f, 2), new PointF(3, 2)]);
                    graphics.DrawRectangle(pen, -5, -2.5f, 7.5f, 7.5f);
                    break;
                case ButtonIcon.Close:
                    graphics.DrawLine(pen, -4.5f, -4.5f, 4.5f, 4.5f);
                    graphics.DrawLine(pen, -4.5f, 4.5f, 4.5f, -4.5f);
                    break;
                case ButtonIcon.Chevron:
                    graphics.DrawLines(pen, [new Point(-4, -2), new Point(0, 2), new Point(4, -2)]);
                    break;
            }
            graphics.Restore(state);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    }

    private sealed class PageDeck : TableLayoutPanel
    {
        private readonly FlowLayoutPanel strip = new()
        {
            Dock = DockStyle.Fill, BackColor = Surface, Margin = new Padding(0, 0, 0, 11), WrapContents = false,
        };
        private readonly Panel container = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
        private readonly List<(StudioButton Button, Control Page)> pages = [];
        private int selectedIndex;
        internal event EventHandler? SelectionChanged;

        public PageDeck()
        {
            ColumnCount = 1;
            RowCount = 2;
            Dock = DockStyle.Fill;
            BackColor = Surface;
            Margin = Padding.Empty;
            RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(strip, 0, 0);
            Controls.Add(container, 0, 1);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                selectedIndex = value;
                for (int i = 0; i < pages.Count; i++)
                {
                    var (button, page) = pages[i];
                    button.Selected = i == value;
                    button.Invalidate();
                    page.Visible = i == value;
                }
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Add(string title, Control content)
        {
            int index = pages.Count;
            var button = new StudioButton(title)
            {
                IsTab = true, MinimumSize = new Size(63, 35), Margin = new Padding(0, 0, 4, 0),
                Padding = new Padding(7, 0, 7, 0),
                AccessibleRole = AccessibleRole.PageTab,
            };
            button.Click += (_, _) => SelectedIndex = index;
            content.Dock = DockStyle.Fill;
            pages.Add((button, content));
            strip.Controls.Add(button);
            container.Controls.Add(content);
            SelectedIndex = selectedIndex;
        }

        public void SetCaption(int index, string caption)
        {
            pages[index].Button.Text = caption;
            pages[index].Button.Invalidate();
        }
    }
}
