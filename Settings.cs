using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BareRecord;

/// <summary>
/// User-visible settings, persisted as JSON in
/// <c>%LOCALAPPDATA%\BareRecord\settings.json</c>. Best-effort: any I/O
/// failure falls back to defaults rather than crashing the app.
/// </summary>
internal sealed class Settings
{
    public string Source        { get; set; } = "Primary";   // Primary | Window
    public bool   AudioSystem   { get; set; } = true;
    public bool   AudioMic      { get; set; } = false;
    public string OutputFolder  { get; set; } = DefaultOutputFolder();
    public int    Fps           { get; set; } = 30;
    public bool   ShowCursor    { get; set; } = true;
    public bool   Countdown     { get; set; } = false;

    /// <summary>Auto-stop after N seconds; 0 disables the timer.</summary>
    public int    AutoStopSeconds { get; set; } = 0;

    /// <summary>
    /// Filename template. Tokens: {yyyy} {MM} {dd} {HH} {mm} {ss} {source} {counter}.
    /// The extension is appended automatically.
    /// </summary>
    public string FilenameTemplate { get; set; } = "BareRecord-{yyyy}{MM}{dd}-{HHmmss}";

    public int    Counter { get; set; } = 0;

    /// <summary>Hide window to tray instead of taskbar while recording.</summary>
    public bool   MinimizeToTrayWhileRecording { get; set; } = true;

    /// <summary>Show "Recording started" / "Saved to …" balloon notifications.</summary>
    public bool   Notifications { get; set; } = true;

    private static string DefaultOutputFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "BareRecord");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BareRecord", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Settings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings) ?? new Settings();
        }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings);
            File.WriteAllText(FilePath, json);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Expand <see cref="FilenameTemplate"/> against a timestamp and source label.
    /// Falls back to a default template if the user wiped the field. Strips
    /// characters that are illegal in Windows filenames after substitution.
    /// Always increments and persists <see cref="Counter"/>.
    /// </summary>
    public string BuildFileName(DateTime when, string source)
    {
        var tpl = string.IsNullOrWhiteSpace(FilenameTemplate)
            ? "BareRecord-{yyyy}{MM}{dd}-{HHmmss}"
            : FilenameTemplate;

        Counter++;
        var sb = new StringBuilder(tpl.Length + 16);
        int i = 0;
        while (i < tpl.Length)
        {
            if (tpl[i] == '{')
            {
                int end = tpl.IndexOf('}', i + 1);
                if (end > i)
                {
                    var token = tpl.Substring(i + 1, end - i - 1);
                    sb.Append(ExpandToken(token, when, source));
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(tpl[i++]);
        }

        return Sanitize(sb.ToString());
    }

    private string ExpandToken(string token, DateTime when, string source) => token switch
    {
        "yyyy"   => when.ToString("yyyy", CultureInfo.InvariantCulture),
        "MM"     => when.ToString("MM",   CultureInfo.InvariantCulture),
        "dd"     => when.ToString("dd",   CultureInfo.InvariantCulture),
        "HH"     => when.ToString("HH",   CultureInfo.InvariantCulture),
        "mm"     => when.ToString("mm",   CultureInfo.InvariantCulture),
        "ss"     => when.ToString("ss",   CultureInfo.InvariantCulture),
        "HHmmss" => when.ToString("HHmmss", CultureInfo.InvariantCulture),
        "source" => source,
        "counter"=> Counter.ToString("000", CultureInfo.InvariantCulture),
        _        => "{" + token + "}",
    };

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}

[JsonSerializable(typeof(Settings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SettingsJsonContext : JsonSerializerContext { }
