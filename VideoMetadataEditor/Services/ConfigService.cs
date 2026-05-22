using System.IO;
using Newtonsoft.Json;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

public class ConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        // Clean up any stale .tmp file left by a previous crash mid-save
        var tmpPath = ConfigPath + ".tmp";
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("config.json could not be parsed — using defaults.");
            Settings = new AppSettings();
        }
    }

    /// <summary>
    /// Saves settings asynchronously. Writes to a .tmp file first, then
    /// atomically replaces config.json. Handles the case where config.json
    /// does not yet exist (fresh folder — uses File.Move instead of File.Replace).
    /// </summary>
    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json    = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            var tmpPath = ConfigPath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);
            AtomicReplace(tmpPath, ConfigPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Config save error: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Synchronous save for use in non-async contexts (e.g. OnUnloaded).</summary>
    public void Save()
    {
        _saveLock.Wait();
        try
        {
            var json    = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            var tmpPath = ConfigPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            AtomicReplace(tmpPath, ConfigPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Config save error: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="source"/> atomically.
    /// Uses File.Replace when the destination exists (NTFS atomic), or File.Move
    /// when it does not (first save in a fresh folder).
    /// </summary>
    private static void AtomicReplace(string source, string destination)
    {
        if (File.Exists(destination))
            File.Replace(source, destination, null);
        else
            File.Move(source, destination);
    }
}
