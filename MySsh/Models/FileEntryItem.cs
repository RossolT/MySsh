namespace MySsh.Models;

/// <summary>Элемент локальной или удалённой файловой системы.</summary>
public sealed class FileEntryItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required bool IsParentLink { get; init; }
    public long Size { get; init; }
    public DateTime? LastWriteTime { get; init; }

    public string TypeText => IsParentLink
        ? "Папка"
        : (IsDirectory ? "Папка" : "Файл");

    public string SizeText => (IsDirectory || IsParentLink)
        ? ""
        : FormatSize(Size);

    public string ModifiedText => LastWriteTime.HasValue
        ? LastWriteTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.0} КБ";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.0} МБ";
        double gb = mb / 1024.0;
        return $"{gb:0.00} ГБ";
    }
}