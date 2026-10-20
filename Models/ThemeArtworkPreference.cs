namespace PowerHub.Models;

public sealed class ThemeArtworkPreference
{
    public bool Enabled { get; set; } = true;
    public string? Path { get; set; }
    public double Opacity { get; set; } = .45;
    public string Position { get; set; } = "Right";
}
