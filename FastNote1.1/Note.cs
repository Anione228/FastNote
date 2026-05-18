using System;
using System.ComponentModel.DataAnnotations;

public class Note
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string Title { get; set; }
    public string Content { get; set; }
    public DateTime CreatedAt { get; set; }

    // НОВОЕ ПОЛЕ: Время напоминания (знак ? означает, что поле может быть null)
    public DateTime? ReminderAt { get; set; }

    public string MediaType { get; set; } = "text";

    // Уникальный ID файла на серверах Telegram
    public string? TelegramFileId { get; set; }

    // Длительность аудио/видео в секундах (для ГС и кружков)
    public int? Duration { get; set; }
}

public class UserSetting
{
    [Key]
    public long UserId { get; set; }
    public string TitleType { get; set; } = "auto"; // "auto" (первая строка) или "date" (дата)
    public bool IsTranscriptionEnabled { get; set; } = true; // По умолчанию включено
}
public class BotSettings
{
    public string Token { get; set; } = string.Empty;
    public string WebAppUrl { get; set; } = string.Empty;
    public string GroqApiKey { get; set; } = string.Empty;
}