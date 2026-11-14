using System.Windows;
using System.Windows.Controls;
using GeoVisionPro.Core.Spatial;
using Microsoft.Win32;

namespace GeoVisionPro.Desktop.Services;

/// <summary>File dialogs, confirmations and simple prompts.</summary>
public sealed class DialogService
{
    public const string SegyFilter = "SEG-Y files (*.sgy;*.segy;*.seg)|*.sgy;*.segy;*.seg|All files (*.*)|*.*";
    public const string LasFilter = "LAS files (*.las)|*.las|All files (*.*)|*.*";
    public const string TextFilter = "Text/CSV files (*.txt;*.csv;*.dat;*.xyz;*.asc)|*.txt;*.csv;*.dat;*.xyz;*.asc|All files (*.*)|*.*";
    public const string ProjectFilter = "GeoVision Pro project (*.gvp)|*.gvp";

    public string? LastFolder { get; set; }

    public string[] OpenFiles(string title, string filter, bool multi = true)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, Multiselect = multi, InitialDirectory = LastFolder ?? "" };
        if (d.ShowDialog() != true) return [];
        LastFolder = System.IO.Path.GetDirectoryName(d.FileNames[0]);
        return d.FileNames;
    }

    public string? OpenFile(string title, string filter) => OpenFiles(title, filter, false).FirstOrDefault();

    public string? SaveFile(string title, string filter, string defaultName)
    {
        var d = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, InitialDirectory = LastFolder ?? "" };
        if (d.ShowDialog() != true) return null;
        LastFolder = System.IO.Path.GetDirectoryName(d.FileName);
        return d.FileName;
    }

    public string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog() == true ? d.FolderName : null;
    }

    public bool Confirm(string message, string title = "GeoVision Pro") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool? YesNoCancel(string message, string title = "GeoVision Pro") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true, MessageBoxResult.No => false, _ => null
        };

    public void Info(string message, string title = "GeoVision Pro") => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public void Error(string message, string title = "GeoVision Pro") => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>Single-line text prompt (built in code to avoid an extra XAML file).</summary>
    public string? Prompt(string message, string title, string defaultValue = "")
    {
        var win = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current.MainWindow, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        win.SetResourceReference(Control.BackgroundProperty, "SecondaryBackgroundBrush");
        win.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var box = new TextBox { Text = defaultValue, Margin = new Thickness(10, 4, 10, 4) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        ok.Click += (_, _) => win.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(10, 10, 10, 2), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        win.Content = panel;
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}

public enum AppTheme { Dark, Light }

/// <summary>Swaps the colour dictionary at runtime; controls use DynamicResource so the change is immediate.</summary>
public sealed class ThemeService
{
    public AppTheme Current { get; private set; } = AppTheme.Dark;
    public event EventHandler<AppTheme>? ThemeChanged;

    public void Apply(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        var dicts = app.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Dark.xaml") == true || d.Source?.OriginalString.Contains("Themes/Light.xaml") == true);
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/GeoVisionPro;component/Themes/{theme}.xaml", UriKind.Absolute) };
        if (existing is not null) dicts[dicts.IndexOf(existing)] = next; else dicts.Insert(0, next);
        Current = theme;
        ThemeChanged?.Invoke(this, theme);
    }
}

/// <summary>Linked cursor across viewers: publishes the map location and time under the mouse.</summary>
public sealed class CursorBus
{
    public event EventHandler<(Point2D Map, double Time, object Source)>? Moved;
    public (Point2D Map, double Time)? Last { get; private set; }
    public void Publish(Point2D map, double time, object source)
    {
        Last = (map, time);
        Moved?.Invoke(this, (map, time, source));
    }
}
