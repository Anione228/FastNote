using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

// ============================================================
//  BotWorker — фоновый сервис Telegram-бота FastNote
//
//  Жизненный цикл:
//    1. ExecuteAsync     — запускает polling + цикл напоминаний
//    2. UpdateHandler    — точка входа для всех входящих сообщений
//    3. CheckRemindersAsync — каждые 30 сек сканирует БД и шлёт уведомления
//
//  Фичи обработки сообщений:
//    /start   — приветствие + inline-кнопки
//    ++текст  — дописать к последней заметке
//    .        — поднять заметку в самый верх (Reply на сообщение)
//    медиа    — сохранение фото/голосовых/кружков/видео
//
//  Вспомогательные методы:
//    ProcessForwardedBatch — сборка пакета пересланных сообщений в одну заметку
//    TranscribeVoiceAsync  — расшифровка голосовых через Groq Whisper API
//    ErrorHandler          — логирование ошибок Telegram API
// ============================================================

namespace FastNote1._1
{
    public class BotWorker : BackgroundService
    {
        // ---------------------------------------------------------
        // Поля
        // ---------------------------------------------------------

        private readonly ITelegramBotClient _botClient;
        private readonly BotSettings _settings;

        // Буфер для группировки пересланных сообщений в пакет
        private static readonly ConcurrentDictionary<long, List<Message>> _forwardQueue = new();

        // По одному семафору на пользователя — защита от гонки при пакетной обработке
        private static readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

        // ---------------------------------------------------------
        // Конструктор
        // ---------------------------------------------------------
        /// Принимает настройки из DI и создаёт клиент Telegram Bot API.
        public BotWorker(BotSettings settings)
        {
            _settings = settings;
            _botClient = new TelegramBotClient(_settings.Token);
        }

        // ============================================================
        // ExecuteAsync — точка запуска фонового сервиса
        // ============================================================

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Запускаем polling входящих сообщений (не блокирует поток)
            _botClient.StartReceiving(
                UpdateHandler,
                ErrorHandler,
                cancellationToken: stoppingToken
            );

            // Основной цикл: проверяем напоминания каждые 30 секунд
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

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        // ============================================================
        // CheckRemindersAsync — отправка напоминаний
        // ============================================================
        /// Сканирует БД, находит заметки с истёкшим ReminderAt и отправляет
        /// уведомления в Telegram. После отправки обнуляет ReminderAt.
        private async Task CheckRemindersAsync(CancellationToken cancellationToken)
        {
            using (var db = new AppDbContext())
            {
                // Сравниваем UTC из базы с UTC текущего момента — совпадают идеально
                var now = DateTime.UtcNow;

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

                        // Сбрасываем напоминание, чтобы не отправить повторно
                        note.ReminderAt = null;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Не удалось отправить напоминание для заметки {note.Id}: {ex.Message}");
                    }
                }

                // Сохраняем только если было что сбрасывать
                if (activeReminders.Any())
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }

        // ============================================================
        // UpdateHandler — диспетчер входящих сообщений
        // ============================================================

        private async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            long myAdminId = 637956329;
            if (update.Message is not { } message) return;

            long userId = message.Chat.Id;
            string? userText = message.Text;

            // -----------------------------------------------------------------
            // АНТИ-СПАМ / ПРОВЕРКА НА БАН (Для всех, кроме админа)
            // -----------------------------------------------------------------
            if (userId != myAdminId)
            {
                using (var db = new AppDbContext())
                {
                    var settings = await db.UserSettings.FindAsync(userId);
                    if (settings != null && settings.IsBanned)
                    {
                        // Заблокированный пользователь получает отказ и обработка прекращается
                        await botClient.SendMessage(
                            chatId: userId,
                            text: "🚫 <b>Доступ ограничен.</b> Ваш аккаунт заблокирован администратором.",
                            parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                            cancellationToken: cancellationToken
                        );
                        return;
                    }
                }
            }

            // -----------------------------------------------------------------
            // АДМИН-ПАНЕЛЬ (Доступно ТОЛЬКО для пользователя с myAdminId)
            // -----------------------------------------------------------------
            if (userId == myAdminId)
            {
                // 1. Команда: Список пользователей
                if (userText?.ToLower() == "/admin_users")
                {
                    using (var db = new AppDbContext())
                    {
                        // 1. Считаем общую статистику (это всегда полезно админу)
                        int totalNotes = await db.Notes.CountAsync(cancellationToken);

                        // 2. Получаем только топ-30 самых активных/последних юзеров, чтобы не спамить в чат
                        var topUsers = await db.Notes
                            .GroupBy(n => n.UserId)
                            .Select(g => new { UserId = g.Key, NotesCount = g.Count() })
                            .OrderByDescending(u => u.NotesCount)
                            .Take(30)
                            .ToListAsync(cancellationToken);

                        string response = "<b>👥 Админ-панель: Статистика</b>\n";
                        response += $"Всего заметок в базе: <code>{totalNotes}</code>\n";
                        response += $"Уникальных авторов: <code>{topUsers.Count}</code>\n\n";
                        response += "<b>🔝 Топ-30 активных пользователей:</b>\n";

                        if (!topUsers.Any())
                        {
                            response += "В базе данных пока нет ни одной заметки.";
                        }
                        else
                        {
                            foreach (var u in topUsers)
                            {
                                // 1. Проверяем статус бана в бэкапе настроек
                                var settings = await db.UserSettings.FindAsync(u.UserId);
                                string banStatus = (settings != null && settings.IsBanned) ? "❌ Забанен" : "✅ Активен";

                                string userInfo = $"<code>{u.UserId}</code>";

                                try
                                {
                                    // 2. Запрашиваем данные чата (профиля) у Телеграма
                                    var chat = await botClient.GetChat(u.UserId, cancellationToken);

                                    string firstName = chat.FirstName ?? "Без имени";

                                    // Если есть юзернейм, оформляем его как кликабельную ссылку @username
                                    if (!string.IsNullOrEmpty(chat.Username))
                                    {
                                        userInfo = $"👤 <b>{firstName}</b> (<a href=\"https://t.me/{chat.Username}\">@{chat.Username}</a>) | ID: <code>{u.UserId}</code>";
                                    }
                                    else
                                    {
                                        // Если юзернейма нет, выводим только имя и ID
                                        userInfo = $"👤 <b>{firstName}</b> | ID: <code>{u.UserId}</code>";
                                    }
                                }
                                catch (Exception)
                                {
                                    // Улавливаем ошибку, если пользователь скрыл профиль или удалил диалог с ботом
                                    userInfo = $"👤 <i>Скрытый профиль</i> | ID: <code>{u.UserId}</code>";
                                }

                                // Собираем общую строку ответа для админа
                                response += $"• {userInfo} | Заметок: <b>{u.NotesCount}</b> | {banStatus}\n";
                            }
                        }

                        await botClient.SendMessage(
                            chatId: myAdminId,
                            text: response,
                            parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                            cancellationToken: cancellationToken
                        );
                    }
                    return;
                }

                // 2. Команда: Заблокировать пользователя (/ban 111222333)
                if (userText != null && userText.StartsWith("/ban "))
                {
                    var targetIdStr = userText.Replace("/ban ", "").Trim();
                    if (long.TryParse(targetIdStr, out long targetId))
                    {
                        using (var db = new AppDbContext())
                        {
                            var settings = await db.UserSettings.FindAsync(targetId);
                            if (settings == null)
                            {
                                // Если записи настроек нет, создаем ее, чтобы зафиксировать бан
                                settings = new UserSetting { UserId = targetId, TitleType = "auto", IsTranscriptionEnabled = true };
                                db.UserSettings.Add(settings);
                            }

                            settings.IsBanned = true;
                            await db.SaveChangesAsync(cancellationToken);
                            await botClient.SendMessage(chatId: myAdminId, text: $"🚫 Пользователь <code>{targetId}</code> успешно заблокирован.", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
                        }
                    }
                    else
                    {
                        await botClient.SendMessage(chatId: myAdminId, text: "❌ Неверный формат. Используйте: <code>/ban ID</code>", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
                    }
                    return;
                }

                // 3. Команда: Разблокировать пользователя (/unban 111222333)
                if (userText != null && userText.StartsWith("/unban "))
                {
                    var targetIdStr = userText.Replace("/unban ", "").Trim();
                    if (long.TryParse(targetIdStr, out long targetId))
                    {
                        using (var db = new AppDbContext())
                        {
                            var settings = await db.UserSettings.FindAsync(targetId);
                            if (settings != null)
                            {
                                settings.IsBanned = false;
                                await db.SaveChangesAsync(cancellationToken);
                                await botClient.SendMessage(chatId: myAdminId, text: $"✅ Пользователь <code>{targetId}</code> успешно разблокирован.", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
                            }
                            else
                            {
                                await botClient.SendMessage(chatId: myAdminId, text: "Пользователь не найден в базе данных.", cancellationToken: cancellationToken);
                            }
                        }
                    }
                    else
                    {
                        await botClient.SendMessage(chatId: myAdminId, text: "❌ Неверный формат. Используйте: <code>/unban ID</code>", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
                    }
                    return;
                }

                // 4. Команда: Отправить смс от имени бота (/send 111222333 Привет!)
                if (userText != null && userText.StartsWith("/send "))
                {
                    var parts = userText.Split(' ', 3);
                    if (parts.Length >= 3 && long.TryParse(parts[1], out long targetId))
                    {
                        string messageToUser = parts[2];
                        try
                        {
                            await botClient.SendMessage(
                                chatId: targetId,
                                text: $"💬 <b>Сообщение от администрации FastNote:</b>\n\n{messageToUser}",
                                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                                cancellationToken: cancellationToken
                            );
                            await botClient.SendMessage(chatId: myAdminId, text: "✅ Сообщение успешно доставлено пользователю.", cancellationToken: cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            await botClient.SendMessage(chatId: myAdminId, text: $"❌ Ошибка отправки (возможно, бот заблокирован пользователем): {ex.Message}", cancellationToken: cancellationToken);
                        }
                    }
                    else
                    {
                        await botClient.SendMessage(chatId: myAdminId, text: "❌ Неверный формат. Используйте: <code>/send ID Текст сообщения</code>", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
                    }
                    return;
                }
            }
            // ---------------------------------------------------------
            // Шаг 1. Пересланные сообщения — буферизуем и обрабатываем пакетом
            // ---------------------------------------------------------

            bool isForwarded = message.ForwardFrom != null
                             || message.ForwardFromChat != null
                             || !string.IsNullOrEmpty(message.ForwardSenderName);

            if (isForwarded)
            {
                var semaphore = _locks.GetOrAdd(userId, new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync();

                try
                {
                    var list = _forwardQueue.GetOrAdd(userId, new List<Message>());
                    list.Add(message);

                    // Первое сообщение в пакете — запускаем отложенную обработку через 1.5 сек
                    if (list.Count == 1)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(1500);
                            await semaphore.WaitAsync();
                            try
                            {
                                if (_forwardQueue.TryRemove(userId, out var msgs))
                                {
                                    await ProcessForwardedBatch(botClient, userId, msgs, cancellationToken);
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });
                    }
                }
                finally
                {
                    semaphore.Release();
                }
                return; // Пересланное — дальше не идём
            }

            // ---------------------------------------------------------
            // Шаг 2. Обычные сообщения — разбираем по типу/команде
            // ---------------------------------------------------------


            // --------------------------------------------------
            // 0. Команда /start — приветствие и инструкция
            // --------------------------------------------------
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

            // --------------------------------------------------
            // 1. Фича «++» — дописать текст к последней заметке
            // --------------------------------------------------
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

            // --------------------------------------------------
            // 2. Фича «.» — поднять заметку в самый верх
            //    Reply на сообщение + одиночная точка
            // --------------------------------------------------
            if (userText == "." && message.ReplyToMessage is { } repliedMsg)
            {
                using (var db = new AppDbContext())
                {
                    IQueryable<Note> query = db.Notes.Where(n => n.UserId == userId);

                    // Определяем заметку по типу сообщения, на которое сделан Reply
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
                        originalNote.CreatedAt = DateTime.Now; // Поднимаем наверх обновлением даты

                        // Удаляем дубликаты, если они случайно накопились
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

            // --------------------------------------------------
            // 3. Универсальное сохранение новой заметки (текст + медиа)
            // --------------------------------------------------

            var newNote = new Note
            {
                UserId = userId,
                CreatedAt = DateTime.Now,
                MediaType = "text"
            };

            if (!string.IsNullOrEmpty(userText))
            {
                // Обычное текстовое сообщение
                newNote.Content = userText;
            }
            else if (message.Photo is { } photos)
            {
                // Фото — берём самое крупное разрешение из массива
                var largestPhoto = photos.Last();
                newNote.MediaType = "photo";
                newNote.TelegramFileId = largestPhoto.FileId;
                newNote.Content = message.Caption ?? "";
            }
            else if (message.Document is { } doc && doc.MimeType != null && doc.MimeType.StartsWith("image/"))
            {
                // Документ с MIME-типом image (картинка без сжатия)
                newNote.MediaType = "photo";
                newNote.TelegramFileId = doc.FileId;
                newNote.Content = message.Caption ?? "";
            }
            else if (message.Voice is { } voice)
            {
                // Голосовое сообщение — сохраняем и при необходимости расшифровываем
                newNote.MediaType = "voice";
                newNote.TelegramFileId = voice.FileId;
                newNote.Duration = voice.Duration;

                // Читаем настройку транскрибации из БД для этого пользователя
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
                // Видео-кружок
                newNote.MediaType = "video_note";
                newNote.TelegramFileId = videoNote.FileId;
                newNote.Duration = videoNote.Duration;
                newNote.Content = "🔵 Видео-заметка (кружок)";
            }
            else if (message.Video is { } video)
            {
                // Обычное видео
                newNote.MediaType = "video";
                newNote.TelegramFileId = video.FileId;
                newNote.Duration = video.Duration;
                newNote.Content = message.Caption ?? "🎬 Видео";
            }
            else
            {
                return; // Неподдерживаемый тип — игнорируем
            }

            // --------------------------------------------------
            // Авто-генерация заголовка по настройке пользователя
            // --------------------------------------------------
            using (var dbMedia = new AppDbContext())
            {
                var settingMedia = await dbMedia.UserSettings.FindAsync(userId);
                string titleTypeMedia = settingMedia?.TitleType ?? "auto";

                if (titleTypeMedia == "auto")
                {
                    if (!string.IsNullOrWhiteSpace(newNote.Content))
                    {
                        // Берём первую непустую строку, обрезаем до 20 символов
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
                        // Медиа без подписи — эмодзи-заголовок по типу файла
                        newNote.Title = newNote.MediaType switch
                        {
                            "photo" => "📷 Фотозаметка",
                            "voice" => "🎙 Голосовая заметка",
                            "video_note" => "🔵 Кружок",
                            "video" => "🎬 Видео",
                            _ => "Заметка"
                        };
                    }
                }
                else if (titleTypeMedia == "date")
                {
                    // Дата и время создания как заголовок
                    newNote.Title = newNote.CreatedAt.ToString("dd.MM.yyyy HH:mm");
                }
                else
                {
                    newNote.Title = "Заметка";
                }

                dbMedia.Notes.Add(newNote);
                await dbMedia.SaveChangesAsync(cancellationToken);
            }

            // --------------------------------------------------
            // 4. Отправка фидбека с inline-кнопками
            // --------------------------------------------------

            string feedbackText = newNote.MediaType switch
            {
                "photo" => "Фотография успешно сохранена в заметки! 🖼",
                "voice" => "Голосовое сообщение добавлено! 🎙",
                "video_note" => "Кружок успешно сохранен! 🔵",
                "video" => "Видео успешно сохранено! 🎬",
                _ => "Заметка сохранена! 📝"
            };

            // Подставляем ID только что созданной заметки в ссылку
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
                replyMarkup: inlineKeyboardNormal,
                cancellationToken: cancellationToken
            );
        }

        // ============================================================
        // ProcessForwardedBatch — сборка пересланных сообщений в заметку
        // ============================================================

        /// Собирает все сообщения из буфера (пришедшие за ~1.5 сек)
        /// в одну заметку с объединённым текстом.
        /// Медиа-вложение берётся только первое (модель хранит один файл на заметку).
        private async Task ProcessForwardedBatch(ITelegramBotClient botClient, long userId, List<Message> messages, CancellationToken ct)
        {
            // Создаём мастер-заметку с временным заголовком
            var newNote = new Note
            {
                UserId = userId,
                CreatedAt = DateTime.Now,
                Title = $"Пересылка ({messages.Count} сообщ.)",
                Content = "",
                MediaType = "text"
            };

            using (var db = new AppDbContext())
            {
                db.Notes.Add(newNote);
                await db.SaveChangesAsync(ct);

                // Наполняем контент текстом из всех сообщений, разделяя блоки «---»
                foreach (var msg in messages)
                {
                    if (!string.IsNullOrEmpty(msg.Text))
                        newNote.Content += (string.IsNullOrEmpty(newNote.Content) ? "" : "\n---\n") + msg.Text;

                    // Если в пакете есть медиа, берём самое первое (одно вложение на заметку)
                    if (newNote.TelegramFileId == null)
                    {
                        if (msg.Photo != null) { newNote.TelegramFileId = msg.Photo.Last().FileId; newNote.MediaType = "photo"; }
                        else if (msg.Voice != null) { newNote.TelegramFileId = msg.Voice.FileId; newNote.MediaType = "voice"; }
                        // и т.д. для других типов
                    }
                }
                await db.SaveChangesAsync(ct);
            }

            await botClient.SendMessage(userId, $"✅ Сохранено {messages.Count} сообщений в одну заметку!", cancellationToken: ct);
        }

        // ============================================================
        // ErrorHandler — обработчик ошибок Telegram API
        // ============================================================

        private Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Ошибка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }

        // ============================================================
        // TranscribeVoiceAsync — расшифровка голосового через Groq Whisper
        // ============================================================

        /// Скачивает аудиофайл с серверов Telegram и отправляет в Groq API
        /// для расшифровки моделью Whisper Large v3 (язык: русский).
        /// При любой ошибке возвращает fallback-строку вместо исключения.
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

                // 2. Скачиваем аудио в MemoryStream
                using var audioStream = new MemoryStream();
                await botClient.DownloadFile(fileInfo.FilePath, audioStream, cancellationToken);
                audioStream.Position = 0;

                // 3. Готовим HTTP-запрос к Groq API
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.GroqApiKey);

                using var form = new MultipartFormDataContent();
                var streamContent = new StreamContent(audioStream);

                // Telegram присылает голосовые в формате Ogg Opus
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/ogg");

                form.Add(streamContent, "file", "voice.ogg");
                form.Add(new StringContent("whisper-large-v3"), "model"); // Лучшая модель Groq для русского
                form.Add(new StringContent("ru"), "language");            // Принудительно русский язык

                // 4. Отправляем запрос и разбираем JSON-ответ
                var response = await httpClient.PostAsync("https://api.groq.com/openai/v1/audio/transcriptions", form, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();

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