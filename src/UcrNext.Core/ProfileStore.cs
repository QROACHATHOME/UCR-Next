using System.Text.Json;

namespace UcrNext.Core;

/// <summary>Saves each profile as one JSON file named after its Id.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Directory { get; }

    public ProfileStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    /// <summary>Default location: %APPDATA%\UCR-Next\profiles.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCR-Next", "profiles");

    public List<Profile> LoadAll()
    {
        var list = new List<Profile>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file), Options);
                if (p is not null) list.Add(p);
            }
            catch (JsonException)
            {
                // Skip a corrupt file instead of failing the whole load.
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Save(Profile profile)
    {
        var path = PathFor(profile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profile, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(Profile profile)
    {
        var path = PathFor(profile);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(Profile p) => Path.Combine(Directory, p.Id.ToString("N") + ".json");
}
