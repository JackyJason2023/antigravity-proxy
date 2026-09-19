using System.Drawing;

namespace AntigravityProxyInstaller;

/// <summary>
/// Three-step deployment wizard: confirm the Antigravity installation, obtain the
/// proxy payload, then copy the verified files. Each step owns its own actions and
/// the bottom bar always offers the single most useful next action.
/// </summary>
internal sealed class MainForm : Form
{
    private const int PrimaryButtonWidth = 244;
    private const int FieldLabelWidth = 92;
    private const int CardButtonMinWidth = 104;

    /// <summary>Vertical gap inserted above every card row except the first one.</summary>
    private const int FieldRowGap = Theme.RowGap;
    private const int CheckRowGap = Theme.Space8;

    /// <summary>Character width of each side of the "1.2 MB / 3.4 MB" readout.</summary>
    private const int ReadoutFieldWidth = 9;

    /// <summary>
    /// Height of one status row in card ③: the button-sized cell plus the gap above and below it,
    /// which is the same rhythm as a field row in cards ① and ②.
    /// </summary>
    private const int CheckRowHeight = Theme.ButtonHeight + (2 * CheckRowGap);

    // ── 状态 ────────────────────────────────────────────────────────
    private PayloadInfo? _payload;
    private TargetInfo? _target;
    private DeploymentAssessment? _assessment;
    private CancellationTokenSource? _fetchCancellation;
    private bool _targetRunning;
    private bool _bannerPinned;
    private string _bannerSignature = string.Empty;
    private string _refreshSignature = string.Empty;

    // ── ① 目标程序 ──────────────────────────────────────────────────
    private readonly CardPanel _targetCard = new() { Badge = "1", Title = "目标程序" };
    private readonly TextBox _executableText = CreateField();
    private readonly TextBox _directoryText = CreateField();
    private readonly Label _dragHintLabel = CreateHintLabel();
    private readonly FlatButton _chooseTargetButton = new();
    private readonly FlatButton _openTargetFolderButton = new();

    // ── ② 部署源 ────────────────────────────────────────────────────
    private readonly CardPanel _payloadCard = new() { Badge = "2", Title = "部署源" };
    private readonly TextBox _payloadText = CreateField();
    private readonly FlatButton _fetchButton = new();
    private readonly FlatButton _choosePayloadButton = new();
    private readonly FlatButton _cancelFetchButton = new();
    private readonly Label _payloadHintLabel = CreateHintLabel();
    private readonly ProgressBar _fetchProgress = new();
    private readonly Label _fetchProgressLabel = CreateHintLabel();
    private readonly Label _fetchProgressDetail = CreateReadoutLabel();
    private TableLayoutPanel _fetchProgressRow = new();

    // ── ③ 检查与部署 ────────────────────────────────────────────────
    private readonly CardPanel _checkCard = new() { Badge = "3", Title = "检查与部署" };
    private readonly Label _architectureValue = CreateRowValue();
    private readonly Label _architectureAction = CreateRowValue();
    private readonly Label _versionValue = CreateRowValue();
    private readonly Label _versionAction = CreateRowValue();
    private readonly Label _configValue = CreateRowValue();
    private readonly Label _configAction = CreateRowValue();
    private readonly CheckBox _overwriteCheckBox = new();

    // ── 反馈区与主操作 ──────────────────────────────────────────────
    private readonly NoticeBanner _banner = new();
    private readonly Label _statusLabel = CreateRowValue();
    private readonly FlatButton _primaryButton = new();
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _dragResetTimer = new();
    private readonly System.Windows.Forms.Timer _stateRefreshTimer = new();

    public MainForm()
    {
        // Every geometry below is a design pixel at 96 DPI. Point-sized fonts already grow with
        // the monitor DPI, so the shapes have to follow them; ApplyDensityScale() does that in
        // one explicit pass. WinForms' own autoscale is deliberately switched off — measured on a
        // 144 DPI monitor, a form set to AutoScaleMode.Dpi with AutoScaleDimensions 96 kept its
        // 96 DPI bounds, so the framework path cannot be relied on here.
        Theme.InitializeScale(Theme.DetectScale());

        Text = "Antigravity Proxy 部署助手";
        // CenterScreen is deliberately not used: it is applied once, before the density pass and
        // FitToWorkArea() have settled the final size, so the window would grow down and to the
        // right of the position WinForms picked. CenterOnWorkArea() places it explicitly instead.
        StartPosition = FormStartPosition.Manual;
        // Both boxes off: Win32 draws no caption buttons at all when neither WS_MINIMIZEBOX nor
        // WS_MAXIMIZEBOX is set, which is the only way to drop the maximize button while keeping a
        // resizable border — a sizing frame otherwise renders the missing button greyed out.
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Body;
        BackColor = Theme.WindowBackground;
        MinimumSize = new Size(900, 600);

        // Measured at 150 %: the step area is 1165 device px tall in every state (the download
        // progress group swaps into the hint's permanent row instead of adding one), and the banner
        // slot, the action bar and the form padding take another 247 px. 954 design px = 1431
        // device px covers that with room to spare, so the last status row and the 允许覆盖 option
        // stay visible without scrolling. FitToWorkArea() still caps it on small screens.
        ClientSize = new Size(952, 954);
        Padding = new Padding(20, 16, 20, 12);
        AllowDrop = true;

        _toolTip.AutoPopDelay = 12000;
        _toolTip.InitialDelay = 350;
        _toolTip.ReshowDelay = 100;
        _toolTip.ShowAlways = true;

        BuildUi();
        ApplyDensityScale();
        WireDragAndDrop();

        _dragResetTimer.Interval = 110;
        _dragResetTimer.Tick += (_, _) =>
        {
            _dragResetTimer.Stop();
            SetDropHighlight(false);
        };

        _stateRefreshTimer.Interval = 1500;
        _stateRefreshTimer.Tick += (_, _) => RefreshRunningState();

        _payload = PayloadLocator.TryFindDefault(out var defaultPayload) ? defaultPayload : null;
        if (InstalledApplicationLocator.TryFindDefault(out var defaultTarget))
        {
            _target = defaultTarget;
        }

        UpdateView();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The window now knows the DPI of the monitor it actually landed on.
        RescaleForCurrentDpi();
    }

    /// <summary>
    /// Fires when the window moves to a monitor with a different scale factor. Rescaling is done
    /// on the next message pump so the new <see cref="Control.DeviceDpi"/> is already visible.
    /// </summary>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        BeginInvoke(new Action(RescaleForCurrentDpi));
    }

    private void RescaleForCurrentDpi()
    {
        var previous = Theme.Scale;
        Theme.InitializeScale(DeviceDpi > 0 ? DeviceDpi / 96F : previous);
        if (Math.Abs(previous - Theme.Scale) < 0.01F)
        {
            return;
        }

        ApplyDensityScale();
        FitToWorkArea();
        CenterOnWorkArea();
        Invalidate(true);
    }

    // ══════════════════════════════════════════════════════════════════
    //  密度缩放（设计像素 → 设备像素）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>The device scale the tree has already been multiplied by.</summary>
    private float _appliedScale = 1F;

    /// <summary>
    /// Multiplies every geometry in the freshly built tree by the ratio between the device scale
    /// and what has already been applied, so the pass is safe to repeat when the window moves to
    /// a monitor with different scaling. The literals in the source stay design pixels.
    /// Fonts are deliberately untouched: point sizes already grow with the DPI, which is exactly
    /// the mismatch this pass removes.
    /// </summary>
    private void ApplyDensityScale()
    {
        var factor = Theme.Scale / _appliedScale;
        _appliedScale = Theme.Scale;
        if (Math.Abs(factor - 1F) < 0.01F)
        {
            return;
        }

        ClientSize = Scale(ClientSize, factor);
        if (!MinimumSize.IsEmpty)
        {
            MinimumSize = Scale(MinimumSize, factor);
        }

        Padding = Scale(Padding, factor);
        foreach (Control child in Controls)
        {
            ScaleDescendants(child, factor);
        }
    }

    private static void ScaleDescendants(Control control, float factor)
    {
        control.Padding = Scale(control.Padding, factor);
        control.Margin = Scale(control.Margin, factor);

        if (!control.MinimumSize.IsEmpty)
        {
            control.MinimumSize = Scale(control.MinimumSize, factor);
        }

        if (!control.MaximumSize.IsEmpty)
        {
            control.MaximumSize = Scale(control.MaximumSize, factor);
        }

        if (!control.AutoSize)
        {
            control.Size = Scale(control.Size, factor);
        }

        if (control is TableLayoutPanel layout)
        {
            foreach (ColumnStyle column in layout.ColumnStyles)
            {
                if (column.SizeType == SizeType.Absolute)
                {
                    column.Width = (float)Math.Round(column.Width * factor, MidpointRounding.AwayFromZero);
                }
            }

            foreach (RowStyle row in layout.RowStyles)
            {
                if (row.SizeType == SizeType.Absolute)
                {
                    row.Height = (float)Math.Round(row.Height * factor, MidpointRounding.AwayFromZero);
                }
            }
        }

        foreach (Control child in control.Controls)
        {
            ScaleDescendants(child, factor);
        }
    }

    private static Size Scale(Size value, float factor) => new(Round(value.Width, factor), Round(value.Height, factor));

    private static Padding Scale(Padding value, float factor) => new(
        Round(value.Left, factor),
        Round(value.Top, factor),
        Round(value.Right, factor),
        Round(value.Bottom, factor));

    private static int Round(int value, float factor) => (int)Math.Round(value * factor, MidpointRounding.AwayFromZero);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToWorkArea();
        CenterOnWorkArea();
    }

    /// <summary>
    /// Places the window in the middle of the work area of the screen that hosts it. Runs after
    /// every program-driven resize — the density pass and the work-area cap both change the size
    /// once the framework would have decided the position — and never from a resize the user makes,
    /// so dragging or sizing the window is left alone.
    /// </summary>
    private void CenterOnWorkArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    /// <summary>
    /// A window scaled for a 150 % monitor no longer fits a laptop work area, so the scaled
    /// size is capped against the screen that hosts it.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var maximumWidth = Math.Max(Theme.S(520), area.Width - Theme.S(24));
        var maximumHeight = Math.Max(Theme.S(420), area.Height - Theme.S(24));

        if (MinimumSize.Width > maximumWidth || MinimumSize.Height > maximumHeight)
        {
            MinimumSize = new Size(Math.Min(MinimumSize.Width, maximumWidth),
                Math.Min(MinimumSize.Height, maximumHeight));
        }

        if (Width > maximumWidth || Height > maximumHeight)
        {
            Size = new Size(Math.Min(Width, maximumWidth), Math.Min(Height, maximumHeight));
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _stateRefreshTimer.Start();
        // 让键盘焦点落在主操作上，而不是只读的路径显示框
        BeginInvoke(new Action(() =>
        {
            if (_primaryButton.Enabled)
            {
                ActiveControl = _primaryButton;
                _primaryButton.Select();
            }
        }));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _stateRefreshTimer.Stop();
        _fetchCancellation?.Cancel();
        _fetchCancellation?.Dispose();
        _fetchCancellation = null;
        base.OnFormClosing(e);
    }

    // ══════════════════════════════════════════════════════════════════
    //  UI 构建
    // ══════════════════════════════════════════════════════════════════

    private void BuildUi()
    {
        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = false,
            Padding = new Padding(0),
            BackColor = Color.Transparent
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));                  // 0 可滚动的步骤区
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.BannerSlotHeight)); // 1 通知横幅（永久占位，避免抖动）
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));                   // 2 主操作栏
        Controls.Add(root);

        // The step cards keep their natural height, so a short window scrolls
        // instead of letting a card overlap the banner.
        var body = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0)
        };

        var content = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var row = 0; row < 4; row++)
        {
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        content.Controls.Add(BuildHeader(), 0, 0);
        content.Controls.Add(BuildTargetCard(), 0, 1);
        content.Controls.Add(BuildPayloadCard(), 0, 2);
        content.Controls.Add(BuildCheckCard(), 0, 3);
        body.Controls.Add(content);

        root.Controls.Add(body, 0, 0);
        root.Controls.Add(_banner, 0, 1);
        root.Controls.Add(BuildActionBar(), 0, 2);

        foreach (var card in new Control[] { _targetCard, _payloadCard, _checkCard })
        {
            card.Dock = DockStyle.Fill;
        }

        // The banner owns a fixed-height slot: it fills it and never resizes itself, so a
        // message appearing or disappearing cannot move a single card.
        _banner.Dock = DockStyle.Fill;
        AcceptButton = _primaryButton;
    }

    private Control BuildHeader()
    {
        var panel = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, Theme.Space12),
            Padding = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label
        {
            Text = "Antigravity Proxy 部署助手",
            AutoSize = true,
            Font = Theme.WindowTitle,
            ForeColor = Theme.TextPrimary,
            Margin = new Padding(0, 0, 0, Theme.Space8)
        }, 0, 0);

        panel.Controls.Add(new Label
        {
            Text = "三步完成：确认目标程序 → 获取部署文件 → 复制到安装目录，架构与内容全程自动校验",
            AutoSize = true,
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary
        }, 0, 1);

        return panel;
    }

    private Control BuildTargetCard()
    {
        var table = CreateCardBody(3);

        AddFieldRow(table, 0, "程序", _executableText, _chooseTargetButton);
        AddFieldRow(table, 1, "安装目录", _directoryText, _openTargetFolderButton);

        _chooseTargetButton.Text = "更换…";
        _chooseTargetButton.Variant = ButtonVariant.Secondary;
        _chooseTargetButton.Click += OnChooseTargetClick;

        _openTargetFolderButton.Text = "打开目录";
        _openTargetFolderButton.Variant = ButtonVariant.Secondary;
        _openTargetFolderButton.Click += OnOpenTargetFolderClick;

        _dragHintLabel.Text = "提示：也可以把桌面上的 Antigravity 快捷方式拖到本窗口任意位置来更换目标。";
        _dragHintLabel.Margin = new Padding(0, Theme.RowGap, 0, 0);
        table.Controls.Add(_dragHintLabel, 0, 2);
        table.SetColumnSpan(_dragHintLabel, 3);

        _targetCard.Controls.Add(table);
        return _targetCard;
    }

    private Control BuildPayloadCard()
    {
        var table = CreateCardBody(3);

        AddFieldRow(table, 0, "来源", _payloadText, _choosePayloadButton);
        _choosePayloadButton.Text = "选择文件夹…";
        _choosePayloadButton.Variant = ButtonVariant.Secondary;
        _choosePayloadButton.Click += OnChoosePayloadClick;

        // 操作行：主按钮靠左，形成“这一步做什么”的视觉顺序。
        var actionRow = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = Theme.ButtonHeight,
            Margin = new Padding(0, Theme.RowGap, 0, 0),
            Padding = new Padding(0),
            WrapContents = false
        };

        _fetchButton.Text = "从 GitHub 获取最新构建";
        _fetchButton.Variant = ButtonVariant.Primary;
        _fetchButton.MinimumSize = new Size(176, Theme.ButtonHeight);
        _fetchButton.Margin = new Padding(0);
        _fetchButton.Click += OnFetchLatestClick;
        actionRow.Controls.Add(_fetchButton);

        table.Controls.Add(actionRow, 0, 1);
        table.SetColumnSpan(actionRow, 3);

        // 状态槽：常驻的一行，平时放提示文字，下载时整行换成
        // 进度条 + 阶段文字 + 等宽数字读数 + 取消。行高固定，所以卡片 ② 的高度全程不变，
        // 下面的卡片 ③ 不会被顶下去再弹回来。
        var statusSlot = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = Theme.ProgressRowHeight,
            Margin = new Padding(0, Theme.RowGap, 0, 0),
            Padding = new Padding(0),
            BackColor = Color.Transparent
        };

        _payloadHintLabel.Dock = DockStyle.Fill;
        _payloadHintLabel.Margin = new Padding(0);
        _payloadHintLabel.Text = "已缓存最新构建时无需重复获取";

        _fetchProgressRow = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.Transparent,
            Visible = false
        };
        _fetchProgressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240F));
        _fetchProgressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _fetchProgressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.ProgressReadoutWidth));
        _fetchProgressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
        _fetchProgressRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _fetchProgress.Dock = DockStyle.Fill;
        _fetchProgress.MinimumSize = new Size(0, 18);
        _fetchProgress.Margin = new Padding(0, 11, 0, 11);
        _fetchProgress.Minimum = 0;
        _fetchProgress.Maximum = 100;
        _fetchProgress.Value = 0;
        _fetchProgress.Style = ProgressBarStyle.Continuous;

        _fetchProgressLabel.Dock = DockStyle.Fill;
        _fetchProgressLabel.AutoSize = false;
        _fetchProgressLabel.Margin = new Padding(Theme.Space12, 0, Theme.Space8, 0);
        _fetchProgressLabel.TextAlign = ContentAlignment.MiddleLeft;

        // Tabular figures plus a fixed column: the numbers can change every frame without the
        // text block ever changing width, which is what used to read as flicker.
        _fetchProgressDetail.Dock = DockStyle.Fill;
        _fetchProgressDetail.AutoSize = false;
        _fetchProgressDetail.Margin = new Padding(0, 0, Theme.Space12, 0);
        _fetchProgressDetail.TextAlign = ContentAlignment.MiddleRight;
        _fetchProgressDetail.MinimumSize = new Size(Theme.ProgressReadoutWidth, 0);

        _cancelFetchButton.Text = "取消";
        _cancelFetchButton.Variant = ButtonVariant.Ghost;
        _cancelFetchButton.Margin = new Padding(0, 6, 0, 6);
        _cancelFetchButton.MinimumSize = new Size(64, 28);
        _cancelFetchButton.MaximumSize = new Size(0, 28);
        _cancelFetchButton.Click += OnCancelFetchClick;

        _fetchProgressRow.Controls.Add(_fetchProgress, 0, 0);
        _fetchProgressRow.Controls.Add(_fetchProgressLabel, 1, 0);
        _fetchProgressRow.Controls.Add(_fetchProgressDetail, 2, 0);
        _fetchProgressRow.Controls.Add(_cancelFetchButton, 3, 0);

        statusSlot.Controls.Add(_payloadHintLabel);
        statusSlot.Controls.Add(_fetchProgressRow);

        table.Controls.Add(statusSlot, 0, 2);
        table.SetColumnSpan(statusSlot, 3);

        _payloadCard.Controls.Add(table);
        return _payloadCard;
    }

    private Control BuildCheckCard()
    {
        var table = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 5,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 208F));

        // The three status rows own a fixed height. Auto-size rows plus fill-docked cells are a
        // feedback loop: the card measures the table, the table hands any spare height back to
        // the rows, and the next pass measures a taller card — measured at 150 % scaling that
        // inflated a 72 px row to 186 px. Fixed rows break the loop and give the table the same
        // rhythm as the field rows in cards ① and ②.
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));                     // 0 表头
        for (var row = 1; row <= 3; row++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, CheckRowHeight)); // 1..3 状态行
        }

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));                     // 4 覆盖选项

        AddHeaderCell(table, 0, 0, "项目");
        AddHeaderCell(table, 1, 0, "目标目录中的状态");
        AddHeaderCell(table, 2, 0, "将执行的操作");

        AddCheckRow(table, 1, "程序架构", _architectureValue, _architectureAction);
        AddCheckRow(table, 2, "version.dll", _versionValue, _versionAction);
        AddCheckRow(table, 3, "config.json", _configValue, _configAction);

        _overwriteCheckBox.Text = "允许覆盖内容不同的文件（覆盖前先备份到 .antigravity-proxy-backup）";
        _overwriteCheckBox.AutoSize = true;
        _overwriteCheckBox.Font = Theme.Small;
        _overwriteCheckBox.ForeColor = Theme.TextSecondary;
        _overwriteCheckBox.Margin = new Padding(0, Theme.Space16, 0, 0);
        _overwriteCheckBox.CheckedChanged += (_, _) =>
        {
            _bannerPinned = false;
            UpdateView();
        };
        table.Controls.Add(_overwriteCheckBox, 0, 4);
        table.SetColumnSpan(_overwriteCheckBox, 3);

        _checkCard.Controls.Add(table);
        return _checkCard;
    }

    private Control BuildActionBar()
    {
        var panel = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };

        var separator = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Theme.CardBorder
        };

        var content = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, Theme.Space12, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, PrimaryButtonWidth));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Font = Theme.Body;
        _statusLabel.Margin = new Padding(0, 0, Theme.Space16, 0);

        _primaryButton.Text = "开始";
        _primaryButton.Variant = ButtonVariant.Primary;
        _primaryButton.AutoSize = false;
        _primaryButton.MaximumSize = Size.Empty;
        _primaryButton.Dock = DockStyle.Fill;
        _primaryButton.Enabled = false;
        _primaryButton.Click += OnPrimaryClick;

        content.Controls.Add(_statusLabel, 0, 0);
        content.Controls.Add(_primaryButton, 1, 0);

        panel.Controls.Add(content);
        panel.Controls.Add(separator);
        return panel;
    }

    // ══════════════════════════════════════════════════════════════════
    //  控件工厂
    // ══════════════════════════════════════════════════════════════════

    private static TableLayoutPanel CreateCardBody(int contentRows)
    {
        var table = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = contentRows,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, FieldLabelWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var row = 0; row < contentRows; row++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return table;
    }

    private static void AddFieldRow(TableLayoutPanel table, int row, string name, Control field, Control action)
    {
        var top = row == 0 ? 0 : FieldRowGap;
        var label = new Label
        {
            Text = name,
            Dock = DockStyle.Fill,
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, top, Theme.Space8, 0),
            AutoSize = false,
            MinimumSize = new Size(0, Theme.ButtonHeight)
        };

        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, top, Theme.Space8, 0);
        action.Margin = new Padding(0, top, 0, 0);
        action.MinimumSize = new Size(CardButtonMinWidth, Theme.ButtonHeight);

        table.Controls.Add(label, 0, row);
        table.Controls.Add(field, 1, row);
        table.Controls.Add(action, 2, row);
    }

    private static void AddCheckRow(TableLayoutPanel table, int row, string name, Label status, Label action)
    {
        // Every cell of a check row is a fill-docked, vertically centred label of the same
        // height. Mixing an auto-sized (therefore top-aligned) name with centred value cells is
        // what made the three columns sit on three different lines.
        table.Controls.Add(new Label
        {
            Text = name,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Body,
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            MinimumSize = new Size(0, Theme.ButtonHeight),
            Margin = new Padding(0, CheckRowGap, Theme.Space8, CheckRowGap)
        }, 0, row);

        status.Margin = new Padding(0, CheckRowGap, Theme.Space8, CheckRowGap);
        action.Margin = new Padding(0, CheckRowGap, 0, CheckRowGap);
        action.TextAlign = ContentAlignment.MiddleRight;
        table.Controls.Add(status, 1, row);
        table.Controls.Add(action, 2, row);
    }

    private static void AddHeaderCell(TableLayoutPanel table, int col, int row, string text)
    {
        table.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = Theme.Small,
            ForeColor = Theme.TextTertiary,
            TextAlign = col == 2 ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, Theme.Space8, Theme.Space8)
        }, col, row);
    }

    private static TextBox CreateField()
    {
        return new TextBox
        {
            ReadOnly = true,
            TabStop = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.FieldBackground,
            ForeColor = Theme.FieldText,
            Font = Theme.Body,
            MinimumSize = new Size(0, Theme.ButtonHeight),
            Margin = new Padding(0)
        };
    }

    private static Label CreateRowValue()
    {
        return new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Body,
            ForeColor = Theme.TextTertiary,
            TextAlign = ContentAlignment.MiddleLeft,
            MinimumSize = new Size(0, Theme.ButtonHeight)
        };
    }

    private static Label CreateHintLabel()
    {
        return new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Small,
            ForeColor = Theme.TextTertiary,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            MinimumSize = new Size(0, 20),
            Margin = new Padding(0)
        };
    }

    /// <summary>Monospaced, right-aligned readout so changing digits never move neighbouring text.</summary>
    private static Label CreateReadoutLabel()
    {
        return new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = Theme.Mono,
            ForeColor = Theme.TextSecondary,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = false,
            MinimumSize = new Size(0, 20),
            Margin = new Padding(0)
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  拖放：全窗口接收，目标卡片高亮
    // ══════════════════════════════════════════════════════════════════

    private void WireDragAndDrop()
    {
        foreach (var control in EnumerateControls())
        {
            control.AllowDrop = true;
            control.DragEnter += OnAnyDragEnter;
            control.DragLeave += OnAnyDragLeave;
            control.DragDrop += OnAnyDragDrop;
        }
    }

    private IEnumerable<Control> EnumerateControls()
    {
        foreach (Control control in Controls)
        {
            yield return control;
            foreach (var child in EnumerateChildControls(control))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<Control> EnumerateChildControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateChildControls(child))
            {
                yield return descendant;
            }
        }
    }

    private void OnAnyDragEnter(object? sender, DragEventArgs e)
    {
        var accepted = HasCandidateFile(e);
        e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        if (!accepted)
        {
            return;
        }

        _dragResetTimer.Stop();
        SetDropHighlight(true);
    }

    private void OnAnyDragLeave(object? sender, EventArgs e)
    {
        _dragResetTimer.Start();
    }

    private void OnAnyDragDrop(object? sender, DragEventArgs e)
    {
        _dragResetTimer.Stop();
        SetDropHighlight(false);
        HandleDrop(e);
    }

    private static bool HasCandidateFile(DragEventArgs e)
    {
        return e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
    }

    private void SetDropHighlight(bool active)
    {
        _targetCard.Highlight = active;
        _dragHintLabel.ForeColor = active ? Theme.Accent : Theme.TextTertiary;
        _dragHintLabel.Text = active
            ? "松开：把该文件作为目标程序或部署源。"
            : "提示：也可以把桌面上的 Antigravity 快捷方式拖到本窗口任意位置来更换目标。";
    }

    private void HandleDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        _bannerPinned = false;

        var directory = paths.FirstOrDefault(IsExistingDirectory);
        var executable = paths.FirstOrDefault(path => !IsExistingDirectory(path) &&
            (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)));

        if (executable is not null)
        {
            if (!DeploymentInspector.TryCreateTarget(executable, out var target, out var error))
            {
                ShowError(error);
                return;
            }

            _target = target;
            _assessment = null;
            UpdateView();
            return;
        }

        if (directory is not null)
        {
            if (!PayloadLocator.TryLoad(directory, out var payload, out var payloadError) || payload is null)
            {
                ShowError(payloadError);
                return;
            }

            _payload = payload;
            UpdateView();
            return;
        }

        ShowError("无法识别拖入的内容。请拖入 Antigravity 的 .lnk 快捷方式（或 Antigravity.exe），" +
                  "也可以拖入同时包含 version.dll 与 config.json 的文件夹作为部署源。");
    }

    private static bool IsExistingDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  交互
    // ══════════════════════════════════════════════════════════════════

    private void OnPrimaryClick(object? sender, EventArgs e)
    {
        (_primaryButton.Tag as Action)?.Invoke();
    }

    private void OnChooseTargetClick(object? sender, EventArgs e)
    {
        ChooseTargetFromDialog();
    }

    private void ChooseTargetFromDialog()
    {
        _bannerPinned = false;

        using var dialog = new OpenFileDialog
        {
            Title = "选择 Antigravity 快捷方式或程序",
            Filter = "Antigravity 快捷方式或程序 (*.lnk;*.exe)|*.lnk;*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var startingDirectory = Path.GetDirectoryName(_target?.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(startingDirectory) && Directory.Exists(startingDirectory))
        {
            dialog.InitialDirectory = startingDirectory;
        }
        else
        {
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!DeploymentInspector.TryCreateTarget(dialog.FileName, out var target, out var error))
        {
            ShowError(error);
            return;
        }

        _target = target;
        _assessment = null;
        UpdateView();
    }

    private void OnOpenTargetFolderClick(object? sender, EventArgs e)
    {
        RevealTargetDirectory();
    }

    private void RevealTargetDirectory()
    {
        if (_target is null)
        {
            return;
        }

        try
        {
            AntigravityLauncher.RevealInstallDirectory(_target);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnChoosePayloadClick(object? sender, EventArgs e)
    {
        _bannerPinned = false;

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择同时包含 version.dll 和 config.json 的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!PayloadLocator.TryLoad(dialog.SelectedPath, out var payload, out var error) || payload is null)
        {
            ShowError(error);
            return;
        }

        _payload = payload;
        UpdateView();
    }

    private void OnFetchLatestClick(object? sender, EventArgs e)
    {
        _ = FetchLatestAsync();
    }

    private void OnCancelFetchClick(object? sender, EventArgs e)
    {
        _fetchCancellation?.Cancel();
    }

    private async Task FetchLatestAsync()
    {
        if (_fetchCancellation is not null)
        {
            return;
        }

        _bannerPinned = false;

        var architecture = _target?.Architecture ?? PeArchitecture.X64;
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
        {
            ShowError("目前 GitHub 最新构建仅提供 x86 和 x64 版本，请手动选择架构匹配的部署源。");
            return;
        }

        var architectureName = architecture == PeArchitecture.X86 ? "x86" : "x64";
        _fetchCancellation = new CancellationTokenSource();
        SetFetching(true, architectureName);

        var progress = new Progress<DownloadProgress>(ApplyDownloadProgress);

        try
        {
            _payload = await RemotePayloadService.DownloadLatestAsync(
                architecture,
                _fetchCancellation.Token,
                progress);
            UpdateView();
            ShowBanner(
                StatusKind.Success,
                $"已获取 GitHub 最新构建（{architectureName}）。确认已退出 Antigravity 后点击右下角按钮完成部署。",
                pinned: true);
        }
        catch (OperationCanceledException) when (!_fetchCancellation.IsCancellationRequested)
        {
            ShowError("访问 GitHub 超时。请检查系统代理或网络连接，也可以手动选择本地部署源。");
        }
        catch (OperationCanceledException)
        {
            _bannerPinned = false;
            UpdateView();
        }
        catch (HttpRequestException ex)
        {
            ShowError($"无法连接 GitHub：{ex.Message}\n请检查系统代理或网络连接，也可以手动选择本地部署源。",
                openReleases: true);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message, openReleases: true);
        }
        catch (Exception ex)
        {
            ShowError($"获取最新编译失败：{ex.Message}");
        }
        finally
        {
            _fetchCancellation?.Dispose();
            _fetchCancellation = null;
            SetFetching(false, architectureName);
            UpdateView();
        }
    }

    private void SetFetching(bool fetching, string architectureName)
    {
        BeginLayoutBatch();
        try
        {
            _fetchProgressRow.Visible = fetching;

            // The progress group takes the hint's place in the same row, which keeps card ② at a
            // constant height: nothing below it moves when a download starts or ends.
            _payloadHintLabel.Visible = !fetching;
            _cancelFetchButton.Enabled = true;
            _fetchButton.Enabled = !fetching;
            _choosePayloadButton.Enabled = !fetching;
            SetText(_fetchButton, fetching
                ? $"正在获取（{architectureName}）…"
                : $"从 GitHub 获取最新构建（{architectureName}）");

            if (fetching)
            {
                SetProgress(0, "正在连接 GitHub…", null);
            }
            else
            {
                SetProgress(0, string.Empty, null);
                SetText(_fetchProgressLabel, string.Empty);
            }
        }
        finally
        {
            EndLayoutBatch();
        }
    }

    /// <summary>
    /// Progress reporting for the download row.
    /// The bar deliberately keeps a single native style for the whole operation: toggling
    /// between Marquee and Continuous recreates the underlying Win32 control, which is what
    /// made the row flash several times per second.
    /// </summary>
    private void SetProgress(int percent, string phase, string? readout)
    {
        var clamped = Math.Clamp(percent, _fetchProgress.Minimum, _fetchProgress.Maximum);
        if (_fetchProgress.Value != clamped)
        {
            _fetchProgress.Value = clamped;
        }

        SetText(_fetchProgressLabel, phase);
        if (readout is not null)
        {
            SetText(_fetchProgressDetail, readout);
        }
    }

    private void ApplyDownloadProgress(DownloadProgress progress)
    {
        switch (progress.Phase)
        {
            case DownloadPhase.Receiving when progress.TotalBytes is > 0:
            {
                var total = progress.TotalBytes.Value;
                var percent = (int)Math.Clamp(progress.BytesReceived * 100L / total, 0, 100);
                SetProgress(percent, "正在下载部署文件…", FormatReadout(progress.BytesReceived, total, percent));
                break;
            }

            case DownloadPhase.Receiving:
            {
                SetProgress(_fetchProgress.Value, "正在下载部署文件…", FormatReadout(progress.BytesReceived, null, null));
                break;
            }

            case DownloadPhase.Extracting:
            {
                SetProgress(100, "正在解压并校验 version.dll / config.json…", null);
                break;
            }

            case DownloadPhase.Caching:
            {
                SetProgress(100, "正在写入本地缓存…", null);
                break;
            }

            default:
            {
                SetProgress(0, "正在连接 GitHub…", null);
                break;
            }
        }
    }

    /// <summary>
    /// Monospaced, character-padded readout: every update keeps exactly the same pixel width,
    /// so the text never twitches as the numbers grow.
    /// </summary>
    private static string FormatReadout(long received, long? total, int? percent)
    {
        var receivedText = RemotePayloadService.FormatFileSize(received).PadLeft(ReadoutFieldWidth);
        if (total is null || percent is null)
        {
            return receivedText;
        }

        var totalText = RemotePayloadService.FormatFileSize(total.Value).PadLeft(ReadoutFieldWidth);
        return $"{receivedText} / {totalText} {percent.Value,3}%";
    }

    private void LaunchTarget()
    {
        if (_target is null)
        {
            return;
        }

        try
        {
            AntigravityLauncher.Launch(_target);
            ShowBanner(StatusKind.Success, "已启动 Antigravity。若代理未生效，请确认已完全退出旧进程后重新打开。", pinned: true);
            RefreshRunningState();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RunDeployment()
    {
        if (_assessment is null)
        {
            return;
        }

        _bannerPinned = false;

        try
        {
            var result = DeploymentService.Deploy(_assessment, _overwriteCheckBox.Checked);
            UpdateView();

            if (result.CopiedFiles.Count == 0)
            {
                ShowBanner(StatusKind.Neutral, "没有需要复制的文件，目标目录已经与部署源一致。", pinned: true);
                return;
            }

            var text = $"部署完成：已复制 {string.Join("、", result.CopiedFiles)}。";
            if (result.BackupFiles.Count > 0)
            {
                text += $" 原有文件已备份 {result.BackupFiles.Count} 份到 .antigravity-proxy-backup。";
            }

            text += " 现在可以直接启动 Antigravity。";
            ShowBanner(StatusKind.Success, text, pinned: true, launchActions: true);
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("没有权限写入目标目录。请关闭 Antigravity，并以管理员身份重新运行此工具。");
        }
        catch (IOException ex)
        {
            ShowError($"文件复制失败：{ex.Message}\n请确认 Antigravity 已完全退出（系统托盘图标也要退出）。");
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  视图更新
    // ══════════════════════════════════════════════════════════════════

    private void UpdateView()
    {
        InvalidateRefreshSignature();
        BeginLayoutBatch();
        try
        {
            UpdatePayloadView();
            UpdateTargetView();
            Inspect();
            UpdateCheckRows();
            RefreshRunningState();
        }
        finally
        {
            EndLayoutBatch();
        }
    }

    /// <summary>
    /// Batches a burst of property changes into a single layout pass. Without it, updating a
    /// dozen labels and a card status repaints the window several times and reads as flicker.
    /// </summary>
    private void BeginLayoutBatch()
    {
        SuspendLayout();
    }

    private void EndLayoutBatch()
    {
        ResumeLayout(false);
        PerformLayout();
    }

    private static void SetText(Control control, string text)
    {
        if (control.Text != text)
        {
            control.Text = text;
        }
    }

    private void UpdatePayloadView()
    {
        SetText(_payloadText, _payload?.DirectoryPath ?? "尚未选择部署源");
        _toolTip.SetToolTip(_payloadText, _payload?.DirectoryPath ?? string.Empty);
        _payloadCard.StatusText = _payload is null
            ? "未找到部署源"
            : $"{_payload.OriginDisplay} · {DeploymentInspector.FormatArchitecture(_payload.Architecture)}";
        _payloadCard.StatusKind = _payload is null ? StatusKind.Failure : StatusKind.Success;

        var architecture = _target?.Architecture ?? PeArchitecture.X64;
        var architectureName = architecture == PeArchitecture.X86 ? "x86" : "x64";
        if (_fetchCancellation is null)
        {
            SetText(_fetchButton, $"从 GitHub 获取最新构建（{architectureName}）");
        }

        var cacheHint = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AntigravityProxyInstaller",
            "payload-cache");
        _toolTip.SetToolTip(_fetchButton,
            $"按目标程序架构下载 version.dll 与 config.json，缓存在 {cacheHint}\\{architectureName}。");
        _toolTip.SetToolTip(_choosePayloadButton, "选择本地已经编译好的目录，需同时包含 version.dll 与 config.json。");
    }

    private void UpdateTargetView()
    {
        SetText(_executableText, _target is null
            ? "未检测到 Antigravity"
            : $"{Path.GetFileName(_target.ExecutablePath)} · {DeploymentInspector.FormatArchitecture(_target.Architecture)}");
        SetText(_directoryText, _target?.DirectoryPath ?? string.Empty);
        _toolTip.SetToolTip(_executableText, _target?.ExecutablePath ?? string.Empty);
        _toolTip.SetToolTip(_directoryText, _target?.DirectoryPath ?? string.Empty);

        _targetCard.StatusText = _target is null ? "未检测到，请手动选择" : "已检测到安装";
        _targetCard.StatusKind = _target is null ? StatusKind.Failure : StatusKind.Success;
        _chooseTargetButton.Enabled = true;
        _openTargetFolderButton.Enabled = _target is not null;
    }

    private void Inspect()
    {
        _assessment = null;
        if (_payload is null || _target is null)
        {
            return;
        }

        try
        {
            _assessment = DeploymentInspector.Inspect(_payload, _target);
        }
        catch (Exception ex)
        {
            ShowError($"检查失败：{ex.Message}");
        }
    }

    private void UpdateCheckRows()
    {
        if (_assessment is null)
        {
            var waiting = _target is null ? "等待目标程序" : "等待部署源";
            SetRow(_architectureValue, waiting);
            SetRow(_versionValue, waiting);
            SetRow(_configValue, waiting);
            SetRow(_architectureAction, "—");
            SetRow(_versionAction, "—");
            SetRow(_configAction, "—");
            return;
        }

        SetRow(_architectureValue, _assessment.ArchitectureStatus,
            _assessment.ArchitectureMatches ? StatusKind.Success : StatusKind.Failure);
        SetRow(_architectureAction, _assessment.ArchitectureMatches ? "允许部署" : "已禁止",
            _assessment.ArchitectureMatches ? StatusKind.Success : StatusKind.Failure);

        ApplyFileRow(_assessment.VersionDll, _versionValue, _versionAction);
        ApplyFileRow(_assessment.Config, _configValue, _configAction);
    }

    private void ApplyFileRow(FileInspection inspection, Label statusLabel, Label actionLabel)
    {
        if (!inspection.Exists)
        {
            SetRow(statusLabel, "缺失", StatusKind.Warning);
            SetRow(actionLabel, "将复制", StatusKind.Success);
            return;
        }

        if (!inspection.Readable)
        {
            SetRow(statusLabel, "无法读取", StatusKind.Failure);
            SetRow(actionLabel, "需关闭程序后重试", StatusKind.Failure);
            return;
        }

        if (inspection.HashMatches)
        {
            SetRow(statusLabel, "已存在，内容一致", StatusKind.Success);
            SetRow(actionLabel, "跳过", StatusKind.Neutral);
            return;
        }

        SetRow(statusLabel, "已存在，内容不同", StatusKind.Warning);
        SetRow(actionLabel, _overwriteCheckBox.Checked ? "将覆盖（先备份）" : "保持不变（未勾选覆盖）",
            _overwriteCheckBox.Checked ? StatusKind.Warning : StatusKind.Neutral);
    }

    /// <summary>
    /// Cheap refresh: only re-detects whether Antigravity is running, so the deploy
    /// action cannot be attempted against a live install. When nothing relevant changed,
    /// the whole pass is skipped — that is what keeps the 1.5s poll from repainting the window.
    /// </summary>
    private void RefreshRunningState()
    {
        _targetRunning = _target is not null && DeploymentService.IsApplicationRunning(_target);

        var signature = BuildRefreshSignature();
        if (signature == _refreshSignature)
        {
            return;
        }

        _refreshSignature = signature;

        BeginLayoutBatch();
        try
        {
            UpdateCardStatus();
            UpdatePrimaryAction();
            UpdateStatusMessage();
            if (!_bannerPinned)
            {
                UpdateSteadyBanner();
            }
        }
        finally
        {
            EndLayoutBatch();
        }
    }

    private string BuildRefreshSignature()
    {
        var inspection = _assessment is null
            ? "none"
            : string.Concat(
                _assessment.ArchitectureMatches ? "a" : "-",
                _assessment.HasUnreadableFiles ? "u" : "-",
                _assessment.HasMissingFiles ? "m" : "-",
                _assessment.HasDifferentFiles ? "d" : "-",
                DescribeFile(_assessment.VersionDll),
                DescribeFile(_assessment.Config));

        return string.Join("|",
            _target?.ExecutablePath ?? string.Empty,
            _payload?.DirectoryPath ?? string.Empty,
            inspection,
            _targetRunning ? "running" : "idle",
            _overwriteCheckBox.Checked ? "overwrite" : "keep",
            _fetchCancellation is null ? "ready" : "fetching",
            _bannerPinned ? "pinned" : "steady");
    }

    private static string DescribeFile(FileInspection inspection) =>
        $"{(inspection.Exists ? "e" : "-")}{(inspection.Readable ? "r" : "-")}{(inspection.HashMatches ? "h" : "-")}";

    /// <summary>Forces the next refresh to repaint, e.g. after a deployment changed the files.</summary>
    private void InvalidateRefreshSignature()
    {
        _refreshSignature = string.Empty;
    }

    private void UpdateCardStatus()
    {
        if (_target is null || _payload is null || _assessment is null)
        {
            _checkCard.StatusText = _target is null && _payload is null
                ? "等待目标程序与部署源"
                : _assessment is null ? "等待目标程序与部署源" : "待检查";
            _checkCard.StatusKind = StatusKind.Neutral;
            return;
        }

        if (!_assessment.ArchitectureMatches)
        {
            _checkCard.StatusText = "架构不匹配";
            _checkCard.StatusKind = StatusKind.Failure;
            return;
        }

        if (_assessment.HasUnreadableFiles)
        {
            _checkCard.StatusText = "目标文件被占用";
            _checkCard.StatusKind = StatusKind.Failure;
            return;
        }

        if (_targetRunning)
        {
            _checkCard.StatusText = "Antigravity 正在运行";
            _checkCard.StatusKind = StatusKind.Warning;
            return;
        }

        if (_assessment.HasWork(_overwriteCheckBox.Checked))
        {
            var pending = PendingCount();
            _checkCard.StatusText = $"{pending} 个文件待复制";
            _checkCard.StatusKind = StatusKind.Success;
            return;
        }

        if (_assessment.HasDifferentFiles && !_overwriteCheckBox.Checked)
        {
            _checkCard.StatusText = "需要勾选覆盖才能更新";
            _checkCard.StatusKind = StatusKind.Warning;
            return;
        }

        _checkCard.StatusText = "已是最新";
        _checkCard.StatusKind = StatusKind.Success;
    }

    private int PendingCount()
    {
        if (_assessment is null)
        {
            return 0;
        }

        var count = 0;
        if (!_assessment.VersionDll.Exists || (_overwriteCheckBox.Checked && !_assessment.VersionDll.HashMatches))
        {
            count++;
        }

        if (!_assessment.Config.Exists || (_overwriteCheckBox.Checked && !_assessment.Config.HashMatches))
        {
            count++;
        }

        return count;
    }

    private void UpdatePrimaryAction()
    {
        if (_fetchCancellation is not null)
        {
            ApplyPrimary("正在获取最新构建…", false, ButtonVariant.Primary, "下载完成后即可部署", null);
            return;
        }

        if (_target is null)
        {
            ApplyPrimary("选择 Antigravity 程序", true, ButtonVariant.Primary,
                "自动检测没有找到 Antigravity，点击手动选择快捷方式或 Antigravity.exe", ChooseTargetFromDialog);
            return;
        }

        if (_payload is null)
        {
            ApplyPrimary("从 GitHub 获取最新构建", true, ButtonVariant.Primary,
                "已找到目标程序，点击获取与它架构一致的部署文件", () => _ = FetchLatestAsync());
            return;
        }

        if (_assessment is null)
        {
            ApplyPrimary("准备中…", false, ButtonVariant.Primary, string.Empty, null);
            return;
        }

        if (!_assessment.ArchitectureMatches)
        {
            ApplyPrimary("架构不匹配，无法部署", false, ButtonVariant.Primary, _assessment.ArchitectureStatus, null);
            return;
        }

        if (_assessment.HasUnreadableFiles)
        {
            ApplyPrimary("目标文件无法读取", false, ButtonVariant.Primary,
                "请完全退出 Antigravity（含系统托盘）后重试", null);
            return;
        }

        if (_targetRunning && _assessment.HasWork(_overwriteCheckBox.Checked))
        {
            ApplyPrimary("请先完全退出 Antigravity", false, ButtonVariant.Primary,
                "检测到 Antigravity 正在运行，运行中的文件无法替换", null);
            return;
        }

        if (!_assessment.HasWork(_overwriteCheckBox.Checked))
        {
            if (_assessment.HasDifferentFiles && !_overwriteCheckBox.Checked)
            {
                ApplyPrimary("勾选覆盖后才能更新", false, ButtonVariant.Primary,
                    "目标目录已有内容不同的文件，默认不覆盖；勾选上方的覆盖选项后才可替换", null);
                return;
            }

            ApplyPrimary("启动 Antigravity", true, ButtonVariant.Success,
                "文件已一致，可直接启动使用；启动前请确认已退出旧进程", LaunchTarget);
            return;
        }

        var pending = PendingCount();
        var text = _assessment.HasMissingFiles
            ? pending == 1 ? "复制缺少的文件" : $"复制缺少的 {pending} 个文件"
            : $"复制并覆盖 {pending} 个文件";
        ApplyPrimary(text, true, ButtonVariant.Primary,
            $"写入 {_target.DirectoryPath}，覆盖前自动备份", RunDeployment);
    }

    private void ApplyPrimary(string text, bool enabled, ButtonVariant variant, string tooltip, Action? handler)
    {
        var changed = _primaryButton.Text != text ||
                      _primaryButton.Enabled != enabled ||
                      _primaryButton.Variant != variant;

        SetText(_primaryButton, text);
        _primaryButton.Variant = variant;
        _primaryButton.Enabled = enabled;
        _primaryButton.Tag = handler;
        _primaryButton.Cursor = handler is not null && enabled ? Cursors.Hand : Cursors.Default;
        _toolTip.SetToolTip(_primaryButton, tooltip);
        _primaryButton.AccessibleName = text;

        if (changed)
        {
            _primaryButton.Invalidate();
        }
    }

    private void UpdateStatusMessage()
    {
        if (_target is null && _payload is null)
        {
            SetMessage(StatusKind.Neutral, "未检测到 Antigravity。拖入快捷方式，或直接点击右下角按钮手动选择。");
            return;
        }

        if (_target is null)
        {
            SetMessage(StatusKind.Neutral, "部署源已就绪，还需要选择目标程序。");
            return;
        }

        if (_payload is null)
        {
            SetMessage(StatusKind.Neutral, "已自动找到 Antigravity，接下来获取或选择部署源。");
            return;
        }

        if (_assessment is null)
        {
            SetMessage(StatusKind.Neutral, "正在检查目标目录…");
            return;
        }

        if (_targetRunning && _assessment.HasWork(_overwriteCheckBox.Checked))
        {
            SetMessage(StatusKind.Warning, "Antigravity 正在运行，请完全退出后再部署（退出后本提示会自动消失）。");
            return;
        }

        if (!_assessment.ArchitectureMatches)
        {
            SetMessage(StatusKind.Failure, _assessment.ArchitectureStatus);
            return;
        }

        if (_assessment.HasWork(_overwriteCheckBox.Checked))
        {
            SetMessage(StatusKind.Success, $"检查通过：{PendingCount()} 个文件待写入目标目录。");
            return;
        }

        if (!_assessment.HasMissingFiles && !_assessment.HasDifferentFiles)
        {
            SetMessage(StatusKind.Success, "代理文件已经部署，与当前部署源完全一致。");
            return;
        }

        SetMessage(StatusKind.Warning, "目标目录存在内容不同的文件；勾选覆盖选项后才会更新。");
    }

    private void SetMessage(StatusKind kind, string text)
    {
        var color = kind == StatusKind.Neutral ? Theme.TextSecondary : Theme.Palette(kind).Text;
        if (_statusLabel.Text == text && _statusLabel.ForeColor == color)
        {
            return;
        }

        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
        _toolTip.SetToolTip(_statusLabel, text);
    }

    private void SetRow(Label label, string text, StatusKind kind = StatusKind.Neutral)
    {
        var color = kind == StatusKind.Neutral ? Theme.TextTertiary : Theme.Palette(kind).Text;
        if (label.Text == text && label.ForeColor == color)
        {
            return;
        }

        label.Text = text;
        label.ForeColor = color;
        _toolTip.SetToolTip(label, text);
    }

    private void UpdateSteadyBanner()
    {
        if (_assessment is null || _target is null || _payload is null ||
            !_assessment.ArchitectureMatches || _targetRunning)
        {
            HideBanner();
            return;
        }

        var allMatch = !_assessment.HasMissingFiles && !_assessment.HasDifferentFiles;
        if (!allMatch)
        {
            HideBanner();
            return;
        }

        ShowBanner(
            StatusKind.Success,
            "version.dll 与 config.json 已与当前部署源一致，可以直接使用。",
            pinned: false,
            launchActions: true);
    }

    private void ShowBanner(
        StatusKind kind,
        string text,
        bool pinned,
        bool launchActions = false,
        bool openReleases = false)
    {
        var signature = $"{kind}|{text}|{launchActions}|{openReleases}";
        _bannerPinned = pinned;
        if (signature == _bannerSignature)
        {
            return;
        }

        _bannerSignature = signature;
        _banner.Show(kind, text);

        var actions = new List<BannerAction>();
        if (launchActions && _target is not null)
        {
            // The bottom bar already owns 启动 Antigravity as the primary action in exactly these
            // states, so the banner only adds the secondary one. Two identical calls to action on
            // screen would leave the user wondering which one is authoritative.
            actions.Add(new BannerAction("打开安装目录", ButtonVariant.Secondary, RevealTargetDirectory));
        }

        if (openReleases)
        {
            actions.Add(new BannerAction("打开 Releases 页面", ButtonVariant.Secondary,
                () => AntigravityLauncher.OpenUrl(RemotePayloadService.ReleasePageUrl)));
        }

        if (actions.Count > 0)
        {
            _banner.ShowActions(actions.ToArray());
        }
    }

    private void HideBanner()
    {
        if (string.IsNullOrEmpty(_bannerSignature))
        {
            return;
        }

        _bannerSignature = string.Empty;
        _banner.Dismiss();
    }

    private void ShowError(string message, bool openReleases = false)
    {
        ShowBanner(StatusKind.Failure, message, pinned: true, openReleases: openReleases);
        SetMessage(StatusKind.Failure, FirstLine(message));
    }

    private static string FirstLine(string message)
    {
        var index = message.IndexOfAny(new[] { '\r', '\n' });
        return index < 0 ? message : message[..index];
    }
}
