using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinMonitor.UI;

/// <summary>
/// Single source of truth for colors + dark-mode state. Every form/control reads these static
/// fields instead of hardcoding colors, so light/dark stay consistent app-wide. Initialize()
/// is called at startup and whenever the setting changes; consumers rebuild via SettingsApplied.
/// The semantic Good/Warn/Hot triplet replaces the three divergent palettes that previously
/// lived in MainForm / CompactForm / TrayIconManager.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    // Surfaces
    public static Color WindowBack { get; private set; }
    public static Color Surface { get; private set; }      // panels, toolbars
    public static Color ListBack { get; private set; }     // list/grid backgrounds
    public static Color ChartBack { get; private set; }
    public static Color Border { get; private set; }
    public static Color GridLine { get; private set; }

    // Text
    public static Color Text { get; private set; }
    public static Color SubtleText { get; private set; }

    // Semantic state colors (shared by list rows, compact, tray icons, chart threshold segments)
    public static Color Good { get; private set; }
    public static Color Warn { get; private set; }
    public static Color Hot { get; private set; }

    // Banner (elevation warning)
    public static Color BannerBack { get; private set; }
    public static Color BannerText { get; private set; }

    /// <summary>mode: "auto" (follow Windows), "light", "dark".</summary>
    public static void Initialize(string mode)
    {
        bool dark = mode switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark(),
        };
        IsDark = dark;

        if (dark)
        {
            WindowBack = Color.FromArgb(32, 32, 34);
            Surface = Color.FromArgb(40, 40, 43);
            ListBack = Color.FromArgb(28, 28, 30);
            ChartBack = Color.FromArgb(24, 24, 26);
            Border = Color.FromArgb(70, 70, 74);
            GridLine = Color.FromArgb(52, 52, 56);
            Text = Color.FromArgb(235, 235, 235);
            SubtleText = Color.FromArgb(150, 150, 155);
            Good = Color.FromArgb(95, 210, 120);
            Warn = Color.FromArgb(255, 195, 70);
            Hot = Color.FromArgb(255, 105, 95);
            BannerBack = Color.FromArgb(70, 60, 20);
            BannerText = Color.FromArgb(255, 224, 130);
        }
        else
        {
            WindowBack = SystemColors.Control;
            Surface = Color.White;
            ListBack = SystemColors.Window;
            ChartBack = Color.White;
            Border = Color.FromArgb(0xD0, 0xD0, 0xD0);
            GridLine = Color.FromArgb(0xE3, 0xE3, 0xE3);
            Text = SystemColors.ControlText;
            SubtleText = Color.FromArgb(0x55, 0x55, 0x55);
            Good = Color.FromArgb(46, 140, 80);
            Warn = Color.FromArgb(191, 111, 0);
            Hot = Color.FromArgb(198, 40, 40);
            BannerBack = Color.FromArgb(255, 246, 200);
            BannerText = Color.FromArgb(96, 77, 0);
        }
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Dark title bar via DWM; harmless no-op when unsupported or in light mode.</summary>
    public static void ApplyTitleBar(Form form)
    {
        try
        {
            int useDark = IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref useDark, sizeof(int));
        }
        catch { }
    }

    /// <summary>Applies the theme's menu renderer + colors to a MenuStrip (and its drop-downs).</summary>
    public static void StyleMenu(MenuStrip menu)
    {
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer = new ToolStripProfessionalRenderer(new ThemeColorTable()) { RoundedEdges = false };
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        foreach (ToolStripItem item in menu.Items)
            StyleMenuItem(item);
    }

    private static void StyleMenuItem(ToolStripItem item)
    {
        item.ForeColor = Text;
        if (item is ToolStripMenuItem m)
        {
            m.DropDown.BackColor = Surface;
            m.DropDown.ForeColor = Text;
            foreach (ToolStripItem child in m.DropDownItems)
                StyleMenuItem(child);
        }
    }

    private sealed class ThemeColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Surface;
        public override Color MenuStripGradientEnd => Surface;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemSelected => IsDark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(0xCC, 0xE4, 0xF7);
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color MenuItemPressedGradientBegin => MenuItemSelected;
        public override Color MenuItemPressedGradientEnd => MenuItemSelected;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
