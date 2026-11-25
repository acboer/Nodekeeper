using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HwMon.App.Services;
using HwMon.App.ViewModels;

namespace HwMon.App.Views;

public partial class WidgetWindow : Window
{
    private const double DefaultWidth = 270;
    private const double MaxWidthLimit = 2400;

    private readonly WidgetViewModel _vm;
    private readonly SettingsStore _settings;
    /// <summary>Widget único: nulo (usa Left/Top/Width das configurações). Um por PC: o MachineId.</summary>
    private readonly string? _key;

    /// <param name="index">Posição na fila dos widgets por PC, para a primeira vez não ficarem um em cima do outro.</param>
    public WidgetWindow(WidgetViewModel vm, SettingsStore settings, int index = 0)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        vm.Attach();
        DataContext = vm;

        var s = settings.Current.Widget;
        // Widget único: a posição de sempre. Um por PC: cada máquina guarda a sua.
        _key = vm.Fixed?.MachineId;
        Width = SavedWidth() ?? DefaultWidthFor(vm.IsHorizontal);
        double? left = _key is null ? s.Left : s.Positions.GetValueOrDefault(_key)?.Left;
        double? top = _key is null ? s.Top : s.Positions.GetValueOrDefault(_key)?.Top;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (left is { } l && top is { } t && MainWindow.IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            // Primeira vez: canto superior direito da área de trabalho (os seguintes à esquerda do anterior).
            Left = SystemParameters.WorkArea.Right - (Width + 8) * (index + 1) - 8;
            Top = SystemParameters.WorkArea.Top + 16;
        }
        LocationChanged += (_, _) => SavePlacement();
        vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnViewModelChanged;
            vm.Detach();
        };
    }

    // ------------------------------------------------------------------ largura

    /// <summary>Trocou a orientação: volta à largura que o widget tinha nela (ou a padrão).</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WidgetViewModel.IsHorizontal)) return;
        var right = Left + Width;
        Width = SavedWidth() ?? DefaultWidthFor(_vm.IsHorizontal);
        // Ancorado à direita quando estava encostado na borda direita da tela (o lugar padrão do widget).
        if (right >= SystemParameters.WorkArea.Right - 40) Left = Math.Max(SystemParameters.WorkArea.Left, right - Width);
    }

    /// <summary>Horizontal: todas as categorias numa linha, até a largura da tela.</summary>
    private double DefaultWidthFor(bool horizontal)
    {
        if (!horizontal) return DefaultWidth;
        var columns = Math.Max(3, _vm.Groups.Count);
        // Colunas + margem entre elas + bordas (8 de sombra e 13/9 de espaço interno de cada lado).
        var width = columns * (WidgetViewModel.ColumnWidth + 18) + 16 + 22 + 4;
        return Math.Min(width, SystemParameters.WorkArea.Width - 32);
    }

    private double? SavedWidth()
    {
        var s = _settings.Current.Widget;
        var horizontal = _vm.IsHorizontal;
        if (_key is null) return horizontal ? s.WidthHorizontal : s.Width;
        var p = s.Positions.GetValueOrDefault(_key);
        return horizontal ? p?.WidthHorizontal : p?.Width;
    }

    private void SavePlacement()
    {
        var s = _settings.Current.Widget;
        var horizontal = _vm.IsHorizontal;
        if (_key is null)
        {
            s.Left = Left;
            s.Top = Top;
            if (horizontal) s.WidthHorizontal = Width;
            else s.Width = Width;
        }
        else
        {
            var p = s.Positions.GetValueOrDefault(_key) ?? new WidgetPosition();
            p.Left = Left;
            p.Top = Top;
            if (horizontal) p.WidthHorizontal = Width;
            else p.Width = Width;
            s.Positions[_key] = p;
        }
        _settings.SaveSoon();
    }

    private void GripRight_DragDelta(object sender, DragDeltaEventArgs e)
        => Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidthLimit);

    private void GripLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Borda esquerda: a direita fica parada.
        var width = Math.Clamp(Width - e.HorizontalChange, MinWidth, MaxWidthLimit);
        Left += Width - width;
        Width = width;
    }

    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e) => SavePlacement();

    // ------------------------------------------------------------------ ações

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OpenPanel_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowMain();

    private void Close_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).HideWidget();

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Current?.Descriptor is null) return;
        var dialog = new WidgetMetricsWindow(_vm, _vm.Current) { Owner = this, Topmost = Topmost };
        dialog.ShowDialog();
    }
}
