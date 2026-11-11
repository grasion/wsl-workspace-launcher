using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkspaceLauncher.Core;

public sealed class ProfileStore
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkspaceLauncher");

    public static string FilePath { get; } = Path.Combine(DataDir, "profiles.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public AppData Data { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Data = JsonSerializer.Deserialize<AppData>(json, Options) ?? new AppData();
            }
        }
        catch (Exception ex)
        {
            // 손상된 파일은 백업해 두고 새로 시작
            try { File.Copy(FilePath, FilePath + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            Data = new AppData();
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Options));
        File.Move(tmp, FilePath, true);
    }
}
