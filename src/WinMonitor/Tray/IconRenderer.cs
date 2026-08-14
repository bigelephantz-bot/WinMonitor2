using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WinMonitor.Tray;

/// <summary>
/// Renders short value strings ("72", "3.4k", "45°C") into tray <see cref="Icon"/>s.
/// The icon comes from <c>Bitmap.GetHicon()</c>; <c>Icon.FromHandle</c> does NOT take
/// ownership of that HICON, so every icon obtained from <see cref="RenderText"/> MUST be
/// returned through <see cref="ReleaseIcon"/> or the GDI handle leaks (the wrapper's
/// Dispose alone is not enough).
/// The canvas matches the shell's actual small-icon size at the current system DPI
/// (SM_CXSMICON/SM_CYSMICON — 16x16 at 100% scaling, 24x24 at 150%), so the taskbar shows our
/// pixels 1:1; rendering 32x32 and letting the shell resample down was one source of blur.
/// The size is computed once and cached: taskbar DPI changes are rare and a stale size
/// until restart is an accepted trade-off.
///
/// Legibility at 16 px is dominated by three choices, all settled by rendering the candidates
/// and comparing pixels rather than by theory: glyphs below <see cref="AntiAliasMinPx"/> are
/// drawn with whole-pixel hinting instead of grayscale AA, no contrast halo is drawn at all,
/// and callers pass digits only — a unit suffix costs glyphs the number cannot spare. See the
/// comments at those sites, and TrayIconManager.FormatShort, for why.
/// </summary>
public static class IconRenderer
{
    private const int SmCxSmIcon = 49; // SM_CXSMICON
    private const int SmCySmIcon = 50; // SM_CYSMICON
    // A 16×16 shell icon needs 5–7 px text for values with visible units (for example,
    // "100W" or "100°C"). Keeping the former 8 px floor clipped those glyphs at 100% DPI.
    private const int MinFontPx = 5;
    private const float ShortTextHeightFill = 0.95f; // 1-2 chars fill ~95% of canvas height

    // With a sparkline present the number shares the canvas: it keeps the top ~70%, the
    // sparkline gets the bottom ~30%. The height-fill factors below are applied to the
    // number's reduced band so the digits never bleed into the graph.
    private const float SparklineBandFraction = 0.30f;

    /// <summary>Edge of the square canvas; see class remarks for the DPI fallback chain.</summary>
    private static readonly int CanvasSize = ComputeCanvasSize();

    // Semibold face keeps thin digit strokes from washing out at tray sizes. Field order
    // matters: _textFamily must be initialized before _baseStyle reads it.
    private static readonly FontFamily _textFamily = CreateTextFamily();
    private static readonly FontStyle _baseStyle =
        _textFamily.Name.Contains("Semibold", StringComparison.OrdinalIgnoreCase)
            ? FontStyle.Regular
            : FontStyle.Bold;

    // Below this fitted size, grayscale anti-aliasing spreads a glyph's few pixels into a
    // low-contrast smear; snapping to whole pixels instead keeps the strokes solid. Above it
    // there is enough room for AA to look smooth rather than mushy. The threshold is on the
    // FITTED size, so a 16 px canvas (100 % DPI) renders crisp while a 24 px one (150 % DPI)
    // uses AA for short strings and still falls back to crisp for cramped 4-glyph ones.
    private const int AntiAliasMinPx = 13;

    // Caches live for the process lifetime; guarded by _gate (renders happen on the UI
    // thread, but locking makes the class safe from any caller).
    private static readonly object _gate = new();
    private static readonly Dictionary<int, Font> _fontCache = new();        // key: px<<1 | bold
    private static readonly Dictionary<int, int> _fittedPxCache = new();     // key: (bucket<<2) | (spark<<1) | bold -> font px
    private static readonly Dictionary<int, SolidBrush> _brushCache = new(); // key: ARGB
    private static readonly Dictionary<int, Pen> _penCache = new();          // key: ARGB (sparkline line pens)
    private static readonly StringFormat _centerFormat = CreateCenterFormat();
    private static GraphicsPath? _badgePath;

    // Reused sparkline vertex buffer (guarded by _gate, which serializes every render). Kept
    // exactly sample-count sized so DrawLines — which draws the whole array — never trails
    // stale points; a steady history length reuses it and allocates nothing.
    private static PointF[] _sparkPoints = System.Array.Empty<PointF>();

    /// <summary>
    /// Renders <paramref name="text"/> centered on a canvas matching the shell small-icon
    /// size. A transparent <paramref name="bg"/> (alpha 0) gives bare colored text; an
    /// opaque one paints a rounded-rect badge behind the text. Caller must release via
    /// <see cref="ReleaseIcon"/>.
    /// </summary>
    public static Icon RenderText(string text, Color fg, Color bg, bool bold)
        => RenderCore(text, fg, bg, bold, ReadOnlySpan<float>.Empty, default);

    /// <summary>
    /// Same as <see cref="RenderText"/> but reserves the bottom <see cref="SparklineBandFraction"/>
    /// of the canvas for an auto-scaled trend line drawn from <paramref name="history"/>
    /// (oldest first) in <paramref name="lineColor"/>. The number is fitted into the
    /// remaining upper band so it never overlaps the graph. Fewer than 2 points draws no
    /// line (identical layout to a plain sparkline-enabled icon). Caller must release via
    /// <see cref="ReleaseIcon"/>.
    /// </summary>
    public static Icon RenderTextWithSparkline(string text, Color fg, Color bg, bool bold,
        ReadOnlySpan<float> history, Color lineColor)
        => RenderCore(text, fg, bg, bold, history, lineColor);

    /// <summary>
    /// Shared renderer. When <paramref name="history"/> is empty the layout is identical to
    /// the historical plain path (the number fills the whole canvas); otherwise the number
    /// keeps the upper band and a sparkline is drawn in the reserved bottom band.
    /// </summary>
    private static Icon RenderCore(string text, Color fg, Color bg, bool bold,
        ReadOnlySpan<float> history, Color lineColor)
    {
        if (string.IsNullOrEmpty(text)) text = "—";
        // Reserve the band only when a sparkline is actually requested. An empty span keeps
        // the plain path byte-for-byte identical (same font fit, same full-canvas bounds).
        bool spark = history.Length >= 2;

        lock (_gate)
        {
            var bmp = new Bitmap(CanvasSize, CanvasSize, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);

                    if (bg.A > 0)
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.FillPath(GetBrush(bg), GetBadgePath());
                        g.SmoothingMode = SmoothingMode.None;
                    }

                    // Measure with AA hinting; the draw hint is chosen per fitted size below.
                    // ClearType is never an option here: its subpixel coverage needs an opaque
                    // background and fringes badly against transparent alpha.
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    // Available height for text; a sparkline reserves the bottom band.
                    float textHeight = spark ? CanvasSize * (1f - SparklineBandFraction) : CanvasSize;
                    SolidBrush fill = GetBrush(fg);

                    // No contrast halo anywhere below. An 8-way 1 px rim sounds protective, but at
                    // tray sizes the offsets overlap into a near-opaque black ring that closes the
                    // counters of 0/4/6/8/9 and fills the gaps between strokes — glyphs turn to mud
                    // on BOTH light and dark taskbars. Saturated state colors carry the contrast;
                    // verified by rendering both variants over each taskbar shade.
                    Font font = PickFont(g, text, bold, spark);
                    ApplyHintFor(g, font);
                    g.DrawString(text, font, fill, new RectangleF(0f, 0f, CanvasSize, textHeight), _centerFormat);

                    if (spark) DrawSparkline(g, history, lineColor);
                }

                // GetHicon copies the pixels into a brand-new HICON, so the bitmap is
                // disposed immediately (finally below). Icon.FromHandle merely wraps the
                // handle without owning it — see ReleaseIcon.
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    return Icon.FromHandle(hIcon);
                }
                catch
                {
                    // The raw HICON is ours from the moment GetHicon returns; if no managed
                    // wrapper takes ownership, this is the only place it can still be destroyed.
                    ReleaseHandle(hIcon);
                    throw;
                }
            }
            finally
            {
                bmp.Dispose();
            }
        }
    }

    /// <summary>Selects whole-pixel hinting for glyphs too small for grayscale AA to help.</summary>
    private static void ApplyHintFor(Graphics g, Font font)
        => g.TextRenderingHint = font.Size < AntiAliasMinPx
            ? TextRenderingHint.SingleBitPerPixelGridFit
            : TextRenderingHint.AntiAliasGridFit;

    /// <summary>
    /// Draws an auto-scaled anti-aliased polyline across the bottom band. Caller guarantees
    /// <paramref name="history"/> has ≥2 points. Runs under <c>_gate</c>.
    /// </summary>
    private static void DrawSparkline(Graphics g, ReadOnlySpan<float> history, Color lineColor)
    {
        int n = history.Length;

        // Auto-scale to the actual value range; a flat series draws a centered horizontal line.
        float min = history[0], max = history[0];
        for (int i = 1; i < n; i++)
        {
            float v = history[i];
            if (v < min) min = v;
            else if (v > max) max = v;
        }
        float span = max - min;

        // Bottom band with a 1px inset so the line never clips against the canvas edge.
        float bandTop = CanvasSize * (1f - SparklineBandFraction);
        float top = bandTop + 1f;
        float bottom = CanvasSize - 1f;
        float height = bottom - top;
        if (height <= 0f) return;   // canvas too small to host a band; skip silently
        float left = 1f;
        float width = CanvasSize - 2f;
        float stepX = n > 1 ? width / (n - 1) : 0f;

        // Exactly n points so the classic DrawLines(Pen, PointF[]) overload (always present)
        // draws the whole array with no trailing stale vertices; reallocate only when n changes.
        if (_sparkPoints.Length != n) _sparkPoints = new PointF[n];
        PointF[] pts = _sparkPoints;
        for (int i = 0; i < n; i++)
        {
            float t = span > 0f ? (history[i] - min) / span : 0.5f;   // flat → mid-band
            float x = left + stepX * i;
            float y = bottom - t * height;   // higher value → higher on screen
            pts[i] = new PointF(x, y);
        }

        var prevMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Pen pen = GetPen(lineColor);
        try { g.DrawLines(pen, pts); }
        catch (Exception) { /* GDI can reject degenerate geometry; the number still rendered */ }
        finally { g.SmoothingMode = prevMode; }
    }

    /// <summary>
    /// Destroys an icon produced by <see cref="RenderText"/>. Order matters: capture the
    /// raw HICON first, Dispose the managed wrapper (which does NOT destroy a FromHandle
    /// handle), then DestroyIcon the handle itself.
    /// </summary>
    public static void ReleaseIcon(Icon icon)
    {
        if (icon is null) return;
        IntPtr handle = IntPtr.Zero;
        try { handle = icon.Handle; }
        catch (ObjectDisposedException) { /* already disposed elsewhere; nothing to destroy safely */ }
        icon.Dispose();
        ReleaseHandle(handle);
    }

    /// <summary>
    /// Destroys a raw HICON that never reached a managed wrapper. Used on the failure path between
    /// <c>GetHicon</c> and <c>Icon.FromHandle</c>, where nothing else can still own it.
    /// </summary>
    private static void ReleaseHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { DestroyIcon(handle); } catch { /* teardown must not throw over a lost handle */ }
    }

    /// <summary>
    /// Shell small-icon edge at the current system DPI. Fallback chain:
    /// GetSystemMetricsForDpi(GetDpiForSystem()) → GetSystemMetrics → 16 scaled by DPI → 16.
    /// </summary>
    private static int ComputeCanvasSize()
    {
        uint dpi = 96;
        try
        {
            uint systemDpi = GetDpiForSystem(); // user32, Win10 1607+
            if (systemDpi > 0) dpi = systemDpi;
            int cx = GetSystemMetricsForDpi(SmCxSmIcon, dpi);
            int cy = GetSystemMetricsForDpi(SmCySmIcon, dpi);
            if (cx > 0 && cy > 0) return Math.Max(cx, cy);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1607 Windows: fall through to the DPI-unaware metrics.
        }

        int fx = GetSystemMetrics(SmCxSmIcon);
        int fy = GetSystemMetrics(SmCySmIcon);
        if (fx > 0 && fy > 0) return Math.Max(fx, fy);

        int scaled = (int)Math.Round(16 * dpi / 96.0);
        return scaled > 0 ? scaled : 16;
    }

    private static FontFamily CreateTextFamily()
    {
        try { return new FontFamily("Segoe UI Semibold"); }
        catch (ArgumentException) { /* face not installed; fall through */ }
        try { return new FontFamily("Segoe UI"); }
        catch (ArgumentException) { return FontFamily.GenericSansSerif; }
    }

    private static StringFormat CreateCenterFormat()
    {
        // GenericTypographic kills the ~1/6-em padding GDI+ adds around strings, so the
        // measured box matches the glyphs and the fitted text can actually fill the canvas.
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
        };
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        return format;
    }

    /// <summary>
    /// Fitted font for <paramref name="text"/>: 1-2 chars fill ~95% of the number's band,
    /// 3+ chars fill ~the full width. When <paramref name="spark"/> is set the band is only
    /// the upper <c>1 - SparklineBandFraction</c> of the canvas, so the fit is cached under a
    /// separate key. Binary-searched once per (length bucket, bold, spark) and cached; a wider
    /// string in the same bucket shrinks the cached size so the bucket settles on a stable
    /// worst-case fit.
    /// </summary>
    private static Font PickFont(Graphics g, string text, bool bold, bool spark)
    {
        int bucket = text.Length < 4 ? text.Length : 4;
        int key = (bucket << 2) | (spark ? 2 : 0) | (bold ? 1 : 0);
        if (!_fittedPxCache.TryGetValue(key, out int px))
        {
            px = FitFontPx(g, text, bucket, bold, spark);
            _fittedPxCache[key] = px;
        }

        while (px > MinFontPx && !Fits(g, text, bucket, GetFont(px, bold), spark))
        {
            px--;
            _fittedPxCache[key] = px;
        }
        return GetFont(px, bold);
    }

    /// <summary>
    /// Largest pixel size in [MinFontPx, 2*CanvasSize] whose measured box fits. Cold path
    /// (once per bucket), so probe fonts are created and disposed instead of cached.
    /// </summary>
    private static int FitFontPx(Graphics g, string text, int bucket, bool bold, bool spark)
    {
        FontStyle style = bold ? FontStyle.Bold : _baseStyle;
        int lo = MinFontPx;
        int hi = CanvasSize * 2;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            bool fits;
            using (var probe = new Font(_textFamily, mid, style, GraphicsUnit.Pixel))
            {
                fits = Fits(g, text, bucket, probe, spark);
            }
            if (fits) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private static bool Fits(Graphics g, string text, int bucket, Font font, bool spark)
    {
        SizeF measured = g.MeasureString(text, font, PointF.Empty, _centerFormat);
        // A sparkline steals the bottom band, so the number's vertical budget shrinks with it.
        float band = spark ? CanvasSize * (1f - SparklineBandFraction) : CanvasSize;
        float maxHeight = bucket <= 2 ? band * ShortTextHeightFill : band;
        return measured.Width <= CanvasSize && measured.Height <= maxHeight;
    }

    private static Font GetFont(int px, bool bold)
    {
        int key = (px << 1) | (bold ? 1 : 0);
        if (!_fontCache.TryGetValue(key, out var font))
        {
            FontStyle style = bold ? FontStyle.Bold : _baseStyle;
            font = new Font(_textFamily, px, style, GraphicsUnit.Pixel);
            _fontCache[key] = font;
        }
        return font;
    }

    private static SolidBrush GetBrush(Color color)
    {
        int key = color.ToArgb();
        if (!_brushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(color);
            _brushCache[key] = brush;
        }
        return brush;
    }

    /// <summary>
    /// Cached 1px sparkline pen per color (process lifetime, like the font/brush caches).
    /// Reused across renders so a redraw never allocates or leaks a Pen.
    /// </summary>
    private static Pen GetPen(Color color)
    {
        int key = color.ToArgb();
        if (!_penCache.TryGetValue(key, out var pen))
        {
            pen = new Pen(color, 1f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            _penCache[key] = pen;
        }
        return pen;
    }

    private static GraphicsPath GetBadgePath()
    {
        if (_badgePath is not null) return _badgePath;
        // Corner radius scales with the canvas (6px at the old 32px canvas = 3/16).
        int radius = Math.Max(2, CanvasSize * 3 / 16);
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(CanvasSize - d, 0, d, d, 270, 90);
        path.AddArc(CanvasSize - d, CanvasSize - d, d, d, 0, 90);
        path.AddArc(0, CanvasSize - d, d, d, 90, 90);
        path.CloseFigure();
        _badgePath = path;
        return path;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
