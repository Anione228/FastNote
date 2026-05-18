using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace FastNote1._1
{
    public class BotWorker : BackgroundService
    {
        private readonly ITelegramBotClient _botClient;
        private readonly BotSettings _settings;

        // Конструктор принимает настройки из DI-контейнера
        public BotWorker(BotSettings settings)
        {
            _settings = settings;
            _botClient = new TelegramBotClient(_settings.Token);
        }

      
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _botClient.StartReceiving(
                UpdateHandler,
                ErrorHandler,
                cancellationToken: stoppingToken
            );

            // ТЕПЕРЬ ЦИКЛ ЖИВОЙ: Каждые 30 секунд проверяем, не пора ли отправить напоминалку
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckRemindersAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при проверке напоминаний: {ex.Message}");
                }

                // Проверка каждые 30 секунд (хватает с головой, чтобы не нагружать базу)
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        // НОВЫЙ МЕТОД: Сканирует БД и шлет уведомления в Telegram
        private async Task CheckRemindersAsync(CancellationToken cancellationToken)
        {
            using (var db = new AppDbContext())
            {
                // ИСПРАВЛЕНО: Вместо DateTime.Now используем DateTime.UtcNow
                var now = DateTime.UtcNow;

                // Теперь мы сравниваем UTC из базы с UTC текущего момента — они совпадут идеально!
                var activeReminders = await db.Notes
                    .Where(n => n.ReminderAt != null && n.ReminderAt <= now)
                    .ToListAsync(cancellationToken);

                foreach (var note in activeReminders)
                {
                    try
                    {
                        string reminderText = $"⏰ <b>НАПОМИНАНИЕ!</b>\n\n" +
                                             $"📌 <b>{note.Title}</b>\n" +
                                             $"{note.Content}";

                        var inlineKeyboard = new InlineKeyboardMarkup(new[]
                        {
                    new[]
                    {
                        InlineKeyboardButton.WithWebApp("📝 Открыть заметку", new WebAppInfo { Url = $"{_settings.WebAppUrl}?noteId={note.Id}" })
                    }
                });

                        await _botClient.SendMessage(
                            chatId: note.UserId,
                            text: reminderText,
                            parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                            replyMarkup: inlineKeyboard,
                            cancellationToken: cancellationToken
                        );

                        note.ReminderAt = null;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Не удалось отправить напоминание для заметки {note.Id}: {ex.Message}");
                    }
                }

                if (activeReminders.Any())
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }


        private async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // Нас интересуют только текстовые или медиа сообщения из чатов
            if (update.Message is { } message)
            {
                string? userText = message.Text;
                long userId = message.Chat.Id;

                // ==========================================
                // 0. КОМАНДА /start: Приветствие и инструкция
                // ==========================================
                if (userText?.ToLower() == "/start")
                {
                    string welcomeText =
                        "👋 <b>Привет! Я твой персональный бот для быстрых заметок FastNote.</b>\n\n" +
                        "Я помогу тебе мгновенно сохранять мысли, задачи и настраивать напоминания через удобное Мини-Приложение.\n\n" +
                        "🚀 <b>Как мной пользоваться прямо в чате:</b>\n\n" +
                        "1️⃣ <b>Просто отправь текст или медиа</b> — я сразу создам новую заметку.\n" +
                        "2️⃣ <code>++ твой текст</code> — введи это перед сообщением, чтобы <u>дописать</u> текст в самый конец твоей последней заметки, не плодя новые.\n" +
                        "3️⃣ <b>Перешли сообщение и ответь точкой</b> <code>.</code> — если ты ответишь (сделаешь Reply) одиночной точкой на пересланное сообщение, я найду оригинал этой заметки и <u>подниму её в самый верх</u> списка.\n\n" +
                        "👇 Нажми на кнопку ниже, чтобы открыть свои заметки или настроить автоматические заголовки!";

                    var inlineKeyboardStart = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithWebApp("📝 Открыть заметки", new WebAppInfo { Url = _settings.WebAppUrl }),
                            InlineKeyboardButton.WithWebApp("⚙️ Настройки", new WebAppInfo { Url = $"{_settings.WebAppUrl}?action=settings" })
                        }
                    });

                    await botClient.SendMessage(
                        chatId: userId,
                        text: welcomeText,
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                        replyMarkup: inlineKeyboardStart,
                        cancellationToken: cancellationToken
                    );
                    return;
                }

                // ==========================================
                // 1. ФИЧА «++»: Добавление текста к последней заметке
                // ==========================================
                if (userText != null && userText.StartsWith("++"))
                {
                    string textToAppend = userText.Substring(2).Trim();

                    using (var db = new AppDbContext())
                    {
                        var lastNote = await db.Notes
                            .Where(n => n.UserId == userId)
                            .OrderByDescending(n => n.CreatedAt)
                            .FirstOrDefaultAsync(cancellationToken);

                        if (lastNote != null)
                        {
                            lastNote.Content += "\n" + textToAppend;
                            await db.SaveChangesAsync(cancellationToken);

                            await botClient.SendMessage(
                                chatId: userId,
                                text: "Текст успешно добавлен к последней заметке! ➕",
                                cancellationToken: cancellationToken
                            );
                        }
                        else
                        {
                            await botClient.SendMessage(
                                chatId: userId,
                                text: "У тебя еще нет заметок, к которым можно что-то добавить.",
                                cancellationToken: cancellationToken
                            );
                        }
                    }
                    return;
                }

                // ==========================================
                // 2. ФИЧА «.»: Перемещение заметки в самый верх (ТЕПЕРЬ ДЛЯ ВСЕХ ТИПОВ МЕДИА)
                // ==========================================
                if (userText == "." && message.ReplyToMessage is { } repliedMsg)
                {
                    using (var db = new AppDbContext())
                    {
                        IQueryable<Note> query = db.Notes.Where(n => n.UserId == userId);

                        // Определяем, какое именно сообщение переслали
                        if (repliedMsg.Photo is { } p)
                        {
                            var fId = p.Last().FileId;
                            query = query.Where(n => n.TelegramFileId == fId);
                        }
                        else if (repliedMsg.Voice is { } v)
                        {
                            query = query.Where(n => n.TelegramFileId == v.FileId);
                        }
                        else if (repliedMsg.VideoNote is { } vn)
                        {
                            query = query.Where(n => n.TelegramFileId == vn.FileId);
                        }
                        else if (repliedMsg.Document is { } d && d.MimeType != null && d.MimeType.StartsWith("image/"))
                        {
                            query = query.Where(n => n.TelegramFileId == d.FileId);
                        }
                        else if (!string.IsNullOrEmpty(repliedMsg.Text))
                        {
                            query = query.Where(n => n.Content.StartsWith(repliedMsg.Text));
                        }
                        else
                        {
                            await botClient.SendMessage(chatId: userId, text: "Этот тип сообщения не поддерживается для поднятия.", cancellationToken: cancellationToken);
                            return;
                        }

                        var existingNotes = await query.OrderBy(n => n.CreatedAt).ToListAsync(cancellationToken);

                        if (existingNotes.Any())
                        {
                            var originalNote = existingNotes.First();
                            originalNote.CreatedAt = DateTime.Now; // Поднимаем наверх

                            // Удаляем дубликаты, если они случайно создались
                            if (existingNotes.Count > 1)
                            {
                                foreach (var dup in existingNotes.Skip(1))
                                {
                                    db.Notes.Remove(dup);
                                }
                            }

                            await db.SaveChangesAsync(cancellationToken);
                            await botClient.SendMessage(chatId: userId, text: "Заметка успешно перемещена в самый верх! 🔝", cancellationToken: cancellationToken);
                        }
                        else
                        {
                            await botClient.SendMessage(chatId: userId, text: "Не найдено сохраненной заметки для этого сообщения.", cancellationToken: cancellationToken);
                        }
                    }
                    return;
                }

                // ==========================================
                // 3. УНИВЕРСАЛЬНОЕ СОХРАНЕНИЕ ЗАМЕТОК (ТЕКСТ + МЕДИА)
                // ==========================================
                var newNote = new Note
                {
                    UserId = userId,
                    CreatedAt = DateTime.Now,
                    MediaType = "text"
                };

                if (!string.IsNullOrEmpty(userText))
                {
                    newNote.Content = userText;
                }
                else if (message.Photo is { } photos)
                {
                    var largestPhoto = photos.Last();
                    newNote.MediaType = "photo";
                    newNote.TelegramFileId = largestPhoto.FileId;
                    newNote.Content = message.Caption ?? "";
                }
                else if (message.Document is { } doc && doc.MimeType != null && doc.MimeType.StartsWith("image/"))
                {
                    newNote.MediaType = "photo";
                    newNote.TelegramFileId = doc.FileId;
                    newNote.Content = message.Caption ?? "";
                }
                else if (message.Voice is { } voice)
                {
                    newNote.MediaType = "voice";
                    newNote.TelegramFileId = voice.FileId;
                    newNote.Duration = voice.Duration;

                    // Проверяем настройку пользователя в базе
                    bool isTranscriptionEnabled = true;
                    using (var dbMedia = new AppDbContext())
                    {
                        var settings = await dbMedia.UserSettings.FindAsync(userId);
                        if (settings != null)
                        {
                            isTranscriptionEnabled = settings.IsTranscriptionEnabled;
                        }
                    }

                    string transcribedText;
                    if (isTranscriptionEnabled)
                    {
                        transcribedText = await TranscribeVoiceAsync(botClient, voice.FileId, cancellationToken);
                    }
                    else
                    {
                        transcribedText = "🎙 Голосовое сообщение (Расшифровка отключена в настройках)";
                    }

                    newNote.Content = !string.IsNullOrEmpty(message.Caption)
                        ? $"{message.Caption}\n\n{transcribedText}"
                        : transcribedText;
                }
                else if (message.VideoNote is { } videoNote)
                {
                    newNote.MediaType = "video_note";
                    newNote.TelegramFileId = videoNote.FileId;
                    newNote.Duration = videoNote.Duration;
                    newNote.Content = "🔵 Видео-заметка (кружок)";
                }
                else
                {
                    return;
                }

                // Автогенерация заголовка
                using (var dbMedia = new AppDbContext())
                {
                    var settingMedia = await dbMedia.UserSettings.FindAsync(userId);
                    string titleTypeMedia = settingMedia?.TitleType ?? "auto";

                    if (titleTypeMedia == "auto")
                    {
                        if (!string.IsNullOrWhiteSpace(newNote.Content))
                        {
                            var lines = newNote.Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            string smartTitle = lines.Length > 0 ? lines[0] : newNote.Content;

                            if (smartTitle.Length > 20)
                            {
                                smartTitle = smartTitle.Substring(0, 20) + "...";
                            }
                            newNote.Title = smartTitle;
                        }
                        else
                        {
                            newNote.Title = newNote.MediaType switch
                            {
                                "photo" => "📷 Фотозаметка",
                                "voice" => "🎙 Голосовая заметка",
                                "video_note" => "🔵 Кружок",
                                _ => "Заметка"
                            };
                        }
                    }
                    else if (titleTypeMedia == "date")
                    {
                        newNote.Title = newNote.CreatedAt.ToString("dd.MM.yyyy HH:mm");
                    }
                    else
                    {
                        newNote.Title = "Заметка";
                    }

                    dbMedia.Notes.Add(newNote);
                    await dbMedia.SaveChangesAsync(cancellationToken);
                }

                // ==========================================
                // 4. ОТПРАВКА ФИДБЕКА С ИНЛАЙН-КНОПКАМИ
                // ==========================================
                string feedbackText = newNote.MediaType switch
                {
                    "photo" => "Фотография успешно сохранена в заметки! 🖼",
                    "voice" => "Голосовое сообщение добавлено! 🎙",
                    "video_note" => "Кружок успешно сохранен! 🔵",
                    _ => "Заметка сохранена! 📝"
                };

                // Создаем клавиатуру, подставляя ID созданной заметки (newNote.Id)
                var inlineKeyboardNormal = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithWebApp("⏰ Добавить напоминание", new WebAppInfo { Url = $"{_settings.WebAppUrl}?noteId={newNote.Id}&action=reminder" }),
                        InlineKeyboardButton.WithWebApp("📝 Редактировать заметку", new WebAppInfo { Url = $"{_settings.WebAppUrl}?noteId={newNote.Id}" })
                    }
                });

                await botClient.SendMessage(
                    chatId: userId,
                    text: feedbackText,
                    replyMarkup: inlineKeyboardNormal, // Прикрепляем кнопки к сообщению
                    cancellationToken: cancellationToken
                );
            }
        }
        private Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            // Здесь можно логировать системные ошибки Telegram API, если нужно
            Console.WriteLine($"Ошибка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }
        // МЕТОД ДЛЯ БЕСПЛАТНОЙ РАСШИФРОВКИ ГОЛОСОВЫХ ЧЕРЕЗ WHISPER
        private async Task<string> TranscribeVoiceAsync(ITelegramBotClient botClient, string fileId, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(_settings.GroqApiKey))
                {
                    return "🎙 Голосовое сообщение (Разработчик не настроил GroqApiKey)";
                }

                // 1. Получаем путь к файлу на серверах Telegram
                var fileInfo = await botClient.GetFile(fileId, cancellationToken);
                if (string.IsNullOrEmpty(fileInfo.FilePath))
                {
                    return "🎙 Голосовое сообщение (Не удалось скачать аудиофайл)";
                }

                // 2. Скачиваем аудио в поток памяти (MemoryStream)
                using var audioStream = new MemoryStream();
                await botClient.DownloadFile(fileInfo.FilePath, audioStream, cancellationToken);
                audioStream.Position = 0;

                // 3. Готовим HTTP-запрос к API Groq
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.GroqApiKey);

                using var form = new MultipartFormDataContent();
                var streamContent = new StreamContent(audioStream);

                // Указываем тип аудио, который присылает Telegram (Ogg Opus)
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/ogg");

                form.Add(streamContent, "file", "voice.ogg");
                form.Add(new StringContent("whisper-large-v3"), "model"); // Используем лучшую модель
                form.Add(new StringContent("ru"), "language");            // Принудительно ставим русский язык

                // 4. Отправляем запрос
                var response = await httpClient.PostAsync("https://api.groq.com/openai/v1/audio/transcriptions", form, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();

                    // Быстро парсим JSON стандартным System.Text.Json
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("text", out var textProp))
                    {
                        string resultText = textProp.GetString() ?? "";
                        return !string.IsNullOrWhiteSpace(resultText) ? "🗣 " + resultText : "🎙 (Пустое голосовое сообщение)";
                    }
                }
                else
                {
                    Console.WriteLine($"Ошибка Groq API: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при транскрибации голосового: {ex.Message}");
            }

            return "🎙 Голосовое сообщение (Не удалось расшифровать)";
        }
    }
}