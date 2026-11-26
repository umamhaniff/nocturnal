using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nocturnal.Core;

namespace Nocturnal.App.UI;

public partial class AppPickerWindow : Window
{
    private sealed record AppRowItem(string Name, string Subtitle, string RamLabel);

    private readonly List<AppRowItem> _allRows;

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

        AppsListBox.ItemsSource = _allRows;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = (FilterBox.Text ?? string.Empty).Trim();
        AppsListBox.ItemsSource = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void AppsListBox_MouseClick(object sender, MouseButtonEventArgs e)
    {
        if (AppsListBox.SelectedItem is AppRowItem item)
        {
            SelectedProcessName = item.Name;
            DialogResult = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
