using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// MainViewModel — External Tools section (Settings → External Tools).
///
/// Presents ffmpeg, mkvpropedit and fpcalc through a uniform row VM backed by
/// <see cref="ManagedToolsService"/>. Status/version detection is shared; real
/// in-app download is available where the underlying service supports it
/// (fpcalc today), otherwise the row offers "Open download page".
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<ToolRowViewModel> ExternalTools { get; } = new();

    private bool _toolsInitialised;

    /// <summary>Builds the tool rows once and kicks off an initial status refresh.</summary>
    public void InitExternalTools()
    {
        if (_toolsInitialised) return;
        _toolsInitialised = true;

        foreach (var tool in ManagedToolsService.Tools)
            ExternalTools.Add(new ToolRowViewModel(tool, this));

        RaiseProperty(nameof(NativeFolderExists));
        _ = RefreshAllToolsAsync();
    }

    public bool NativeFolderExists => ManagedToolsService.NativeFolderExists;

    private ICommand? _createNativeFolderCommand;
    public ICommand CreateNativeFolderCommand => _createNativeFolderCommand ??= new RelayCommand(_ =>
    {
        try
        {
            var path = ManagedToolsService.EnsureNativeFolder();
            StatusText = $"✓ native folder ready: {path}";
            RaiseProperty(nameof(NativeFolderExists));
            foreach (var r in ExternalTools) r.RefreshExistenceOnly();
        }
        catch (System.Exception ex)
        {
            StatusText = $"Could not create native folder: {ex.Message}";
        }
    });

    private ICommand? _openNativeFolderCommand;
    public ICommand OpenNativeFolderCommand => _openNativeFolderCommand ??= new RelayCommand(_ =>
    {
        try
        {
            ManagedToolsService.EnsureNativeFolder();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ManagedToolsService.NativeDir, UseShellExecute = true
            });
        }
        catch (System.Exception ex) { StatusText = $"Could not open native folder: {ex.Message}"; }
    });

    private ICommand? _checkAllToolsCommand;
    public ICommand CheckAllToolsCommand => _checkAllToolsCommand ??= new AsyncRelayCommand(
        () => RefreshAllToolsAsync());

    /// <summary>True when any tool reports an available update. Drives the cross-tab hint.</summary>
    private bool _anyToolUpdateAvailable;
    public bool AnyToolUpdateAvailable
    {
        get => _anyToolUpdateAvailable;
        private set => Set(ref _anyToolUpdateAvailable, value);
    }

    public async Task RefreshAllToolsAsync()
    {
        foreach (var r in ExternalTools)
            await r.RefreshAsync();
        RecomputeUpdateFlag();
    }

    internal void RecomputeUpdateFlag()
        => AnyToolUpdateAvailable = ExternalTools.Any(t => t.UpdateAvailable);
}

/// <summary>One row in the External Tools list.</summary>
public sealed class ToolRowViewModel : ViewModelBase
{
    private readonly ManagedTool _tool;
    private readonly MainViewModel _owner;

    public ToolRowViewModel(ManagedTool tool, MainViewModel owner)
    {
        _tool  = tool;
        _owner = owner;
    }

    public string DisplayName => _tool.DisplayName;
    public string Purpose     => _tool.Purpose;
    public bool   CanAutoDownload => _tool.CanAutoDownload;

    private bool _isInstalled;
    public bool IsInstalled { get => _isInstalled; private set => Set(ref _isInstalled, value); }

    private string _installedVersion = string.Empty;
    public string InstalledVersion { get => _installedVersion; private set => Set(ref _installedVersion, value); }

    private string _latestVersion = string.Empty;
    public string LatestVersion { get => _latestVersion; private set => Set(ref _latestVersion, value); }

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }

    private string _status = "Checking…";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    /// <summary>Label for the primary action button — Download when missing, Update when outdated.</summary>
    public string ActionLabel => IsInstalled ? "Check for Update" : "Download";

    /// <summary>Quick existence/version refresh without hitting the network.</summary>
    public void RefreshExistenceOnly()
    {
        IsInstalled = _tool.IsInstalled();
        RaiseProperty(nameof(ActionLabel));
    }

    public async Task RefreshAsync()
    {
        try
        {
            IsInstalled = _tool.IsInstalled();
            RaiseProperty(nameof(ActionLabel));

            InstalledVersion = IsInstalled
                ? (await _tool.GetInstalledVersionAsync() ?? "unknown") : string.Empty;

            // Only hit the network for a latest-version when the tool supports it.
            if (_tool.GetLatestVersionAsync != null)
            {
                var latest = await _tool.GetLatestVersionAsync(CancellationToken.None);
                LatestVersion = latest ?? string.Empty;

                if (_tool.IsUpdateAvailableAsync != null)
                {
                    // Tool supplies its own check (e.g. FFmpeg's non-semver BtbN builds).
                    UpdateAvailable = IsInstalled
                        && await _tool.IsUpdateAvailableAsync(CancellationToken.None);
                }
                else
                {
                    UpdateAvailable = IsInstalled
                        && !string.IsNullOrWhiteSpace(latest)
                        && !string.IsNullOrWhiteSpace(InstalledVersion)
                        && InstalledVersion != "unknown"
                        && IsNewer(latest!, InstalledVersion);
                }
            }
            else
            {
                LatestVersion = string.Empty;
                UpdateAvailable = false;
            }

            Status = !IsInstalled
                ? "Not installed"
                : UpdateAvailable
                    ? $"Update available → {LatestVersion}"
                    : "Up to date";
        }
        catch (System.Exception ex)
        {
            Status = $"Check failed: {ex.Message}";
        }
        _owner.RecomputeUpdateFlag();
    }

    private ICommand? _primaryActionCommand;
    public ICommand PrimaryActionCommand => _primaryActionCommand ??= new AsyncRelayCommand(
        DoPrimaryActionAsync, _ => !IsBusy);

    private async Task DoPrimaryActionAsync()
    {
        // Tools that can't self-install just open their download page.
        if (!_tool.CanAutoDownload || _tool.InstallOrUpdateAsync == null)
        {
            OpenDownloadPage();
            return;
        }

        IsBusy = true;
        Status = "Working…";
        try
        {
            var progress = new System.Progress<(int pct, string msg)>(p => Status = p.msg);
            var (ok, message) = await _tool.InstallOrUpdateAsync(progress, CancellationToken.None);
            Status = message;
            _owner.StatusText = $"{DisplayName}: {message}";
            await RefreshAsync();
        }
        catch (System.Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private ICommand? _openPageCommand;
    public ICommand OpenDownloadPageCommand => _openPageCommand ??= new RelayCommand(_ => OpenDownloadPage());

    private void OpenDownloadPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _tool.DownloadPageUrl, UseShellExecute = true
            });
        }
        catch (System.Exception ex) { _owner.StatusText = $"Couldn't open {DisplayName} page: {ex.Message}"; }
    }

    private ICommand? _redetectCommand;
    public ICommand RedetectCommand => _redetectCommand ??= new AsyncRelayCommand(async () =>
    {
        _tool.Redetect?.Invoke();
        await RefreshAsync();
    });

    public bool CanRedetect => _tool.Redetect != null;

    private static bool IsNewer(string latest, string installed)
    {
        if (System.Version.TryParse(latest.Trim('v', 'V'), out var l)
            && System.Version.TryParse(installed.Trim('v', 'V'), out var i))
            return l > i;
        // Fall back to a string compare when either isn't a clean version.
        return string.CompareOrdinal(latest, installed) > 0;
    }
}
