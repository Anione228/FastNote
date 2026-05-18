using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
    return Results.Ok(new { titleType = setting?.TitleType ?? "auto" });
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
    }
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

app.Run();