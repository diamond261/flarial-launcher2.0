using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Flarial.Launcher.Pages;

public partial class SettingsVersionPage : Page
{
    const string SaveFailureMessage = "Failed to save custom DLL settings.";

    sealed class DllEntryRow
    {
        public string Path { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }

    readonly Settings _settings = Settings.Current;
    readonly ObservableCollection<DllEntryRow> _items = [];

    static DllEntryRow[] ParsePaths(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().Trim('"'))
            .Where(item => item.Length > 0)
            .Select(item =>
            {
                var index = item.IndexOf('|');
                if (index <= 0)
                    return new DllEntryRow { Path = item, Enabled = true };

                var flag = item.Substring(0, index).Trim();
                var path = item.Substring(index + 1).Trim();

                return new DllEntryRow
                {
                    Path = path,
                    Enabled = flag != "0"
                };
            })
            .Where(row => row.Path.Length > 0)
            .ToArray();
    }

    void Persist(bool save = true)
    {
        _settings.CustomDllPath = string.Join(";", _items.Select(item => $"{(item.Enabled ? "1" : "0")}|{item.Path}"));

        if (save)
            _settings.TrySave(SaveFailureMessage);

        DllListBox.ItemsSource = null;
        DllListBox.ItemsSource = _items;
    }

    public SettingsVersionPage()
    {
        InitializeComponent();

        foreach (var entry in ParsePaths(_settings.CustomDllPath ?? string.Empty))
            _items.Add(entry);

        Persist(false);
    }

    void EntryEnabledChanged(object sender, RoutedEventArgs e) => Persist();

    void Add_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            InitialDirectory = @"C:\",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = true,
            DefaultExt = "dll",
            Filter = "DLL Files|*.dll;",
            Title = "Select Custom DLL(s)"
        };

        if (dialog.ShowDialog() is not true)
            return;

        foreach (var file in dialog.FileNames)
            if (!_items.Any(item => item.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
                _items.Add(new DllEntryRow { Path = file, Enabled = true });

        Persist();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (DllListBox.SelectedItem is not DllEntryRow selected)
            return;

        _items.Remove(selected);
        Persist();
    }

    void Up_Click(object sender, RoutedEventArgs e)
    {
        if (DllListBox.SelectedItem is not DllEntryRow selected)
            return;

        var index = _items.IndexOf(selected);
        if (index <= 0)
            return;

        _items.Move(index, index - 1);
        Persist();
        DllListBox.SelectedItem = selected;
    }

    void Down_Click(object sender, RoutedEventArgs e)
    {
        if (DllListBox.SelectedItem is not DllEntryRow selected)
            return;

        var index = _items.IndexOf(selected);
        if (index < 0 || index >= _items.Count - 1)
            return;

        _items.Move(index, index + 1);
        Persist();
        DllListBox.SelectedItem = selected;
    }
}
