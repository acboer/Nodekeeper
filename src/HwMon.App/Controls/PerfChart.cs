using System.Windows;
using System.Windows.Media;

namespace HwMon.App.Controls;

/// <summary>
/// Gráfico da aba Desempenho (como o do Gerenciador de Tarefas): últimos 60 s com grade, área preenchida e uma
/// segunda série opcional tracejada (gravação, envio). O máximo vem de fora (o view model escolhe a escala).
/// </summary>
public sealed class PerfChart : FrameworkElement
{
    private static FrameworkPropertyMetadata Render(object value) => new(value, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(PerfChart), Render(Array.Empty<double>()));
    public static readonly DependencyProperty Values2Property = DependencyProperty.Register(nameof(Values2), typeof(IReadOnlyList<double>), typeof(PerfChart), Render(null!));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(PerfChart), Render(100.0));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(PerfChart), Render(Brushes.MediumPurple));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(PerfChart), Render(Brushes.Transparent));
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(PerfChart), Render(60));
    /// <summary>Linhas da grade: 0 = sem grade (gráficos pequenos por núcleo só com a moldura).</summary>
    public static readonly DependencyProperty GridLinesProperty = DependencyProperty.Register(nameof(GridLines), typeof(int), typeof(PerfChart), Render(10));

    public IReadOnlyList<double> Values { get => (IReadOnlyList<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? Values2 { get => (IReadOnlyList<double>?)GetValue(Values2Property); set => SetValue(Values2Property, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public int GridLines { get => (int)GetValue(GridLinesProperty); set => SetValue(GridLinesProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.MediumPurple;
        var frame = new Pen(new SolidColorBrush(Color.FromArgb(110, color.R, color.G, color.B)), 1);
        frame.Freeze();
        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(10, color.R, color.G, color.B)), null, rect);

        if (GridLines > 0)
        {
            var grid = new Pen(GridBrush, 1);
            grid.Freeze();
            for (var i = 1; i < GridLines; i++)
            {
                var y = Math.Round(h * i / GridLines) + 0.5;
                dc.DrawLine(grid, new Point(1, y), new Point(w - 1, y));
            }
            var columns = Math.Max(1, (int)Math.Round(GridLines * w / Math.Max(1, h) / 2));
            for (var i = 1; i < columns; i++)
            {
                var x = Math.Round(w * i / columns) + 0.5;
                dc.DrawLine(grid, new Point(x, 1), new Point(x, h - 1));
            }
        }

        dc.PushClip(new RectangleGeometry(rect));
        Draw(dc, Values2, color, w, h, dashed: true);
        Draw(dc, Values, color, w, h, dashed: false);
        dc.Pop();
        dc.DrawRectangle(null, frame, rect);
    }

    private void Draw(DrawingContext dc, IReadOnlyList<double>? values, Color color, double w, double h, bool dashed)
    {
        if (values is not { Count: > 1 }) return;
        var max = Maximum > 0 ? Maximum : 1;
        var step = w / Math.Max(1, Capacity - 1);
        var start = w - (values.Count - 1) * step;
        Point P(int i) => new(start + i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 2) - 1);

        if (!dashed)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(start, h), true, true);
                for (var i = 0; i < values.Count; i++) g.LineTo(P(i), true, true);
                g.LineTo(new Point(w, h), true, true);
            }
            area.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B));
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (var i = 1; i < values.Count; i++) g.LineTo(P(i), true, true);
        }
        line.Freeze();
        var pen = new Pen(new SolidColorBrush(color), dashed ? 1.2 : 1.5) { LineJoin = PenLineJoin.Round };
        if (dashed) pen.DashStyle = new DashStyle([3, 2], 0);
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
