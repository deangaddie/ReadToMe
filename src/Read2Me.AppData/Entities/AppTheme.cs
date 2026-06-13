namespace Read2Me.AppData.Entities
{
    public class AppTheme
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; }
        public bool IsDark { get; set; }
        public string Primary { get; set; } = "#594AE2";
        public string Secondary { get; set; } = "#FF4081";
    }
}
