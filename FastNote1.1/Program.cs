using FastNote1._1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);
var botSettings = builder.Configuration.GetSection("BotSettings").Get<BotSettings>();

// 2. Регистрируем настройки в DI-контейнере (чтобы их мог прочитать BotWorker)
builder.Services.AddSingleton(botSettings);

builder.Services.AddHostedService<BotWorker>();

var app = builder.Build();

// Автоматически создаем файл базы данных при старте, если его еще нет
using (var scope = app.Services.CreateScope())
{
    using var db = new AppDbContext();
    db.Database.EnsureCreated();
}

app.UseFileServer();

// ЭНДПОИНТ ПОЛУЧЕНИЯ ЗАМЕТОК (ИСПРАВЛЕННЫЙ)
app.MapGet("/api/notes/{userId}", async (long userId) =>
{
    using var db = new AppDbContext();

    // ФИКС: Обязательно сортируем по убыванию даты (OrderByDescending),
    // чтобы новые и поднятые точкой (.) заметки были в самом верху списка!
    var notes = await db.Notes
        .Where(n => n.UserId == userId)
        .OrderByDescending(n => n.CreatedAt)
        .ToListAsync();

    // SQLite стирает информацию о таймзоне. Явно говорим C#, что это UTC.
    foreach (var note in notes)
    {
        if (note.ReminderAt.HasValue)
        {
            note.ReminderAt = DateTime.SpecifyKind(note.ReminderAt.Value, DateTimeKind.Utc);
        }
    }
    return Results.Ok(notes);
});

// Изменение заметки
app.MapPut("/api/notes/{id}", async (int id, Note updatedNote) =>
{
    using var db = new AppDbContext();
    var note = await db.Notes.FindAsync(id);
    if (note == null) return Results.NotFound();

    note.Content = updatedNote.Content;
    note.Title = updatedNote.Title;
    note.ReminderAt = updatedNote.ReminderAt;

    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ПОЛУЧЕНИЕ НАСТРОЕК
app.MapGet("/api/settings/{userId}", async (long userId) =>
{
    using var db = new AppDbContext();
    var setting = await db.UserSettings.FindAsync(userId);

    // Возвращаем объект, где IsTranscriptionEnabled - это булево значение (true/false)
    return Results.Ok(new
    {
        titleType = setting?.TitleType ?? "auto",
        isTranscriptionEnabled = setting?.IsTranscriptionEnabled ?? true
    });
});

// СОХРАНЕНИЕ НАСТРОЕК
app.MapPost("/api/settings", async (UserSetting setting) =>
{
    using var db = new AppDbContext();
    var existing = await db.UserSettings.FindAsync(setting.UserId);

    if (existing == null)
    {
        db.UserSettings.Add(setting);
    }
    else
    {
        existing.TitleType = setting.TitleType;
        existing.IsTranscriptionEnabled = setting.IsTranscriptionEnabled; // ВОТ ЭТО БЫЛО ПРОПУЩЕНО?
    }
    await db.SaveChangesAsync();
    return Results.Ok();
});
// СОЗДАНИЕ НОВОЙ ЗАМЕТКИ
app.MapPost("/api/notes", async (Note note) =>
{
    using var db = new AppDbContext();
    note.CreatedAt = DateTime.UtcNow.AddHours(3); ; // Ставим время сервера
    db.Notes.Add(note);
    await db.SaveChangesAsync();
    return Results.Ok(note);
});
// Массовое удаление всех заметок пользователя
app.MapDelete("/api/notes/user/{userId}", async (long userId) =>
{
    using var db = new AppDbContext();

    // Получаем все заметки этого юзера одним запросом
    var userNotes = db.Notes.Where(n => n.UserId == userId);

    // Удаляем их все сразу
    db.Notes.RemoveRange(userNotes);

    // Один раз сохраняем изменения
    await db.SaveChangesAsync();

    return Results.Ok();
});
// Метод для удаления заметки
app.MapDelete("/api/notes/{id}", async (int id) =>
{
    using var db = new AppDbContext();
    var note = await db.Notes.FindAsync(id);
    if (note == null) return Results.NotFound();

    db.Notes.Remove(note);
    await db.SaveChangesAsync();
    return Results.Ok();
});
// Эндпоинт для получения прямой ссылки на медиафайл из Телеграма
app.MapGet("/api/notes/media/{fileId}", async (string fileId, BotSettings settings) =>
{
    try
    {
        var botClient = new Telegram.Bot.TelegramBotClient(settings.Token);
        var fileInfo = await botClient.GetFile(fileId);
        string directUrl = $"https://api.telegram.org/file/bot{settings.Token}/{fileInfo.FilePath}";

        return Results.Redirect(directUrl);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.Run();