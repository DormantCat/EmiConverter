using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;

namespace EmiConverter;

internal sealed class HtmlMailPreview : Panel
{
    private readonly WebBrowser viewer = new()
    {
        Dock = DockStyle.Fill, AllowWebBrowserDrop = false, IsWebBrowserContextMenuEnabled = false,
        WebBrowserShortcutsEnabled = false, ScriptErrorsSuppressed = true
    };

    public HtmlMailPreview()
    {
        viewer.Navigating += (_, e) => e.Cancel = e.Url?.AbsoluteUri != "about:blank";
        viewer.NewWindow += (_, e) => e.Cancel = true;
        Controls.Add(viewer);
    }

    internal Task DisplayAsync(string? html)
    {
        try { viewer.DocumentText = MailReader.PreviewHtml(html); }
        catch (RegexMatchTimeoutException) { viewer.DocumentText = "<p>Слишком сложная разметка. Откройте вкладку «Текст».</p>"; }
        return Task.CompletedTask;
    }
}

internal static class WindowTheme
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int NoBorder = unchecked((int)0xFFFFFFFE);

    public static void Drag(Form window)
    {
        ReleaseCapture();
        SendMessage(window.Handle, 0x00A1, 2, 0);
    }

    public static void Apply(Form window, bool dark, Color caption, Color ink)
    {
        if (!window.IsHandleCreated)
            return;
        Set(UseImmersiveDarkMode, dark ? 1 : 0);
        Set(BorderColor, NoBorder);
        Set(CaptionColor, ColorTranslator.ToWin32(caption));
        Set(TextColor, ColorTranslator.ToWin32(ink));

        void Set(int attribute, int value) => DwmSetWindowAttribute(window.Handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
