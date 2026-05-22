using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.Views;

public partial class RecoveryDialog : Window
{
    private readonly ObservableCollection<RecoveryItem> _items;
    private readonly Action<string>                      _log;

    public RecoveryDialog(
        IReadOnlyList<RecoveryCandidate> candidates,
        Action<string> log,
        Window? owner = null)
    {
        InitializeComponent();

        if (owner != null) Owner = owner;

        _log = log;
        _items = new ObservableCollection<RecoveryItem>(
            candidates.Select(c => new RecoveryItem(c)));

        CandidateList.ItemsSource = _items;
        UpdateSummary();

        // Apply owner's resource dictionary for theming
        if (owner != null)
        {
            Resources.MergedDictionaries.Clear();
            foreach (var dict in owner.Resources.MergedDictionaries)
                Resources.MergedDictionaries.Add(dict);
        }
    }

    // ── Per-row actions ───────────────────────────────────────────────────────

    private void ResumeBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RecoveryItem item) return;
        var target = item.SelectedSourceFile ?? item.Candidate.PossibleSourceFiles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show("Select a target file to apply the tags to.", "Resume",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExecuteResume(item, target);
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RecoveryItem item) return;
        ExecuteDelete(item);
    }

    // ── Bulk actions ──────────────────────────────────────────────────────────

    private void ResumeAllBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items.ToList())
        {
            var target = item.SelectedSourceFile ?? item.Candidate.PossibleSourceFiles.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(target))
                ExecuteResume(item, target);
        }
    }

    private void DeleteAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            $"Delete all {_items.Count} orphan file(s)?\nThis cannot be undone.",
            "Delete All", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var item in _items.ToList())
            ExecuteDelete(item);
    }

    private void DismissBtn_Click(object sender, RoutedEventArgs e) => Close();

    // ── Core operations ───────────────────────────────────────────────────────

    private void ExecuteResume(RecoveryItem item, string targetPath)
    {
        var (ok, err) = RecoveryService.Resume(item.Candidate, targetPath);
        if (ok)
        {
            _log($"[Recovery] Resumed: {Path.GetFileName(targetPath)} ← {item.Candidate.FileName}");
            _items.Remove(item);
            UpdateSummary();
        }
        else
        {
            _log($"[Recovery] Resume failed for {item.Candidate.FileName}: {err}");
            MessageBox.Show($"Resume failed:\n{err}", "Recovery Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        if (_items.Count == 0) Close();
    }

    private void ExecuteDelete(RecoveryItem item)
    {
        var (ok, err) = RecoveryService.Delete(item.Candidate);
        if (ok)
        {
            _log($"[Recovery] Deleted orphan: {item.Candidate.FileName}");
            _items.Remove(item);
            UpdateSummary();
        }
        else
        {
            _log($"[Recovery] Delete failed for {item.Candidate.FileName}: {err}");
            MessageBox.Show($"Delete failed:\n{err}", "Recovery Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        if (_items.Count == 0) Close();
    }

    private void UpdateSummary()
    {
        int temps    = _items.Count(i => i.Candidate.TempFileType == TempFileType.WriteTemp);
        int backups  = _items.Count(i => i.Candidate.TempFileType == TempFileType.WriteBackup);
        var parts    = new List<string>();
        if (temps   > 0) parts.Add($"{temps} incomplete write{(temps   > 1 ? "s" : "")}");
        if (backups > 0) parts.Add($"{backups} stale backup{(backups > 1 ? "s" : "")}");
        SummaryText.Text = parts.Any() ? string.Join(", ", parts) : "All resolved.";

        // Hide Resume All if only backups remain (no resumes needed)
        ResumeAllBtn.Visibility = temps > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

// ── View model for each row ──────────────────────────────────────────────────

public class RecoveryItem : INotifyPropertyChanged
{
    public RecoveryCandidate Candidate { get; }

    private string? _selectedSourceFile;
    public string? SelectedSourceFile
    {
        get => _selectedSourceFile;
        set { _selectedSourceFile = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanResume)); }
    }

    public RecoveryItem(RecoveryCandidate candidate)
    {
        Candidate = candidate;
        // Pre-select the first source file if only one exists
        _selectedSourceFile = candidate.PossibleSourceFiles.FirstOrDefault();
    }

    // Presentation properties
    public string   FileName             => Candidate.FileName;
    public string   Description          => Candidate.Description;
    public string   FileSizeDisplay      => Candidate.FileSizeDisplay;
    public string   EmbeddedTitle        => Candidate.EmbeddedTitle;
    public bool     HasTitle             => !string.IsNullOrWhiteSpace(Candidate.EmbeddedTitle);
    public string   TempFilePath         => Candidate.TempFilePath;
    public IReadOnlyList<string> PossibleSourceFiles => Candidate.PossibleSourceFiles;

    public string TypeBadge => Candidate.TempFileType switch
    {
        TempFileType.WriteTemp    => "INCOMPLETE WRITE",
        TempFileType.WriteBackup  => "STALE BACKUP",
        _ => "ORPHAN"
    };
    public string TypeBadgeColor => Candidate.TempFileType switch
    {
        TempFileType.WriteTemp    => "#E67E22",
        TempFileType.WriteBackup  => "#3498DB",
        _ => "#95A5A6"
    };

    // Whether user needs to pick a source (multiple candidates)
    public bool NeedsSourceSelection =>
        Candidate.TempFileType == TempFileType.WriteTemp
        && Candidate.PossibleSourceFiles.Count > 1;

    // Can auto-resume (single source match)
    public bool CanAutoResume => Candidate.CanAutoResolveResume;

    // Can resume with current selection
    public bool CanResume =>
        Candidate.TempFileType == TempFileType.WriteTemp
        && !string.IsNullOrWhiteSpace(SelectedSourceFile);

    public string AutoResumeTarget => Candidate.PossibleSourceFiles.Count == 1
        ? $"→ {Path.GetFileName(Candidate.PossibleSourceFiles[0])}"
        : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
