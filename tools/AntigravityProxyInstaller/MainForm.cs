using System.Drawing;

namespace AntigravityProxyInstaller;

internal sealed class MainForm : Form
{
    private enum StatusKind { Pending, Success, Failure }

    // ── 设计令牌：统一色彩与间距 ──────────────────────────────────────
    private static readonly Color ColorSuccess = Color.FromArgb(22, 163, 74);
    private static readonly Color ColorError = Color.FromArgb(220, 38, 38);
    private static readonly Color ColorPending = Color.FromArgb(107, 114, 128);
    private static readonly Color ColorSubtitle = Color.FromArgb(107, 114, 128);
    private static readonly Color ColorFieldLabel = Color.FromArgb(75, 85, 99);
    private static readonly Color ColorTitle = Color.FromArgb(17, 24, 39);
    private static readonly Color ColorSurface = Color.FromArgb(243, 244, 246);
    private static readonly Color ColorSurfaceHover = Color.FromArgb(219, 234, 254);
    private static readonly Color ColorBorder = Color.FromArgb(209, 213, 219);
    private static readonly Color ColorDropText = Color.FromArgb(55, 65, 81);

    private const int FieldLabelWidth = 92;
    private const int GroupSpacing = 12;

    // ── 控件字段（保持原有引用名不变） ────────────────────────────────
    private readonly TextBox _shortcutPathText = CreateReadOnlyTextBox();
    private readonly TextBox _executablePathText = CreateReadOnlyTextBox();
    private readonly TextBox _directoryPathText = CreateReadOnlyTextBox();
    private readonly TextBox _payloadDirectoryText = CreateReadOnlyTextBox();
    private readonly Label _payloadStatusLabel = CreateStatusLabel();
    private readonly Label _architectureStatusLabel = CreateStatusLabel();
    private readonly Label _versionStatusLabel = CreateStatusLabel();
    private readonly Label _configStatusLabel = CreateStatusLabel();
    private readonly Label _messageLabel = CreateMessageLabel();
    private readonly ToolTip _toolTip = new();
    private readonly CheckBox _overwriteCheckBox = new();
    private readonly Button _deployButton = new();
    private readonly Button _buildButton = new();
    private readonly Button _choosePayloadButton = new();
    private readonly Panel _dropPanel = new();

    private PayloadInfo? _payload;
    private TargetInfo? _target;
    private DeploymentAssessment? _assessment;
    private CancellationTokenSource? _buildCancellation;

    public MainForm()
    {
        Text = "Antigravity Proxy 部署助手";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(900, 680);
        ClientSize = new Size(980, 760);
        Padding = new Padding(20, 16, 20, 16);
        AllowDrop = true;
        BackColor = Color.White;

        BuildUi();

        _toolTip.AutoPopDelay = 10000;
        _toolTip.InitialDelay = 400;
        _toolTip.ReshowDelay = 100;
        _toolTip.ShowAlways = true;

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        _payload = PayloadLocator.TryFindDefault(out var defaultPayload) ? defaultPayload : null;
        if (InstalledApplicationLocator.TryFindDefault(out var defaultTarget))
        {
            _target = defaultTarget;
        }
        UpdateView();
    }

    // ══════════════════════════════════════════════════════════════════
    //  UI 构建
    // ══════════════════════════════════════════════════════════════════

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            AutoSize = false,
            BackColor = Color.Transparent
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 0  标题区
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88F));  // 1  拖放区
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 2  目标信息
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 3  部署源
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 4  检查结果
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));  // 5  弹性留白
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 6  选项
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));  // 7  操作栏
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildDropZone(), 0, 1);
        root.Controls.Add(BuildTargetGroup(), 0, 2);
        root.Controls.Add(BuildPayloadGroup(), 0, 3);
        root.Controls.Add(BuildStatusGroup(), 0, 4);
        // 第 5 行为弹性留白，无需添加控件
        root.Controls.Add(BuildOptionsRow(), 0, 6);
        root.Controls.Add(BuildActionBar(), 0, 7);
    }

    /// <summary>标题 + 副标题。</summary>
    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 14)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Antigravity Proxy 部署助手",
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size + 4F, FontStyle.Bold),
            ForeColor = ColorTitle,
            Margin = new Padding(0, 0, 0, 3)
        };

        var subtitle = new Label
        {
            Text = "自动获取 GitHub 最新构建；拖入快捷方式后检查并部署代理文件",
            AutoSize = true,
            ForeColor = ColorSubtitle,
            Margin = new Padding(0)
        };

        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(subtitle, 0, 1);
        return panel;
    }

    /// <summary>拖放区域，带悬停高亮反馈。</summary>
    private Control BuildDropZone()
    {
        _dropPanel.Dock = DockStyle.Fill;
        _dropPanel.AllowDrop = true;
        _dropPanel.BorderStyle = BorderStyle.FixedSingle;
        _dropPanel.BackColor = ColorSurface;
        _dropPanel.Margin = new Padding(0, 0, 0, GroupSpacing + 2);

        var dropLabel = new Label
        {
            Text = "将 Antigravity 快捷方式拖到这里",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, Font.Size + 1.5F, FontStyle.Bold),
            ForeColor = ColorDropText
        };
        _dropPanel.Controls.Add(dropLabel);

        _dropPanel.DragEnter += OnDropZoneDragEnter;
        _dropPanel.DragLeave += OnDropZoneDragLeave;
        _dropPanel.DragDrop += OnDropZoneDragDrop;

        return _dropPanel;
    }

    /// <summary>目标信息分组：快捷方式 / 执行文件 / 目标目录。</summary>
    private Control BuildTargetGroup()
    {
        var group = CreateGroupBox("目标信息");
        var table = CreateFieldTable(3);
        AddField(table, 0, "快捷方式", _shortcutPathText);
        AddField(table, 1, "执行文件", _executablePathText);
        AddField(table, 2, "目标目录", _directoryPathText);
        group.Controls.Add(table);
        return group;
    }

    /// <summary>部署源分组：路径 + 选择按钮 / 状态。</summary>
    private Control BuildPayloadGroup()
    {
        var group = CreateGroupBox("部署源");
        var table = CreateFieldTable(2);

        // 第 0 行：路径文本框 + 选择文件夹按钮
        table.Controls.Add(CreateFieldLabel("路径"), 0, 0);
        var chooser = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = false,
            Margin = new Padding(0)
        };
        chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        var chooseButtonWidth = Math.Max(132, TextRenderer.MeasureText("选择文件夹", Font).Width + 28);
        chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, chooseButtonWidth));
        chooser.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _payloadDirectoryText.Dock = DockStyle.Fill;
        _payloadDirectoryText.Margin = new Padding(0, 5, 8, 5);

        _choosePayloadButton.Text = "选择文件夹";
        _choosePayloadButton.Dock = DockStyle.Fill;
        _choosePayloadButton.Margin = new Padding(0, 3, 0, 3);
        _choosePayloadButton.UseVisualStyleBackColor = true;
        _choosePayloadButton.Click += OnChoosePayloadClick;

        chooser.Controls.Add(_payloadDirectoryText, 0, 0);
        chooser.Controls.Add(_choosePayloadButton, 1, 0);
        table.Controls.Add(chooser, 1, 0);

        // 第 1 行：状态
        AddField(table, 1, "状态", _payloadStatusLabel);

        group.Controls.Add(table);
        return group;
    }

    /// <summary>检查结果分组：架构 / version.dll / config.json。</summary>
    private Control BuildStatusGroup()
    {
        var group = CreateGroupBox("检查结果");
        var table = CreateFieldTable(3);
        AddField(table, 0, "架构", _architectureStatusLabel);
        AddField(table, 1, "version.dll", _versionStatusLabel);
        AddField(table, 2, "config.json", _configStatusLabel);
        group.Controls.Add(table);
        return group;
    }

    /// <summary>覆盖选项复选框。</summary>
    private Control BuildOptionsRow()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 6)
        };

        _overwriteCheckBox.Text = "允许覆盖内容不同的文件（覆盖前自动备份）";
        _overwriteCheckBox.AutoSize = true;
        _overwriteCheckBox.Dock = DockStyle.Fill;
        _overwriteCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _overwriteCheckBox.CheckedChanged += (_, _) => UpdateView();

        panel.Controls.Add(_overwriteCheckBox);
        return panel;
    }

    /// <summary>底部操作栏：分隔线 + 状态消息 + 部署按钮。</summary>
    private Control BuildActionBar()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };

        // 顶部分隔线
        var separator = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = ColorBorder
        };

        // 消息 + 按钮
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0, 10, 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        var fetchButtonWidth = Math.Max(144, TextRenderer.MeasureText("获取最新编译", Font).Width + 28);
        var deployButtonWidth = Math.Max(154, TextRenderer.MeasureText("复制缺少的文件", Font).Width + 28);
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, fetchButtonWidth));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, deployButtonWidth));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _messageLabel.Dock = DockStyle.Fill;
        _messageLabel.TextAlign = ContentAlignment.MiddleLeft;
        _messageLabel.Margin = new Padding(0, 0, 12, 0);

        _deployButton.Text = "复制缺少的文件";
        _deployButton.AutoSize = false;
        _deployButton.MinimumSize = new Size(0, 32);
        _deployButton.Dock = DockStyle.Fill;
        _deployButton.Enabled = false;
        _deployButton.Margin = new Padding(0, 2, 0, 6);
        _deployButton.UseVisualStyleBackColor = true;
        _deployButton.Click += OnDeployClick;

        _buildButton.Text = "获取最新编译";
        _buildButton.AutoSize = false;
        _buildButton.MinimumSize = new Size(0, 32);
        _buildButton.Dock = DockStyle.Fill;
        _buildButton.Margin = new Padding(0, 2, 8, 6);
        _buildButton.UseVisualStyleBackColor = true;
        _buildButton.Click += OnFetchLatestClick;

        content.Controls.Add(_messageLabel, 0, 0);
        content.Controls.Add(_buildButton, 1, 0);
        content.Controls.Add(_deployButton, 2, 0);

        // 注意添加顺序：Dock=Fill 先添加（后停靠），Dock=Top 后添加（先停靠）
        panel.Controls.Add(content);
        panel.Controls.Add(separator);

        return panel;
    }

    // ══════════════════════════════════════════════════════════════════
    //  控件工厂
    // ══════════════════════════════════════════════════════════════════

    private static GroupBox CreateGroupBox(string title)
    {
        return new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 12, 10),
            Margin = new Padding(0, 0, 0, GroupSpacing)
        };
    }

    private static TableLayoutPanel CreateFieldTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, FieldLabelWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var row = 0; row < rows; row++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return table;
    }

    private static void AddField(TableLayoutPanel table, int row, string name, Control control)
    {
        table.Controls.Add(CreateFieldLabel(name), 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 5, 0, 5);
        table.Controls.Add(control, 1, row);
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ColorFieldLabel,
            Margin = new Padding(0, 5, 0, 5)
        };
    }

    private static TextBox CreateReadOnlyTextBox()
    {
        return new TextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Window,
            Margin = new Padding(0, 5, 0, 5)
        };
    }

    private static Label CreateStatusLabel()
    {
        return new Label
        {
            AutoEllipsis = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ColorPending,
            Margin = new Padding(0, 5, 0, 5),
            MinimumSize = new Size(0, 26)
        };
    }

    private static Label CreateMessageLabel()
    {
        return new Label
        {
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ColorPending,
            Margin = new Padding(0)
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  拖放处理
    // ══════════════════════════════════════════════════════════════════

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDropZoneDragEnter(object? sender, DragEventArgs e)
    {
        OnDragEnter(sender, e);
        if (e.Effect == DragDropEffects.Copy)
        {
            _dropPanel.BackColor = ColorSurfaceHover;
        }
    }

    private void OnDropZoneDragLeave(object? sender, EventArgs e)
    {
        ResetDropZone();
    }

    private void OnDropZoneDragDrop(object? sender, DragEventArgs e)
    {
        ResetDropZone();
        OnDragDrop(sender, e);
    }

    private void ResetDropZone()
    {
        _dropPanel.BackColor = ColorSurface;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        var droppedPath = paths.FirstOrDefault(path =>
            Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(droppedPath))
        {
            ShowError("请拖入 Antigravity 的 .lnk 快捷方式。");
            return;
        }

        if (!DeploymentInspector.TryCreateTarget(droppedPath, out var target, out var error))
        {
            ShowError(error);
            return;
        }

        _target = target;
        _assessment = null;
        UpdateView();
    }

    // ══════════════════════════════════════════════════════════════════
    //  按钮事件
    // ══════════════════════════════════════════════════════════════════

    private void OnChoosePayloadClick(object? sender, EventArgs e)
    {
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

        if (!PayloadLocator.TryLoad(dialog.SelectedPath, out var payload, out var error))
        {
            ShowError(error);
            return;
        }

        _payload = payload;
        UpdateView();
    }

    private void OnDeployClick(object? sender, EventArgs e)
    {
        if (_assessment is null)
        {
            return;
        }

        try
        {
            var result = DeploymentService.Deploy(_assessment, _overwriteCheckBox.Checked);
            var copied = result.CopiedFiles.Count == 0
                ? "没有需要复制的文件"
                : $"已复制：{string.Join("、", result.CopiedFiles)}";
            var backup = result.BackupFiles.Count == 0
                ? string.Empty
                : $"\n已备份：{result.BackupFiles.Count} 个文件";
            MessageBox.Show(this, copied + backup, "部署完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateView();
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("没有权限写入目标目录。请关闭 Antigravity，并以管理员身份重新运行此工具。");
        }
        catch (IOException ex)
        {
            ShowError($"文件复制失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnFetchLatestClick(object? sender, EventArgs e)
    {
        if (_buildCancellation is not null)
        {
            return;
        }

        var architecture = _target?.Architecture ?? PeArchitecture.X64;
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
        {
            ShowError("目前 GitHub 最新构建仅提供 x86 和 x64 版本，请手动选择匹配的部署源。");
            return;
        }

        var architectureName = architecture == PeArchitecture.X86 ? "x86" : "x64";
        _buildCancellation = new CancellationTokenSource();
        _buildButton.Enabled = false;
        _buildButton.Text = "获取中...";
        SetStatus(_messageLabel, $"正在获取 GitHub 最新构建（{architectureName}），请稍候", StatusKind.Pending);

        try
        {
            _payload = await RemotePayloadService.DownloadLatestAsync(
                architecture,
                _buildCancellation.Token);
            UpdateView();
            SetStatus(_messageLabel, $"已获取 GitHub 最新构建（{architectureName}），可以部署", StatusKind.Success);
        }
        catch (OperationCanceledException) when (_buildCancellation?.IsCancellationRequested != true)
        {
            ShowError("访问 GitHub 超时，请检查系统代理或网络连接，也可以手动选择部署源。");
        }
        catch (OperationCanceledException)
        {
            SetStatus(_messageLabel, "获取已取消", StatusKind.Pending);
        }
        catch (HttpRequestException ex)
        {
            ShowError($"无法连接 GitHub：{ex.Message}\n请检查系统代理或网络连接，也可以手动选择部署源。");
        }
        catch (Exception ex)
        {
            ShowError($"获取最新编译失败：{ex.Message}");
        }
        finally
        {
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            _buildButton.Enabled = true;
            _buildButton.Text = "获取最新编译";
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  视图更新
    // ══════════════════════════════════════════════════════════════════

    private void UpdateView()
    {
        // ── 部署源 ──
        _payloadDirectoryText.Text = _payload?.DirectoryPath
            ?? "未找到部署源，请选择包含两个文件的文件夹";
        SetStatus(_payloadStatusLabel,
            _payload is null ? "未找到 version.dll 和 config.json" : "部署源有效",
            _payload is not null ? StatusKind.Success : StatusKind.Failure);

        // ── 目标信息 ──
        _shortcutPathText.Text = _target?.ShortcutPath ?? "请先拖入快捷方式";
        _executablePathText.Text = _target?.ExecutablePath ?? string.Empty;
        _directoryPathText.Text = _target?.DirectoryPath ?? string.Empty;
        _toolTip.SetToolTip(_shortcutPathText, _target?.ShortcutPath ?? string.Empty);
        _toolTip.SetToolTip(_executablePathText, _target?.ExecutablePath ?? string.Empty);
        _toolTip.SetToolTip(_directoryPathText, _target?.DirectoryPath ?? string.Empty);
        _toolTip.SetToolTip(_payloadDirectoryText, _payload?.DirectoryPath ?? string.Empty);

        // ── 检查 ──
        _assessment = null;
        if (_payload is not null && _target is not null)
        {
            try
            {
                _assessment = DeploymentInspector.Inspect(_payload, _target);
            }
            catch (Exception ex)
            {
                SetStatus(_messageLabel, $"检查失败：{ex.Message}", StatusKind.Failure);
            }
        }

        if (_assessment is null)
        {
            SetStatus(_architectureStatusLabel,
                _target is null ? "等待目标快捷方式" : "等待部署源",
                StatusKind.Pending);
            SetStatus(_versionStatusLabel, "等待检查", StatusKind.Pending);
            SetStatus(_configStatusLabel, "等待检查", StatusKind.Pending);
            _deployButton.Enabled = false;
            if (_target is null && _payload is not null)
            {
                SetStatus(_messageLabel, "请拖入 Antigravity 快捷方式", StatusKind.Pending);
            }
            else if (_target is not null && _payload is null)
            {
                SetStatus(_messageLabel, "已自动找到 Antigravity，请点击“获取最新编译”或选择部署源", StatusKind.Pending);
            }
            else if (_target is null && _payload is null)
            {
                SetStatus(_messageLabel, "请先点击“获取最新编译”获取部署源，或拖入 Antigravity 快捷方式", StatusKind.Pending);
            }
            return;
        }

        SetStatus(_architectureStatusLabel, _assessment.ArchitectureStatus,
            _assessment.ArchitectureMatches ? StatusKind.Success : StatusKind.Failure);
        SetStatus(_versionStatusLabel, _assessment.VersionDll.DisplayStatus,
            _assessment.VersionDll.HashMatches ? StatusKind.Success : StatusKind.Failure);
        SetStatus(_configStatusLabel, _assessment.Config.DisplayStatus,
            _assessment.Config.HashMatches ? StatusKind.Success : StatusKind.Failure);

        var hasWork = _assessment.HasWork(_overwriteCheckBox.Checked);
        _deployButton.Enabled = hasWork;
        _deployButton.Text = _assessment.HasDifferentFiles && _overwriteCheckBox.Checked
            ? "复制并更新文件"
            : "复制缺少的文件";

        if (!_assessment.ArchitectureMatches)
        {
            SetStatus(_messageLabel, "架构不匹配，已禁止部署", StatusKind.Failure);
        }
        else if (_assessment.HasUnreadableFiles)
        {
            SetStatus(_messageLabel, "目标文件无法读取，请关闭相关程序后重试", StatusKind.Failure);
        }
        else if (!_assessment.HasMissingFiles && !_assessment.HasDifferentFiles)
        {
            SetStatus(_messageLabel, "目标目录已经是当前部署源，无需复制", StatusKind.Success);
        }
        else if (_assessment.HasDifferentFiles && !_overwriteCheckBox.Checked)
        {
            SetStatus(_messageLabel, "存在内容不同的文件；勾选覆盖选项后才会更新", StatusKind.Pending);
        }
        else
        {
            SetStatus(_messageLabel, "检查通过，可以部署", StatusKind.Success);
        }
    }

    /// <summary>设置状态标签文本、前缀符号与颜色。</summary>
    private static void SetStatus(Label label, string text, StatusKind kind)
    {
        var (symbol, color) = kind switch
        {
            StatusKind.Success => ("✓", ColorSuccess),
            StatusKind.Failure => ("✗", ColorError),
            _ => ("•", ColorPending)
        };
        label.Text = $"{symbol}  {text}";
        label.ForeColor = color;
    }

    private void ShowError(string message)
    {
        var summary = message.Replace(Environment.NewLine, " ").Replace("\n", " ");
        if (summary.Length > 96)
        {
            summary = summary[..96] + "…";
        }

        SetStatus(_messageLabel, summary, StatusKind.Failure);
        MessageBox.Show(this, message, "无法继续", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
