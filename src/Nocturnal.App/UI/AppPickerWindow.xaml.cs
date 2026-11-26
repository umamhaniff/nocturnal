using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nocturnal.Core;

namespace Nocturnal.App.UI;

public partial class AppPickerWindow : Window
{
    private sealed record AppRowItem(string Name, string Subtitle, string RamLabel);

    private readonly List<AppRowItem> _allRows;
    private List<AppRowItem> _visibleRows;

    public string? SelectedProcessName { get; private set; }

    public AppPickerWindow(IReadOnlyList<RunningAppInfo> runningApps)
    {
        InitializeComponent();

        _allRows = runningApps.Select(a =>
        {
            var mb = Math.Round(a.TotalWorkingSetBytes / (1024.0 * 1024.0), 0);
            var tag = a.IsProtectedByDefault ? " • Protected" : "";
            var sub = $"{a.InstanceCount} proc{(a.InstanceCount == 1 ? "" : "s")}{tag}";
            return new AppRowItem(a.ProcessName, sub, $"{mb} MB");
        }).ToList();

        _visibleRows = _allRows;
        AppsListBox.ItemsSource = _visibleRows;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                var target = AppsListBox.SelectedItem as AppRowItem ?? _visibleRows.FirstOrDefault();
                if (target is not null)
                {
                    SelectedProcessName = target.Name;
                    DialogResult = true;
                    e.Handled = true;
                }
            }
        };
        Loaded += (_, _) =>
        {
            ClampPositionNearOwner();
            FilterBox.Focus();
        };
    }

    private void ClampPositionNearOwner()
    {
        var workArea = SystemParameters.WorkArea;
        if (Owner is { ActualWidth: > 0, ActualHeight: > 0 } owner)
        {
            Left = owner.Left + (owner.ActualWidth - Width) / 2.0;
            Top = owner.Top + (owner.ActualHeight - Height) / 2.0;
        }
        else
        {
            Left = workArea.Right - Width - 12;
            Top = workArea.Bottom - Height - 12;
        }

        Left = Math.Clamp(Left, workArea.Left + 12, Math.Max(workArea.Left + 12, workArea.Right - Width - 12));
        Top = Math.Clamp(Top, workArea.Top + 12, Math.Max(workArea.Top + 12, workArea.Bottom - Height - 12));
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = (FilterBox.Text ?? string.Empty).Trim();
        _visibleRows = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        AppsListBox.ItemsSource = _visibleRows;
    }

    private void AppItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: AppRowItem item })
        {
            SelectedProcessName = item.Name;
            DialogResult = true;
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
