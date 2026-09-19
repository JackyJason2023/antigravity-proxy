using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AntigravityProxyInstaller;

/// <summary>Severity used by chips, banners and status rows.</summary>
internal enum StatusKind
{
    Neutral,
    Success,
    Warning,
    Failure
}

internal readonly record struct StatusPalette(Color Background, Color Border, Color Text, Color Dot);

/// <summary>
/// Shared design tokens: colours, spacing scale, fonts and text helpers. Every visual
/// detail of the custom controls funnels through this class so the window stays consistent.
/// </summary>
/// <remarks>
/// Labels, cards and banners draw their text through GDI (<see cref="TextRenderer"/>) with the
/// same flags used for measuring. Glyphs that sit on a saturated fill — the step badge, the
/// banner symbol — go through <see cref="DrawGlyphCentered"/>, which measures and draws with the
/// same GDI+ engine so the ink really is centred.
/// </remarks>
internal static class Theme
{
    // ── DPI ─────────────────────────────────────────────────────────────
    // Every number in this class and in MainForm is a design pixel measured at 96 DPI. GDI point
    // sizes grow with the monitor DPI on their own, so the shapes have to be multiplied by the
    // same factor: at 150 % a 22 px badge circle would otherwise hold a 24 px line box and read
    // as "off centre". MainForm scales the control tree with ApplyDensityScale(); the numbers
    // used inside OnPaint cannot be reached that way, so they go through S().

    /// <summary>Device pixels per design pixel. 1 until <see cref="InitializeScale"/> runs.</summary>
    public static float Scale { get; private set; } = 1F;

    public static void InitializeScale(float scale) => Scale = Math.Clamp(scale, 1F, 4F);

    /// <summary>
    /// Scale factor of the monitor the desktop is on, usable before any window exists.
    /// </summary>
    public static float DetectScale()
    {
        try
        {
            using var probe = Graphics.FromHwnd(IntPtr.Zero);
            return probe.DpiX > 0 ? probe.DpiX / 96F : Scale;
        }
        catch (OutOfMemoryException)
        {
            // A DC could not be obtained; keep whatever was configured previously.
            return Scale;
        }
    }

    /// <summary>Scales a design pixel count for custom painting.</summary>
    public static int S(int designPixels) => (int)Math.Round(designPixels * Scale);

    public static float S(float designPixels) => designPixels * Scale;

    // ── Spacing scale (4/8 rhythm) ──────────────────────────────────────
    public const int Space4 = 4;
    public const int Space8 = 8;
    public const int Space12 = 12;
    public const int Space16 = 16;
    public const int Space20 = 20;

    /// <summary>Vertical gap between two rows of controls inside a card.</summary>
    public const int RowGap = 10;

    public const int CardSpacing = 14;
    public const int CardPaddingX = 18;
    public const int CardPaddingBottom = 16;
    public const int CardHeaderHeight = 46;
    public const int ButtonHeight = 32;

    /// <summary>
    /// The feedback strip owns a permanent row of this height. Keeping it fixed is what stops
    /// the cards from moving when a message appears or disappears. 72 px = the three wrapped lines
    /// a long error message needs at 9 pt (3 x 24) plus the banner's own 12 px top and bottom
    /// padding — anything less and the third line would be cut off without a trace.
    /// </summary>
    public const int BannerSlotHeight = 72;

    /// <summary>
    /// Height of the status line under the 获取 button. The hint text and the download progress
    /// group swap places inside this one permanent row, so card ② keeps a constant height and
    /// nothing below it moves when a download starts or ends.
    /// </summary>
    public const int ProgressRowHeight = 40;

    /// <summary>
    /// Column width for the monospaced "1.2 MB / 3.4 MB · 35 %" readout. Measured: the longest
    /// string it ever holds ("   9.3 GB /    9.3 GB  99%") is 286 px at 150 %, plus the 18 px
    /// right margin.
    /// </summary>
    public const int ProgressReadoutWidth = 210;

    public static readonly Color WindowBackground = Color.FromArgb(245, 246, 248);
    public static readonly Color CardBackground = Color.White;
    public static readonly Color CardBorder = Color.FromArgb(226, 230, 235);

    public static readonly Color TextPrimary = Color.FromArgb(31, 41, 55);

    // Grey-on-grey was the main accessibility defect in the first pass: the old secondary and
    // tertiary tones measured 4.8:1 and 2.5:1 on white. The values below keep the three-step
    // hierarchy while every one of them clears 4.5:1 on white, on the window fill and on the
    // neutral chip fill.
    public static readonly Color TextSecondary = Color.FromArgb(71, 85, 105);
    public static readonly Color TextTertiary = Color.FromArgb(91, 103, 119);

    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    public static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);

    public static readonly Color Success = Color.FromArgb(22, 163, 74);
    public static readonly Color Warning = Color.FromArgb(217, 119, 6);
    public static readonly Color Failure = Color.FromArgb(220, 38, 38);

    public static readonly Color FieldBackground = Color.FromArgb(249, 250, 251);
    public static readonly Color FieldText = Color.FromArgb(55, 65, 81);

    // ── Secondary buttons ───────────────────────────────────────────────
    // #7D8CA3 on white is 3.4:1, which clears the 3:1 non-text contrast floor for a control
    // boundary. The previous #D6DAE0 outline measured only 1.4:1 and read as "no border".
    public static readonly Color SecondaryButtonFill = Color.FromArgb(248, 250, 252);
    public static readonly Color SecondaryButtonFillHover = Color.FromArgb(241, 245, 249);
    public static readonly Color SecondaryButtonFillPressed = Color.FromArgb(226, 232, 240);
    public static readonly Color SecondaryButtonBorder = Color.FromArgb(125, 140, 163);
    public static readonly Color SecondaryButtonBorderActive = Color.FromArgb(100, 116, 139);

    public static readonly Color ButtonHoverNeutral = Color.FromArgb(243, 244, 246);
    public static readonly Color ButtonPressedNeutral = Color.FromArgb(229, 231, 235);
    public static readonly Color ButtonDisabledBackground = Color.FromArgb(234, 236, 239);

    // Disabled text is exempt from WCAG 1.4.3, but at 2.0:1 the old tone was simply unreadable
    // rather than quiet. #6B7280 on the disabled fill is 4.1:1: still clearly inactive.
    public static readonly Color ButtonDisabledText = Color.FromArgb(107, 114, 128);
    public static readonly Color ButtonSecondaryText = Color.FromArgb(55, 65, 81);

    private static readonly string FamilyName = ResolveFamilyName();

    public static Font Body { get; } = Create(FamilyName, 9F);
    public static Font BodyBold { get; } = Create(FamilyName, 9F, FontStyle.Bold);
    public static Font Small { get; } = Create(FamilyName, 8.25F);
    public static Font SmallBold { get; } = Create(FamilyName, 8.25F, FontStyle.Bold);
    public static Font CardTitle { get; } = Create(FamilyName, 10.5F, FontStyle.Bold);
    public static Font WindowTitle { get; } = Create(FamilyName, 14.5F, FontStyle.Bold);

    /// <summary>Tabular figures for readouts that change while the user watches them.</summary>
    public static Font Mono { get; } = Create(ResolveMonoFamily(), 8.75F);

    /// <summary>Flags shared by every single-line label-like drawing call.</summary>
    public const TextFormatFlags Line =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    public static StatusPalette Palette(StatusKind kind) => kind switch
    {
        StatusKind.Success => new StatusPalette(Color.FromArgb(236, 253, 245), Color.FromArgb(187, 247, 208), Color.FromArgb(21, 128, 61), Success),
        StatusKind.Warning => new StatusPalette(Color.FromArgb(255, 251, 235), Color.FromArgb(253, 230, 138), Color.FromArgb(180, 83, 9), Warning),
        StatusKind.Failure => new StatusPalette(Color.FromArgb(254, 242, 242), Color.FromArgb(254, 202, 202), Color.FromArgb(185, 28, 28), Failure),
        _ => new StatusPalette(Color.FromArgb(241, 243, 246), Color.FromArgb(203, 213, 225), TextSecondary, TextTertiary)
    };

    public static string Symbol(StatusKind kind) => kind switch
    {
        StatusKind.Success => "✓",
        StatusKind.Warning => "!",
        StatusKind.Failure => "✗",
        _ => "i"
    };

    /// <summary>Measures with the same engine that draws, so centring is exact.</summary>
    public static Size Measure(string text, Font font) => TextRenderer.MeasureText(
        text,
        font,
        new Size(int.MaxValue, int.MaxValue),
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

    public static void DrawText(
        Graphics graphics,
        string text,
        Font font,
        Rectangle bounds,
        Color color,
        TextFormatFlags flags)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        TextRenderer.DrawText(graphics, text, font, bounds, color, flags | TextFormatFlags.NoPadding);
    }

    // ── Centred glyphs ──────────────────────────────────────────────────
    // Step numbers and status symbols sit on a saturated fill, which changes two things:
    // ClearType must not be used, because its sub-pixel fringes read as coloured fuzz on blue
    // and green; and the *ink* box has to be centred, not the line box — a digit has no
    // descender, so centring the line box leaves it visibly high (the old BadgeTextOffsetY
    // constant was a hand-tuned correction for exactly that, and it only held at one DPI).

    private static readonly StringFormat GlyphFormat = CreateGlyphFormat();

    private static readonly Dictionary<string, RectangleF> InkBoxes = new(StringComparer.Ordinal);

    private static StringFormat CreateGlyphFormat()
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        format.Trimming = StringTrimming.None;
        return format;
    }

    /// <summary>
    /// Draws <paramref name="text"/> so that the pixels it actually covers are centred inside
    /// <paramref name="bounds"/>.
    /// </summary>
    public static void DrawGlyphCentered(
        Graphics graphics,
        string text,
        Font font,
        Rectangle bounds,
        Color color)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var ink = MeasureInk(graphics, text, font);
        var left = bounds.X + ((bounds.Width - ink.Width) / 2F) - ink.X;
        var top = bounds.Y + ((bounds.Height - ink.Height) / 2F) - ink.Y;

        var previous = graphics.TextRenderingHint;
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        try
        {
            using var brush = new SolidBrush(color);
            graphics.DrawString(text, font, brush, new RectangleF(left, top, bounds.Width, bounds.Height), GlyphFormat);
        }
        finally
        {
            graphics.TextRenderingHint = previous;
        }
    }

    /// <summary>
    /// Covers the ink rectangle of a glyph relative to the layout origin, measured at the
    /// graphics' own DPI. Rendered once into a transparent scratch bitmap and cached.
    /// </summary>
    private static RectangleF MeasureInk(Graphics graphics, string text, Font font)
    {
        var key = string.Concat(text, "|", font.Name, "|", font.SizeInPoints.ToString("0.0"), "|",
            ((int)font.Style).ToString(), "|", graphics.DpiY.ToString("0"));
        if (InkBoxes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        const int pad = 8;
        const int maxSide = 4096;

        // The scratch bitmap has to be as wide as the string it holds. It used to be a fixed 48px
        // square — 32px once the padding is taken off — which silently clipped every caption wider
        // than that: the scan reported the ink box of the first few glyphs, and centring that
        // fraction put the caption in the right half of the button.
        var extent = graphics.MeasureString(text, font, new SizeF(maxSide, maxSide), GlyphFormat);
        var width = ClampedSide(extent.Width, pad, maxSide);
        var height = ClampedSide(extent.Height, pad, maxSide);
        var measured = RectangleF.Empty;
        using (var scratch = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            scratch.SetResolution(graphics.DpiX, graphics.DpiY);
            using var canvas = Graphics.FromImage(scratch);
            canvas.Clear(Color.Transparent);
            canvas.TextRenderingHint = TextRenderingHint.AntiAlias;
            canvas.SmoothingMode = SmoothingMode.None;
            using var black = new SolidBrush(Color.Black);
            canvas.DrawString(
                text,
                font,
                black,
                new RectangleF(pad, pad, width - (pad * 2), height - (pad * 2)),
                GlyphFormat);

            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;

            // Locked bits rather than GetPixel: a caption is now measured over its whole width,
            // and the per-pixel marshalling would put a visible hitch on the first paint.
            var bits = scratch.LockBits(
                new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var stride = bits.Stride;
                var buffer = new byte[stride * height];
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0, buffer, 0, buffer.Length);

                for (var y = 0; y < height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        if (buffer[row + (x * 4) + 3] < 24)
                        {
                            continue;
                        }

                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
            finally
            {
                scratch.UnlockBits(bits);
            }

            if (maxX >= minX)
            {
                measured = new RectangleF(minX - pad, minY - pad, maxX - minX + 1, maxY - minY + 1);
            }
        }

        InkBoxes[key] = measured;
        return measured;
    }

    /// <summary>Scratch bitmap size for a measured extent: the text plus a margin on both sides.</summary>
    private static int ClampedSide(float extent, int pad, int maxSide) =>
        Math.Min(maxSide, Math.Max(pad * 6, ((int)Math.Ceiling(extent)) + (pad * 2)));

    public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var clipped = new Rectangle(bounds.X, bounds.Y, Math.Max(1, bounds.Width - 1), Math.Max(1, bounds.Height - 1));
        var arc = new RectangleF(clipped.X, clipped.Y, diameter, diameter);

        path.AddArc(arc, 180, 90);
        arc.X = clipped.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = clipped.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = clipped.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Draws a rounded outline that survives the control's rounded <see cref="Region"/>: the
    /// path is pushed half a pen width inside the bounds, otherwise the clip eats the outer
    /// half of the stroke and a 1px border visually becomes a 0.5px one.
    /// </summary>
    public static void DrawRoundedBorder(
        Graphics graphics,
        Rectangle bounds,
        int radius,
        Color color,
        float width)
    {
        var inset = (int)Math.Ceiling(width / 2F);
        var inner = new Rectangle(
            bounds.X + inset,
            bounds.Y + inset,
            bounds.Width - 1 - (inset * 2),
            bounds.Height - 1 - (inset * 2));
        if (inner.Width <= 0 || inner.Height <= 0)
        {
            return;
        }

        using var path = RoundedPath(inner, Math.Max(0, radius - inset));
        using var pen = new Pen(color, width);
        graphics.DrawPath(pen, path);
    }

    /// <summary>
    /// Replaces the control's clip region with a rounded rectangle. The previous region
    /// is disposed only after the swap, so the control never owns a disposed GDI handle.
    /// </summary>
    public static void ApplyRoundedRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0)
        {
            return;
        }

        Region region;
        using (var path = RoundedPath(new Rectangle(0, 0, control.Width, control.Height), radius))
        {
            region = new Region(path);
        }

        var previous = control.Region;
        control.Region = region;
        previous?.Dispose();
    }

    private static Font Create(string family, float size, FontStyle style = FontStyle.Regular) => new(family, size, style);

    private static string ResolveFamilyName()
    {
        foreach (var candidate in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
        {
            using var probe = new Font(candidate, 9F);
            if (string.Equals(probe.FontFamily.Name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return FontFamily.GenericSansSerif.Name;
    }

    private static string ResolveMonoFamily()
    {
        foreach (var candidate in new[] { "Cascadia Mono", "Consolas", "Courier New" })
        {
            using var probe = new Font(candidate, 9F);
            if (string.Equals(probe.FontFamily.Name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return FontFamily.GenericMonospace.Name;
    }
}

/// <summary>Container that paints through a buffer, so relayouts never flash the background.</summary>
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }
}

/// <summary>Table layout container with the same double buffering as <see cref="BufferedPanel"/>.</summary>
internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }
}

/// <summary>Flow layout container with the same double buffering as <see cref="BufferedPanel"/>.</summary>
internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }
}

/// <summary>
/// Rounded content card with a step badge, a title and a right-aligned status chip.
/// The body area below the header is exposed through <see cref="Panel.Padding"/>.
/// </summary>
internal sealed class CardPanel : Panel
{
    // Design pixels, i.e. the numbers as they read at 96 DPI. Bounds, padding and margins are
    // handed over unscaled because MainForm's density pass multiplies them by the device scale;
    // anything used inside OnPaint goes through the scaled readers below, because that pass
    // cannot reach into a paint call.
    private const int DesignSidePadding = Theme.CardPaddingX;
    private const int DesignHeaderHeight = Theme.CardHeaderHeight;
    private const int DesignCornerRadius = 10;
    private const int DesignBadgeDiameter = 22;
    private const int DesignChipHeight = 26;
    private const int DesignChipDotDiameter = 7;

    private static int SidePadding => Theme.S(DesignSidePadding);
    private static int HeaderHeight => Theme.S(DesignHeaderHeight);
    private static int CornerRadius => Theme.S(DesignCornerRadius);
    private static int BadgeDiameter => Theme.S(DesignBadgeDiameter);
    private static int ChipHeight => Theme.S(DesignChipHeight);
    private static int ChipDotDiameter => Theme.S(DesignChipDotDiameter);

    private string _badge = string.Empty;
    private string _title = string.Empty;
    private string _statusText = string.Empty;
    private StatusKind _statusKind = StatusKind.Neutral;
    private bool _highlight;

    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, Theme.CardSpacing);
        Padding = new Padding(DesignSidePadding, DesignHeaderHeight, DesignSidePadding, Theme.CardPaddingBottom);
        MinimumSize = new Size(0, DesignHeaderHeight + 40);
    }

    public string Badge
    {
        get => _badge;
        set
        {
            if (_badge == value)
            {
                return;
            }

            _badge = value;
            Invalidate();
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            Invalidate();
        }
    }

    public StatusKind StatusKind
    {
        get => _statusKind;
        set
        {
            if (_statusKind == value)
            {
                return;
            }

            _statusKind = value;
            Invalidate();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            Invalidate();
        }
    }

    /// <summary>Draws an accent border while the user is dragging a shortcut over the window.</summary>
    public bool Highlight
    {
        get => _highlight;
        set
        {
            if (_highlight == value)
            {
                return;
            }

            _highlight = value;
            Invalidate();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, Width, Height);
        using (var path = Theme.RoundedPath(bounds, CornerRadius))
        using (var background = new SolidBrush(Theme.CardBackground))
        {
            graphics.FillPath(background, path);
        }

        Theme.DrawRoundedBorder(
            graphics,
            bounds,
            CornerRadius,
            _highlight ? Theme.Accent : Theme.CardBorder,
            Theme.S(_highlight ? 2F : 1F));

        DrawHeader(graphics);
    }

    private void DrawHeader(Graphics graphics)
    {
        var header = new Rectangle(0, 0, Width, HeaderHeight);
        var left = SidePadding;

        if (!string.IsNullOrEmpty(_badge))
        {
            var centerY = HeaderHeight / 2;
            var circle = new Rectangle(left, centerY - (BadgeDiameter / 2), BadgeDiameter, BadgeDiameter);
            using (var badgeBrush = new SolidBrush(Theme.Accent))
            {
                graphics.FillEllipse(badgeBrush, circle);
            }

            // Centring is measured from the ink, not from the font box, so no DPI-specific
            // optical offset constant is needed any more.
            Theme.DrawGlyphCentered(graphics, _badge, Theme.SmallBold, circle, Color.White);

            left += BadgeDiameter + Theme.S(Theme.Space12);
        }

        var titleBounds = new Rectangle(left, header.Y, Math.Max(1, Width - left - SidePadding), header.Height);
        Theme.DrawText(
            graphics,
            _title,
            Theme.CardTitle,
            titleBounds,
            Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        if (string.IsNullOrEmpty(_statusText))
        {
            return;
        }

        DrawStatusChip(graphics, header);
    }

    private void DrawStatusChip(Graphics graphics, Rectangle header)
    {
        var palette = Theme.Palette(_statusKind);
        var textSize = Theme.Measure(_statusText, Theme.SmallBold);
        var height = ChipHeight;
        var dotDiameter = ChipDotDiameter;
        var gap = Theme.S(Theme.Space8);
        var inset = Theme.S(Theme.Space12);
        var width = textSize.Width + dotDiameter + gap + inset + Theme.S(Theme.Space16);
        var chip = new Rectangle(Width - SidePadding - width, (header.Height - height) / 2, width, height);

        using (var path = Theme.RoundedPath(chip, height / 2))
        using (var background = new SolidBrush(palette.Background))
        {
            graphics.FillPath(background, path);
        }

        Theme.DrawRoundedBorder(graphics, chip, height / 2, palette.Border, Theme.S(1F));

        var centerY = chip.Y + (chip.Height / 2);
        using (var dot = new SolidBrush(palette.Dot))
        {
            graphics.FillEllipse(dot, chip.X + inset, centerY - (dotDiameter / 2), dotDiameter, dotDiameter);
        }

        var textBounds = new Rectangle(
            chip.X + inset + dotDiameter + gap,
            chip.Y,
            Math.Max(1, chip.Width - (inset + dotDiameter + gap + Theme.S(Theme.Space8))),
            chip.Height);
        Theme.DrawText(
            graphics,
            _statusText,
            Theme.SmallBold,
            textBounds,
            palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private void UpdateRegion()
    {
        Theme.ApplyRoundedRegion(this, CornerRadius);
    }
}

internal enum ButtonVariant
{
    Primary,
    Secondary,
    Success,
    Ghost
}

/// <summary>Flat, rounded push button with hover/press feedback and a disabled state.</summary>
internal sealed class FlatButton : Button
{
    private const int DesignCornerRadius = 7;
    private const int DesignFocusInset = 2;

    private static int CornerRadius => Theme.S(DesignCornerRadius);
    private static int FocusInset => Theme.S(DesignFocusInset);
    private static float ScaledBorderWidth => Theme.S(1.5F);
    private static float ScaledRingWidth => Theme.S(2F);

    private bool _hovered;
    private bool _pressed;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.Selectable, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        Margin = new Padding(0);
        UseMnemonic = false;

        // Grow with the label so translated or lengthened text is never clipped.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Theme.Space16, 0, Theme.Space16, 0);
        MinimumSize = new Size(96, Theme.ButtonHeight);
        MaximumSize = new Size(0, Theme.ButtonHeight);
    }

    public ButtonVariant Variant { get; set; } = ButtonVariant.Secondary;

    /// <summary>Locks the button to a fixed height, e.g. when it fills a docked cell.</summary>
    public FlatButton UseFixedHeight(int height)
    {
        AutoSize = false;
        MaximumSize = Size.Empty;
        MinimumSize = new Size(MinimumSize.Width, height);
        Height = height;
        return this;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    /// <summary>
    /// Which text engine draws the caption: grayscale antialiased glyphs for the white-on-colour
    /// variants, ClearType for the light ones.
    ///
    /// This depends on the variant and on nothing else — not on hover, not on press, not on the
    /// fill the state happens to end up with, and not on <c>Enabled</c>. The test used to be "fill
    /// brightness below 0.5", and the accent crosses that line between its resting colour (0.533)
    /// and its hover colour (0.480), so those two states picked two different engines. Each engine
    /// centres a string by its own metrics, so the caption slid sideways the moment the pointer
    /// entered the button, and went back on exit. Measured: the same rule keeps the disabled state
    /// still as well (it used to nudge the longest caption 5.5px, because ClearType lays the string
    /// out by advance width while the glyph path centres the ink box).
    /// </summary>
    private bool UseGlyphText => Variant is ButtonVariant.Primary or ButtonVariant.Success;

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var colors = ResolveColors();
        var bounds = new Rectangle(0, 0, Width, Height);
        using (var path = Theme.RoundedPath(bounds, CornerRadius))
        using (var background = new SolidBrush(colors.Fill))
        {
            graphics.FillPath(background, path);
        }

        if (colors.BorderWidth > 0F)
        {
            Theme.DrawRoundedBorder(graphics, bounds, CornerRadius, colors.Border, colors.BorderWidth);
        }

        if (UseGlyphText)
        {
            // White captions on the accent and success fills: ClearType would paint cyan and
            // orange fringes into the coloured background, and its grid fitting shifts the
            // glyphs by a pixel depending on where the button happens to sit.
            Theme.DrawGlyphCentered(graphics, Text ?? string.Empty, Font, bounds, colors.Text);
        }
        else
        {
            Theme.DrawText(
                graphics,
                Text ?? string.Empty,
                Font,
                bounds,
                colors.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        if (Focused && ShowFocusCues && Enabled)
        {
            DrawFocusRing(graphics, bounds);
        }
    }

    /// <summary>A 2px ring in the accent colour, readable on every variant.</summary>
    private void DrawFocusRing(Graphics graphics, Rectangle bounds)
    {
        var ring = new Rectangle(
            FocusInset,
            FocusInset,
            Math.Max(1, bounds.Width - 1 - (FocusInset * 2)),
            Math.Max(1, bounds.Height - 1 - (FocusInset * 2)));
        using var path = Theme.RoundedPath(ring, Math.Max(0, CornerRadius - FocusInset));
        using var pen = new Pen(
            Variant is ButtonVariant.Primary or ButtonVariant.Success ? Color.White : Theme.Accent,
            ScaledRingWidth);
        graphics.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        Theme.ApplyRoundedRegion(this, CornerRadius);
    }

    private (Color Fill, Color Text, Color Border, float BorderWidth) ResolveColors()
    {
        if (!Enabled)
        {
            return (Theme.ButtonDisabledBackground, Theme.ButtonDisabledText, Theme.ButtonDisabledBackground, 0F);
        }

        if (_pressed)
        {
            return Variant switch
            {
                ButtonVariant.Primary => (Theme.AccentPressed, Color.White, Theme.AccentPressed, 0F),
                ButtonVariant.Success => (Color.FromArgb(22, 101, 52), Color.White, Color.FromArgb(22, 101, 52), 0F),
                ButtonVariant.Ghost => (Theme.ButtonPressedNeutral, Theme.Accent, Theme.SecondaryButtonBorderActive, ScaledBorderWidth),
                _ => (Theme.SecondaryButtonFillPressed, Theme.ButtonSecondaryText, Theme.SecondaryButtonBorderActive, ScaledBorderWidth)
            };
        }

        if (_hovered)
        {
            return Variant switch
            {
                ButtonVariant.Primary => (Theme.AccentHover, Color.White, Theme.AccentHover, 0F),
                ButtonVariant.Success => (Color.FromArgb(21, 128, 61), Color.White, Color.FromArgb(21, 128, 61), 0F),
                ButtonVariant.Ghost => (Theme.ButtonHoverNeutral, Theme.Accent, Theme.SecondaryButtonBorder, ScaledBorderWidth),
                _ => (Theme.SecondaryButtonFillHover, Theme.ButtonSecondaryText, Theme.SecondaryButtonBorderActive, ScaledBorderWidth)
            };
        }

        return Variant switch
        {
            ButtonVariant.Primary => (Theme.Accent, Color.White, Theme.Accent, 0F),
            ButtonVariant.Success => (Theme.Success, Color.White, Theme.Success, 0F),
            ButtonVariant.Ghost => (Theme.CardBackground, Theme.Accent, Theme.SecondaryButtonBorder, ScaledBorderWidth),
            _ => (Theme.SecondaryButtonFill, Theme.ButtonSecondaryText, Theme.SecondaryButtonBorder, ScaledBorderWidth)
        };
    }
}

/// <summary>
/// Inline feedback strip. It lives in a slot of its own and never resizes itself, so showing
/// or hiding a message cannot push the step cards around.
/// </summary>
internal sealed class NoticeBanner : Panel
{
    private const int DesignCornerRadius = 10;
    private const int DesignIconDiameter = 20;
    private const int DesignIconLeft = 14;
    private const int DesignActionButtonHeight = 30;

    private static int CornerRadius => Theme.S(DesignCornerRadius);
    private static int IconDiameter => Theme.S(DesignIconDiameter);
    private static int IconLeft => Theme.S(DesignIconLeft);
    private static int ActionButtonHeight => Theme.S(DesignActionButtonHeight);

    private readonly Label _textLabel;
    private readonly BufferedFlowLayoutPanel _actions;
    private readonly ToolTip _toolTip = new();
    private StatusKind _kind = StatusKind.Neutral;
    private int _actionsWidth = -1;

    public NoticeBanner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        AutoSize = false;
        BackColor = Color.Transparent;
        Margin = new Padding(0);
        Padding = new Padding(DesignIconLeft + DesignIconDiameter + Theme.Space12, Theme.Space12, Theme.Space16, Theme.Space12);
        MinimumSize = new Size(0, Theme.BannerSlotHeight);
        Visible = false;

        // Fixed-height, fill-docked label: the text wraps inside whatever width is left,
        // which removes the measure-then-resize round trip that used to oscillate.
        _textLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Body,
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            UseMnemonic = false
        };

        _actions = new BufferedFlowLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.Transparent,
            Width = 0
        };

        Controls.Add(_textLabel);
        Controls.Add(_actions);
    }

    public void Show(StatusKind kind, string text)
    {
        _kind = kind;
        var palette = Theme.Palette(kind);
        if (_textLabel.Text != text)
        {
            _textLabel.Text = text;
        }

        _textLabel.ForeColor = kind == StatusKind.Neutral ? Theme.TextPrimary : palette.Text;
        _toolTip.SetToolTip(_textLabel, text);
        Visible = true;
        Invalidate();
    }

    public void ShowActions(params BannerAction[] actions)
    {
        ClearActions();
        foreach (var action in actions)
        {
            // These buttons are created while the user is looking at the window, i.e. after
            // MainForm's density pass has already run over the tree, so their metrics are
            // scaled by hand. The width is measured from the caption rather than left at the
            // 108 px floor: "启动 Antigravity" needs ~140 px, and a fixed floor silently clipped it.
            var textWidth = Theme.Measure(action.Text, Theme.Body).Width + (2 * Theme.S(Theme.Space16));
            var button = new FlatButton
            {
                Text = action.Text,
                Variant = action.Variant,
                Margin = new Padding(0, 0, Theme.S(Theme.Space8), 0),
                Padding = new Padding(Theme.S(Theme.Space16), 0, Theme.S(Theme.Space16), 0),
                MinimumSize = new Size(Math.Max(Theme.S(108), textWidth), ActionButtonHeight),
                MaximumSize = new Size(0, ActionButtonHeight)
            };
            button.Click += (_, _) => action.Handler();
            _actions.Controls.Add(button);
        }

        LayoutActions();
        Invalidate();
    }

    public void ClearActions()
    {
        foreach (Control control in _actions.Controls)
        {
            control.Dispose();
        }

        _actions.Controls.Clear();
        LayoutActions();
    }

    public void Dismiss()
    {
        ClearActions();
        Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = Theme.Palette(_kind);
        var bounds = new Rectangle(0, 0, Width, Height);
        using (var path = Theme.RoundedPath(bounds, CornerRadius))
        using (var background = new SolidBrush(palette.Background))
        {
            graphics.FillPath(background, path);
        }

        Theme.DrawRoundedBorder(graphics, bounds, CornerRadius, palette.Border, Theme.S(1F));

        var symbolBounds = new Rectangle(IconLeft, (Height - IconDiameter) / 2, IconDiameter, IconDiameter);
        using (var dot = new SolidBrush(palette.Dot))
        {
            graphics.FillEllipse(dot, symbolBounds);
        }

        Theme.DrawGlyphCentered(graphics, Theme.Symbol(_kind), Theme.SmallBold, symbolBounds, Color.White);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutActions();
        UpdateRegion();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutActions();
    }

    /// <summary>
    /// Sizes the right-hand action dock from the buttons' preferred widths and centres them
    /// vertically. Height is never touched, which is what keeps the surrounding layout still.
    /// </summary>
    private void LayoutActions()
    {
        if (_actions is null)
        {
            return;
        }

        var needed = 0;
        foreach (Control control in _actions.Controls)
        {
            needed += control.PreferredSize.Width + control.Margin.Horizontal;
        }

        var top = Math.Max(0, (ClientSize.Height - ActionButtonHeight) / 2);
        var padding = new Padding(0, top, 0, 0);
        if (_actions.Padding != padding)
        {
            _actions.Padding = padding;
        }

        if (needed != _actionsWidth)
        {
            _actionsWidth = needed;
            _actions.Width = needed;
        }
    }

    private void UpdateRegion()
    {
        Theme.ApplyRoundedRegion(this, CornerRadius);
    }
}

internal readonly record struct BannerAction(string Text, ButtonVariant Variant, Action Handler);
