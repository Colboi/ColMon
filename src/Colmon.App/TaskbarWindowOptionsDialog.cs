namespace Colmon;

internal sealed class TaskbarWindowOptionsDialog : Form
{
    private readonly TextBox _titleBox = new();
    private readonly NumericUpDown _refreshInterval = new()
    {
        Minimum = WindowOptions.MinimumRefreshIntervalSeconds,
        Maximum = WindowOptions.MaximumRefreshIntervalSeconds,
        Increment = 10,
        TextAlign = HorizontalAlignment.Right,
        ThousandsSeparator = true
    };

    public TaskbarWindowOptionsDialog(WindowOptions options)
    {
        Text = "窗口选项";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(360, 150);
        Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point);

        _titleBox.Text = options.Title;
        _titleBox.MaxLength = 80;
        _refreshInterval.Value = Math.Clamp(
            options.RefreshIntervalSeconds,
            WindowOptions.MinimumRefreshIntervalSeconds,
            WindowOptions.MaximumRefreshIntervalSeconds);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "第一行文字", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _titleBox.Dock = DockStyle.Fill;
        layout.Controls.Add(_titleBox, 1, 0);
        layout.Controls.Add(new Label { Text = "刷新频率（秒）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _refreshInterval.Dock = DockStyle.Fill;
        layout.Controls.Add(_refreshInterval, 1, 1);

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    public WindowOptions Options =>
        new WindowOptions(_titleBox.Text, Decimal.ToInt32(_refreshInterval.Value)).Normalize();

}
