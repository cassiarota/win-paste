namespace PasteNowWin.Models;

/// <summary>A user-created list that history items can be filed into.</summary>
public sealed class ClipList
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
