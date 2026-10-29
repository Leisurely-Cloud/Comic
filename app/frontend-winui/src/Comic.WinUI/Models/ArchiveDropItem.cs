namespace Comic.WinUI.Models;

/// <summary>Metadata from an explicitly dropped storage item; resolving it does not open or authorize the file.</summary>
public sealed record ArchiveDropItem(string FilePath, bool IsFile);
