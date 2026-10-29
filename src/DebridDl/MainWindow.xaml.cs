using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace DebridDl;

public partial class MainWindow : Window
{
    const string Repo = "https://github.com/StyuDEV/debrid-dl";

    readonly Settings settings = Settings.Load();
    readonly ObservableCollection<Download> items = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly UpdateManager updates = new(new GithubSource(Repo, null, false));
    UpdateInfo? update;
    HashSet<string> hosts = [];
    bool closing;
    long ticks;

    public MainWindow()
    {
        InitializeComponent();

        Debrid.ApiKey = settings.ApiKey;
        KeyBox.Text = settings.ApiKey;
        ConnectionsBox.Text = settings.Connections.ToString();
        ParallelBox.Text = settings.Parallel.ToString();
        ClipboardBox.IsChecked = settings.WatchClipboard;
        FolderBox.Text = settings.Folder;
        SettingsPanel.Visibility = settings.ApiKey == "" ? Visibility.Visible : Visibility.Collapsed;
        VersionText.Text = updates.IsInstalled ? $"v{updates.CurrentVersion}" : "dev";

        foreach (var q in settings.Queue)
        {
            items.Add(new Download(q.Link, q.Folder, q.FileName, q.Size, q.State));
        }
        List.ItemsSource = items;

        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
        Loaded += async (_, _) => await Task.WhenAll(CheckAccount(), CheckUpdates());
    }

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\.(mkv|mp4|avi|zip|rar|7z|iso|exe|msi|tar|gz|pdf|r\d\d|part\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FilePattern();

    [DllImport("user32.dll")]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle).AddHook(Hook);
        AddClipboardFormatListener(handle);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (closing)
        {
            return;
        }

        e.Cancel = true;
        closing = true;
        await Shutdown();
        if (update is not null)
        {
            updates.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
        }
        Close();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x031D && settings.WatchClipboard)
        {
            Grab();
        }
        return IntPtr.Zero;
    }

    void Tick()
    {
        var free = settings.Parallel - items.Count(d => d.IsActive);
        foreach (var d in items.Where(d => d.State == State.Queued).Take(Math.Max(0, free)).ToList())
        {
            d.Start(settings.Connections);
        }
        foreach (var d in items)
        {
            d.Refresh();
        }

        var speed = items.Sum(d => d.Speed);
        var running = items.Count(d => d.IsActive);
        var finished = items.Count(d => d.State == State.Done);
        TotalText.Text = $"{Download.Bytes(speed)}/s · {running} en cours · {finished}/{items.Count} terminés";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (++ticks % 6 == 0)
        {
            Persist();
        }
        if (ticks % 43200 == 0 && update is null)
        {
            _ = CheckUpdates();
        }
    }

    void Persist()
    {
        foreach (var d in items.Where(d => d.IsActive))
        {
            d.SaveState();
        }
        settings.Queue = [.. items.Select(d => d.ToQueued())];
        try
        {
            settings.Save();
        }
        catch (IOException)
        {
        }
    }

    async Task Shutdown()
    {
        timer.Stop();
        var running = items.Where(d => d.IsActive).ToList();
        foreach (var d in running)
        {
            d.Pause();
        }
        await Task.WhenAny(Task.WhenAll(running.Select(d => d.Stopped)), Task.Delay(5000));
        foreach (var d in running)
        {
            d.Requeue();
        }
        Persist();
    }

    async Task CheckAccount()
    {
        if (settings.ApiKey == "")
        {
            AccountText.Text = "Clé AllDebrid manquante";
            return;
        }

        try
        {
            AccountText.Text = await Debrid.Account();
            hosts = await Debrid.Hosts();
        }
        catch (Exception e) when (e is DebridException or HttpRequestException or TaskCanceledException)
        {
            AccountText.Text = e.Message;
        }
    }

    async Task CheckUpdates()
    {
        if (!updates.IsInstalled)
        {
            return;
        }

        try
        {
            var found = await updates.CheckForUpdatesAsync();
            if (found is null)
            {
                return;
            }
            await updates.DownloadUpdatesAsync(found);
            update = found;
            UpdateButton.Content = $"Mettre à jour (v{found.TargetFullRelease.Version})";
            UpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // An update check must never take the downloads down with it.
        }
    }

    void Grab()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (ExternalException)
        {
            return;
        }

        var fresh = Links(text)
            .Where(IsDownload)
            .Where(l => !LinksBox.Text.Contains(l) && items.All(d => d.Link != l))
            .ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var separator = LinksBox.Text is "" || LinksBox.Text.EndsWith('\n') ? "" : "\n";
        LinksBox.AppendText(separator + string.Join("\n", fresh) + "\n");
    }

    bool IsDownload(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        var known = hosts.Any(h => host == h || host.EndsWith("." + h));
        var debrid = host.EndsWith("debrid.it") || host.EndsWith("alldebrid.com") && uri.AbsolutePath.StartsWith("/f/");
        return known || debrid || FilePattern().IsMatch(uri.AbsolutePath);
    }

    static IEnumerable<string> Links(string text) =>
        LinkPattern().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', ';')).Distinct();

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (folder == "")
        {
            return;
        }

        foreach (var link in Links(LinksBox.Text).Where(l => items.All(d => d.Link != l)))
        {
            items.Add(new Download(link, folder));
        }
        LinksBox.Clear();
        settings.Folder = folder;
        Tick();
        Persist();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = FolderBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            settings.Folder = dialog.FolderName;
            Persist();
        }
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var d = (Download)((FrameworkElement)sender).DataContext;
        if (d.IsActive || d.State == State.Queued)
        {
            d.Pause();
        }
        else
        {
            d.Requeue();
        }
        d.Refresh();
        Persist();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var d = (Download)((FrameworkElement)sender).DataContext;
        if (d.Path is not null && File.Exists(d.Path))
        {
            Process.Start("explorer.exe", $"/select,\"{d.Path}\"");
        }
        else if (Directory.Exists(d.Folder))
        {
            Process.Start("explorer.exe", $"\"{d.Folder}\"");
        }
    }

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var d = (Download)((FrameworkElement)sender).DataContext;
        var delete = false;
        if (d.State != State.Done && d.Path is not null)
        {
            var answer = MessageBox.Show(this, "Supprimer aussi le fichier partiel ?", "Retirer",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }
            delete = answer == MessageBoxResult.Yes;
        }

        d.Pause();
        items.Remove(d);
        await d.Stopped;
        if (delete)
        {
            d.DeletePartial();
        }
        Persist();
    }

    void ResumeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in items)
        {
            d.Requeue();
        }
        Tick();
    }

    void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in items)
        {
            d.Pause();
        }
        Tick();
    }

    void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in items.Where(d => d.State == State.Done).ToList())
        {
            items.Remove(d);
        }
        Persist();
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (update is null)
        {
            return;
        }

        UpdateButton.IsEnabled = false;
        closing = true;
        await Shutdown();
        updates.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }

    void Settings_Click(object sender, RoutedEventArgs e) =>
        SettingsPanel.Visibility = SettingsPanel.IsVisible ? Visibility.Collapsed : Visibility.Visible;

    void Key_LostFocus(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text.Trim();
        if (key == settings.ApiKey)
        {
            return;
        }

        settings.ApiKey = key;
        Debrid.ApiKey = key;
        Persist();
        _ = CheckAccount();
    }

    void Numbers_LostFocus(object sender, RoutedEventArgs e)
    {
        settings.Connections = int.TryParse(ConnectionsBox.Text, out var c) ? Math.Clamp(c, 1, 64) : settings.Connections;
        settings.Parallel = int.TryParse(ParallelBox.Text, out var p) ? Math.Clamp(p, 1, 10) : settings.Parallel;
        ConnectionsBox.Text = settings.Connections.ToString();
        ParallelBox.Text = settings.Parallel.ToString();
        Persist();
    }

    void Clipboard_Click(object sender, RoutedEventArgs e)
    {
        settings.WatchClipboard = ClipboardBox.IsChecked == true;
        Persist();
    }

    void Links_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        LinksHint.Visibility = LinksBox.Text == "" ? Visibility.Visible : Visibility.Collapsed;

    void Link_Navigate(object sender, RequestNavigateEventArgs e) =>
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
}
