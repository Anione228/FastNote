using FastNote1._1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types;

var builder = WebApplication.CreateBuilder(args);

// Читаем секцию BotSettings из appsettings.json
var botSettings = builder.Configuration
    .GetSection("BotSettings")
    .Get<BotSettings>();

// Регистрируем настройки и фоновый сервис в DI-контейнере
builder.Services.AddSingleton(botSettings);
builder.Services.AddHostedService<BotWorker>();

var app = builder.Build();

// Создаём файл базы данных при первом запуске (SQLite)
using (var scope = app.Services.CreateScope())
using (var db = new AppDbContext())
    db.Database.EnsureCreated();

app.UseFileServer();

// GET /api/notes/{userId}
// Возвращает все заметки пользователя:
//   сначала закреплённые, затем по убыванию даты создания.
app.MapGet("/api/notes/{userId}", async (long userId) =>
{
    using var db = new AppDbContext();

var notes = await db.Notes
    .Where(n => n.UserId == userId)
    .OrderByDescending(n => n.isPinned)
    .OrderByDescending(n => n.CreatedAt)
    .ToListAsync();

// SQLite не хранит timezone — явно помечаем время как UTC
foreach (var note in notes)
    if (note.ReminderAt.HasValue)
        note.ReminderAt = DateTime.SpecifyKind(note.ReminderAt.Value, DateTimeKind.Utc);

return Results.Ok(notes);
});

// POST /api/notes
// Создаёт новую заметку, выставляя время по серверу (UTC+3).
app.MapPost("/api/notes", async (Note note) =>
{
    using var db = new AppDbContext();
note.CreatedAt = DateTime.UtcNow.AddHours(3);
db.Notes.Add(note);
await db.SaveChangesAsync();
return Results.Ok(note);
});

// PUT /api/notes/{id}
// Обновляет заголовок, текст и время напоминания заметки.
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

// POST /api/notes/pin/{id}
// Переключает флаг закрепления заметки (toggle).
app.MapPost("/api/notes/pin/{id}", async (int id) =>
{
    using var db = new AppDbContext();
    var note = await db.Notes.FindAsync(id);
    if (note == null) return Results.NotFound();

    note.isPinned = !note.isPinned;

    await db.SaveChangesAsync();
    return Results.Ok(new { isPinned = note.isPinned });
});

// DELETE /api/notes/{id}  — удаляет одну заметку по id
app.MapDelete("/api/notes/{id}", async (int id) =>
{
    using var db = new AppDbContext();
    var note = await db.Notes.FindAsync(id);
    if (note == null) return Results.NotFound();

    db.Notes.Remove(note);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// DELETE /api/notes/user/{userId}  — удаляет все заметки пользователя одним запросом
app.MapDelete("/api/notes/user/{userId}", async (long userId) =>
{
    using var db = new AppDbContext();
    var userNotes = db.Notes.Where(n => n.UserId == userId);
    db.Notes.RemoveRange(userNotes);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// GET /api/settings/{userId}
// Возвращает настройки пользователя (тип заголовка + транскрипция).
// Если записи нет — возвращает значения по умолчанию.
app.MapGet("/api/settings/{userId}", async (long userId) =>
{
    using var db = new AppDbContext();
    var setting = await db.UserSettings.FindAsync(userId);

    return Results.Ok(new
    {
        titleType = setting?.TitleType ?? "auto",
        isTranscriptionEnabled = setting?.IsTranscriptionEnabled ?? true
    });
});

// POST /api/settings
// Создаёт или обновляет настройки пользователя (upsert).
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
        existing.IsTranscriptionEnabled = setting.IsTranscriptionEnabled;
    }

    await db.SaveChangesAsync();
    return Results.Ok();
});

// GET /api/notes/media/{fileId}
// Получает прямую ссылку на файл из Telegram и делает редирект.
// Токен бота берётся из DI-контейнера (BotSettings).
app.MapGet("/api/notes/media/{fileId}", async (string fileId, BotSettings settings) =>
{
    try
    {
        var botClient = new TelegramBotClient(settings.Token);
        var fileInfo = await botClient.GetFile(fileId);
        var directUrl = $"https://api.telegram.org/file/bot{settings.Token}/{fileInfo.FilePath}";

        return Results.Redirect(directUrl);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();