namespace ACGTranslator.Models;
public sealed class SubtitleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // Groups the two subtitles of one verified turn for the overlay, not identity.
    public Guid? TurnGroupId { get; set; }
    public Guid SessionId { get; set; }
    public string SessionName { get; set; } = "旧版记录";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string SourceLanguage { get; set; } = "";
    public string SourceText { get; set; } = "";
    public string ChineseText { get; set; } = "";
    public string Status { get; set; } = "完成";
}
