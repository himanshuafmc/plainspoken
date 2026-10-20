using System.Globalization;
using Plainspoken.Core;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Storage;

namespace Plainspoken.App.UI;

/// <summary>"Speed and cost": per service, how long dictations took and roughly what they cost. Never shows any text.</summary>
internal sealed class StatsForm : Form
{
    private readonly StatsStore _stats;
    private readonly Func<PlainspokenSettings> _settings;
    private readonly ListView _table = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Dock = DockStyle.Fill,
    };
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new Size(640, 0), Dock = DockStyle.Top, Padding = new Padding(8, 8, 8, 4) };

    public StatsForm(StatsStore stats, Func<PlainspokenSettings> settings)
    {
        _stats = stats;
        _settings = settings;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"{AppInfo.Name} — speed and cost";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(700, 360);
        MinimumSize = new Size(480, 260);

        foreach (var (title, width) in new[]
                 {
                     ("Service", 170), ("Dictations", 75), ("Failed", 55), ("Wait (median)", 95), ("With clean-up", 95), ("Streamed", 70),
                     ("Minutes this month", 115), ("Cost this month", 100),
                 })
        {
            _table.Columns.Add(title, width);
        }

        var note = new Label
        {
            Text = "Wait = from the end of your speech to the text being ready (median, so one slow request doesn't distort it). " +
                   "Costs are estimates from the price per hour you set for each service (Settings → General → Set up…). " +
                   "Only timings are kept here, never what you said.",
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Bottom,
            Padding = new Padding(8, 4, 8, 4),
        };
        var clear = Ui.Button("Clear", (_, _) =>
        {
            if (MessageBox.Show(this, "Delete the saved timings?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _stats.Clear();
                Reload();
            }
        });
        var close = Ui.Button("Close", (_, _) => Close());
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(clear);
        CancelButton = close;

        Controls.Add(_table);
        Controls.Add(_summary);
        Controls.Add(note);
        Controls.Add(buttons);
        Reload();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Ui.FitToScreen(this);
    }

    private void Reload()
    {
        var settings = _settings();
        var t = settings.Transcription;
        var summary = _stats.Summary(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        _table.BeginUpdate();
        _table.Items.Clear();
        var totalCost = 0.0;
        foreach (var s in summary)
        {
            var price = t.Services[s.Service].PricePerHour;
            var cost = price > 0 ? s.MinutesThisMonth / 60 * price : (double?)null;
            totalCost += cost ?? 0;
            _table.Items.Add(new ListViewItem(
            [
                s.Service.DisplayName(),
                s.Dictations.ToString(CultureInfo.CurrentCulture),
                s.Failures.ToString(CultureInfo.CurrentCulture),
                Seconds(s.MedianListenMs),
                Seconds(s.MedianTotalMs),
                s.StreamedShare.ToString("P0", CultureInfo.CurrentCulture),
                s.MinutesThisMonth.ToString("0.0", CultureInfo.CurrentCulture),
                cost is { } c ? t.Currency + c.ToString("0.00", CultureInfo.CurrentCulture) : "—",
            ]));
        }

        _table.EndUpdate();
        _summary.Text = summary.Count == 0
            ? "No dictations recorded yet."
            : $"Last {_stats.All.Count} dictations. Estimated cost this month: {t.Currency}{totalCost.ToString("0.00", CultureInfo.CurrentCulture)}.";
    }

    private static string Seconds(long? ms) => ms is { } v ? (v / 1000.0).ToString("0.0 s", CultureInfo.CurrentCulture) : "—";
}
