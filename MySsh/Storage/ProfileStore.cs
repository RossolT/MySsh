using System.Text.Json;
using System.Text.Json.Serialization;

namespace MySsh.Storage;

/// <summary>Хранилище профилей в %AppData%\MySsh\profiles.json.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public ProfileStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MySsh");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "profiles.json");
    }

    public List<ConnectionProfile> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new List<ConnectionProfile>();

            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ConnectionProfile>>(json, JsonOptions)
                   ?? new List<ConnectionProfile>();
        }
        catch (IOException)
        {
            return new List<ConnectionProfile>();
        }
        catch (JsonException)
        {
            return new List<ConnectionProfile>();
        }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        string json = JsonSerializer.Serialize(profiles.ToList(), JsonOptions);

        // Пишем через временный файл, чтобы не потерять данные при сбое.
        string tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _filePath, overwrite: true);
    }
}
