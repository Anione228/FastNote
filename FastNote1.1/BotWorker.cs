using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.EntityFrameworkCore;

public class BotWorker : BackgroundService
{
    

    private readonly ITelegramBotClient _botClient;
    private readonly BotSettings _settings;

    public BotWorker(BotSettings settings)
    {
        _settings = settings;

        // Передаем реальный токен в клиент бота
        _botClient = new TelegramBotClient(_settings.Token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Запускаем прослушку сообщений
        _botClient.StartReceiving(UpdateHandler, ErrorHandler, cancellationToken: stoppingToken);
        Console.WriteLine("🤖 Бот успешно запущен...");

        // 2. ФОНОВЫЙ ТАЙМЕР НАПОМИНАНИЙ: проверяет базу каждые 30 секунд
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckRemindersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка в таймере напоминаний: {ex.Message}");
            }
        }
    }

    // Метод проверки напоминаний в SQLite
    private async Task CheckRemindersAsync(CancellationToken ct)
    {
        using var db = new AppDbContext();
        var now = DateTime.UtcNow;

        // Ищем заметки, у которых пришло время напоминания
        var dueReminders = await db.Notes
            .Where(n => n.ReminderAt != null && n.ReminderAt <= now)
            .ToListAsync(ct);

        foreach (var note in dueReminders)
        {
            // Отправляем пользователю сообщение-напоминание
            string messageText = $"🔔 *НАПОМИНАНИЕ!* 🔔\n\n*Заметка:* {note.Title}\n\n{note.Content}";

            await _botClient.SendMessage(
                chatId: note.UserId,
                text: messageText,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct
            );

            // Сбрасываем напоминание, чтобы оно не срабатывало повторно
            note.ReminderAt = null;
        }

        if (dueReminders.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is { Text: { } userText } message)
        {
            // ==========================================
            // 1. ФИЧА «++»: Добавление текста к последней заметке
            // ==========================================
            if (userText.StartsWith("++"))
            {
                string additionalText = userText.Substring(2).Trim();
                using (var db = new AppDbContext())
                {
                    // Находим самую последнюю заметку пользователя
                    var lastNote = await db.Notes
                        .Where(n => n.UserId == message.Chat.Id)
                        .OrderByDescending(n => n.CreatedAt)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (lastNote != null)
                    {
                        // Добавляем текст с новой строки
                        lastNote.Content += "\n" + additionalText;
                        // Обновляем время, чтобы обновленная заметка поднялась наверх
                        lastNote.CreatedAt = DateTime.Now;

                        await db.SaveChangesAsync(cancellationToken);

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                        new[]
                        {
                            InlineKeyboardButton.WithWebApp("⏰ Добавить напоминание", new WebAppInfo { Url = $"{_settings.WebAppUrl}?noteId={lastNote.Id}&action=reminder" }),
                            InlineKeyboardButton.WithWebApp("📝 Редактировать заметку", new WebAppInfo { Url = $"{_settings.WebAppUrl}?noteId={lastNote.Id}" })
                        }
                    });

                        await botClient.SendMessage(
                            chatId: message.Chat.Id,
                            text: $"Текст успешно добавлен к последней заметке! 📝",
                            replyMarkup: inlineKeyboard,
                            cancellationToken: cancellationToken
                        );
                    }
                    else
                    {
                        await botClient.SendMessage(
                            chatId: message.Chat.Id,
                            text: $"У вас ещё нет заметок. Отправьте текст без ++, чтобы создать первую.",
                            cancellationToken: cancellationToken
                        );
                    }
                }
                return;
            }

            // ==========================================
            // 2. ФИЧА «.»: Перемещение заметки в самый верх (ИСПРАВЛЕНО)
            // ==========================================
            if (userText == "." && message.ReplyToMessage is { Text: { } repliedText })
            {
                using (var db = new AppDbContext())
                {
                    // ИСПРАВЛЕНО: Вместо строгого равенства (==) используем StartsWith,
                    // чтобы фича работала, даже если к заметке дописывали текст через ++
                    var existingNotes = await db.Notes
                        .Where(n => n.UserId == message.Chat.Id && n.Content.StartsWith(repliedText))
                        .OrderBy(n => n.CreatedAt)
                        .ToListAsync(cancellationToken);

                    if (existingNotes.Any())
                    {
                        // Берем самую первую (оригинальную) заметку и даем ей текущее время
                        var originalNote = existingNotes.First();
                        originalNote.CreatedAt = DateTime.Now;

                        // Если точка поставлена на только что пересланное сообщение (дубликат)
                        if (existingNotes.Count > 1)
                        {
                            var duplicateNote = existingNotes.Last();
                            if (duplicateNote.Id != originalNote.Id)
                            {
                                db.Notes.Remove(duplicateNote);
                            }
                        }

                        await db.SaveChangesAsync(cancellationToken);

                        await botClient.SendMessage(
                            chatId: message.Chat.Id,
                            text: $"Заметка успешно перемещена в самый верх! 🔝",
                            cancellationToken: cancellationToken
                        );
                    }
                    else
                    {
                        await botClient.SendMessage(
                            chatId: message.Chat.Id,
                            text: $"Не найдено сохраненной заметки с таким текстом.",
                            cancellationToken: cancellationToken
                        );
                    }
                }
                return;
            }

            // ==========================================
            // 3. СТАНДАРТНОЕ СОЗДАНИЕ ЗАМЕТКИ (Твой прошлый код)
            // ==========================================
            string smartTitle = "Новая заметка";
            int savedNoteId;

            using (var db = new AppDbContext())
            {
                var setting = await db.UserSettings.FindAsync(message.Chat.Id);
                string titleType = setting?.TitleType ?? "auto";

                if (titleType == "date")
                {
                    smartTitle = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                }
                else
                {
                    var lines = userText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        smartTitle = lines[0].Trim();
                        if (smartTitle.Length > 25)
                        {
                            smartTitle = smartTitle.Substring(0, 25) + "...";
                        }
                    }
                }

                var newNote = new Note
                {
                    UserId = message.Chat.Id,
                    Title = smartTitle,
                    Content = userText,
                    CreatedAt = DateTime.Now,
                    ReminderAt = null
                };
                db.Notes.Add(newNote);
                await db.SaveChangesAsync(cancellationToken);
                savedNoteId = newNote.Id;
            }

            var inlineKeyboardNormal = new InlineKeyboardMarkup(new[]
            {
            new[]
            {
                InlineKeyboardButton.WithWebApp("⏰ Добавить напоминание", new WebAppInfo { Url = $"{_settings.WebAppUrl}?noteId={savedNoteId}&action=reminder" }),
                InlineKeyboardButton.WithWebApp("📝 Редактировать заметку", new WebAppInfo { Url = $"{_settings.WebAppUrl}?noteId={savedNoteId}" })
            }
        });

            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: $"Заметка успешно сохранена! 📝",
                replyMarkup: inlineKeyboardNormal,
                cancellationToken: cancellationToken
                );
        }
    }

    private Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Console.WriteLine($"❌ Ошибка бота: {exception.Message}");
        return Task.CompletedTask;
    }
}