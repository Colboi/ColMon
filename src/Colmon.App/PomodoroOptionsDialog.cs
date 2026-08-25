namespace Colmon;

internal sealed class PomodoroOptionsDialog : Form
{
    private readonly CheckBox _autoRest = new() { Text = "自动休息", AutoSize = true };
    private readonly CheckBox _autoNextCycle = new() { Text = "自动进入下一循环", AutoSize = true };
    private readonly NumericUpDown _workMinutes = CreateMinutesInput();
    private readonly NumericUpDown _restMinutes = CreateMinutesInput();

    public PomodoroOptionsDialog(PomodoroOptions options)
    {
        options = options.Normalize();
        Text = "窗口选项";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(370, 220);
        Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular, GraphicsUnit.Point);

        _autoRest.Checked = options.AutoRest;
        _autoNextCycle.Checked = options.AutoNextCycle;
        _workMinutes.Value = options.WorkMinutes;
        _restMinutes.Value = options.RestMinutes;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        for (var index = 0; index < 4; index++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.SetColumnSpan(_autoRest, 2);
        layout.Controls.Add(_autoRest, 0, 0);
        layout.SetColumnSpan(_autoNextCycle, 2);
        layout.Controls.Add(_autoNextCycle, 0, 1);
        layout.Controls.Add(new Label { Text = "“工作”时间段时长（min）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _workMinutes.Dock = DockStyle.Fill;
        layout.Controls.Add(_workMinutes, 1, 2);
        layout.Controls.Add(new Label { Text = "“休息”时间段时长（min）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _restMinutes.Dock = DockStyle.Fill;
        layout.Controls.Add(_restMinutes, 1, 3);

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
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    public PomodoroOptions Options => new(
        _autoRest.Checked,
        _autoNextCycle.Checked,
        Decimal.ToInt32(_workMinutes.Value),
        Decimal.ToInt32(_restMinutes.Value));

    private static NumericUpDown CreateMinutesInput() => new()
    {
        Minimum = PomodoroOptions.MinimumMinutes,
        Maximum = PomodoroOptions.MaximumMinutes,
        TextAlign = HorizontalAlignment.Right,
        ThousandsSeparator = true
    };
}
