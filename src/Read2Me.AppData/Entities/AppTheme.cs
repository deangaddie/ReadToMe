namespace Read2Me.AppData.Entities
{
    /// <summary>A theme row. The optional colours are null where the palette default applies.</summary>
    public class AppTheme
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; }
        public bool IsDark { get; set; }
        public string Primary { get; set; } = "#594AE2";
        public string Secondary { get; set; } = "#FF4081";
        public string? Background { get; set; }
        public string? Surface { get; set; }
        public string? AppbarBackground { get; set; }
        public string? DrawerBackground { get; set; }
        public string? TextPrimary { get; set; }
        public string? TextSecondary { get; set; }
    }
}
