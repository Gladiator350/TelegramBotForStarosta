using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Polly;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using Newtonsoft.Json;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;
using JsonSerializer = System.Text.Json.JsonSerializer;

public class BotBackgroundService : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<BotBackgroundService> _logger;
    private readonly HttpClient _httpClient; // Добавляем HttpClient
    private readonly IMemoryCache _cache;
    private readonly List<long> _adminWhitelist;
    private readonly HashSet<long> _userChatIds;
    private readonly ConcurrentDictionary<(long chatId, string command), DateTime> _lastCommandUsage;
    private readonly string _apiUrl;
    private const int CooldownSeconds = 60;

    public BotBackgroundService(
        ITelegramBotClient botClient,
        ILogger<BotBackgroundService> logger,
        HttpClient httpClient, // Добавляем HttpClient
        IMemoryCache cache)
    {
        _botClient = botClient;
        _logger = logger;
        _httpClient = httpClient; // Инициализируем HttpClient
        _cache = cache;
        _adminWhitelist = new List<long> { 1563759837, 960762871 };
        _userChatIds = new HashSet<long>();
        _lastCommandUsage = new ConcurrentDictionary<(long chatId, string command), DateTime>();
        _apiUrl = "https://telegram-bot-starosta-backend.onrender.com/api/v1/schedule";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting bot...");

        // Запуск периодического обновления расписания
        var updateTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await UpdateScheduleAsync();
                await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
            }
        }, stoppingToken);

        // Настройка обработки сообщений и callback-запросов
        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            HandlePollingErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }, // Разрешаем обработку callback-запросов
            },
            cancellationToken: stoppingToken
        );

        _logger.LogInformation("Bot started.");
    }
    async Task UpdateScheduleAsync()
    {
        try
        {
            _logger.LogInformation("⏳ Обновление расписания...");

            string groupName = "М3О-303С-22";
            string encodedGroupName = HttpUtility.UrlEncode(groupName);
        
            // Политика повтора для запроса
            var retryPolicy = Policy
                .Handle<HttpRequestException>()
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

            var response = await retryPolicy.ExecuteAsync(async () =>
            {
                return await _httpClient.GetAsync($"{_apiUrl}/currentDay?groupName={encodedGroupName}");
            });

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var schedule = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
            string formattedSchedule = FormatSchedule(schedule ?? new List<ScheduleItem>());

            // Обновляем кэш
            _cache.Set("schedule", formattedSchedule, GetTimeUntilMidnightUTC());
        
            _logger.LogInformation("✅ Расписание обновлено");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Ошибка обновления расписания через таймер");
        }
    }
    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Message is not { Text: { } messageText, Chat: { } chat, From: {} user }) return;

            _logger.LogInformation($"Received: '{messageText}' from {chat.Id}");
            _userChatIds.Add(chat.Id);

            var response = await ProcessCommand(messageText, chat.Id, user);
            if (response is (string text, InlineKeyboardMarkup keyboard))
            {
                // Отправляем сообщение с клавиатурой
                await botClient.SendTextMessageAsync(
                    chat.Id,
                    text,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: ct
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message");
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiEx => $"Telegram API Error ({apiEx.ErrorCode}): {apiEx.Message}",
            _ => exception.ToString()
        };
        _logger.LogError(errorMessage);
        return Task.CompletedTask;
    }
    private async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken ct)
    {
        try
        {
            var chatId = callbackQuery.Message.Chat.Id;
            var data = callbackQuery.Data;

            string responseText = data switch
            {
                "tomorrow" => "Вы выбрали: Завтра",
                "day_after_tomorrow" => "Вы выбрали: Послезавтра",
                _ => "Неизвестная команда"
            };

            // Отправляем ответ пользователю
            await botClient.SendTextMessageAsync(
                chatId,
                responseText,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            // Уведомляем Telegram, что запрос обработан
            await botClient.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling callback query");
        }
    }

    private async Task<(string text, InlineKeyboardMarkup? keyboard)> ProcessCommand(string message, long chatId, User user)
    {
        var command = message;

        // Ограничиваем выполнение команды /schedule не чаще одного раза за CooldownSeconds
        if (command == "/schedule")
        {
            if (_lastCommandUsage.TryGetValue((chatId, command), out var lastUsage))
            {
                var nextAllowedTime = lastUsage.AddSeconds(CooldownSeconds);
                if (false/*DateTime.UtcNow < nextAllowedTime*/)
                {
                    var remaining = (int)(nextAllowedTime - DateTime.UtcNow).TotalSeconds;
                    return ($"⏳ Пожалуйста, подождите {remaining} секунд перед повторным использованием команды.", null);
                }
            }
            _lastCommandUsage[(chatId, command)] = DateTime.UtcNow;
        }
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Помощь", "❓ помощь"),
            }
        });
        if (command.Contains("/start")) return await GetStartWithKeyboard(user);
        if (command.Contains("Расписание на сегодня")) return await GetScheduleWithKeyboard();
        if (command.Contains("Расписание на неделю")) return (await GetScheduleWeek(), null);
        if (command.Contains("📝 дедлайны")) return (GetDeadlines(), null);
        if (command.Contains("❓ помощь")) return (GetHelpMessage(IsAdmin(chatId), user), null);
        if (command.Contains("/notify")) return (ProcessNotification(message.Split(' ')[1]), null);
        if (command.Contains("/broadcast") && IsAdmin(chatId)) return (await ProcessBroadcast(message.Split(' ')[1]), null);
        return ("⚠️ Неизвестная команда. Используйте /help ", null);
    }

    private string GetWelcomeMessage(User user) => """
        <b>🎓 Бот старосты группы М3О-303С-22</b>
        
        <i>Доступные команды:</i>
        /schedule - Расписание на сегодня
        /deadlines - Актуальные дедлайны
        /notify [причина] - Уведомить о пропуске
        /help - Справка по командам
        """;
    private async Task<(string StaertText, InlineKeyboardMarkup Keyboard)> GetStartWithKeyboard(User user)
    {
        var scheduleText = GetWelcomeMessage(user);

        // Создаем inline-клавиатуру
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Зарегестрироваться", "tomorrow"),
            }
        });

        return (scheduleText, inlineKeyboard);
    }
    private string GetHelpMessage(bool isAdmin, User user) => isAdmin
        ? """
          <b>👑 Админ-команды:</b>
          /broadcast [сообщение] - Рассылка всем пользователям
          
          """ + GetWelcomeMessage(user)
        : GetWelcomeMessage(user);

    private async Task<string> GetSchedule()
    {
        if (_cache.TryGetValue("schedule", out string cachedSchedule))
        {
            return cachedSchedule;
        }

        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        string scheduleString = await retryPolicy.ExecuteAsync(async () =>
        {
            string groupName = "М3О-303С-22";
            string encodedGroupName = HttpUtility.UrlEncode(groupName);
            var response = await _httpClient.GetAsync($"{_apiUrl}/currentDay?groupName={encodedGroupName}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var schedule = System.Text.Json.JsonSerializer.Deserialize<List<ScheduleItem>>(json);
            return FormatSchedule(schedule ?? new List<ScheduleItem>());
        });

        _cache.Set("schedule", scheduleString, GetTimeUntilMidnightUTC());
        return scheduleString;
    }
    private async Task<(string ScheduleText, InlineKeyboardMarkup Keyboard)> GetScheduleWithKeyboard()
    {
        var scheduleText = await GetSchedule();

        // Создаем inline-клавиатуру
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Завтра", "tomorrow"),
                InlineKeyboardButton.WithCallbackData("Послезавтра", "day_after_tomorrow")
            }
        });

        return (scheduleText, inlineKeyboard);
    }

    private async Task<string> GetScheduleWeek()
    {
        if (_cache.TryGetValue("scheduleWeek", out string cachedSchedule))
        {
            return cachedSchedule;
        }

        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        string scheduleString = await retryPolicy.ExecuteAsync(async () =>
        {
            string groupName = "М3О-303С-22";
            string encodedGroupName = HttpUtility.UrlEncode(groupName);
            var response = await _httpClient.GetAsync($"{_apiUrl}/week?groupName={encodedGroupName}");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var schedule = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
            return FormatWeekSchedule(schedule ?? new List<ScheduleItem>());
        });

        _cache.Set("scheduleWeek", scheduleString, GetTimeUntilMidnightUTC());
        return scheduleString;
    }

    private string FormatSchedule(List<ScheduleItem> schedule)
    {
        if (schedule.Count == 0) return "📭 Расписание на неделю отсутствует";

        return schedule.Aggregate("<b>📅 Расписание на сегодня:</b>\n\n", (current, item) => current + $"""
            📚 <i>{item.SubjectName}</i>
            🕒 {item.StartTime[..5]}-{item.EndTime[..5]}
            🏫 Ауд. {item.Classroom}
            👨🏫 {item.TeacherName}
            🔢 {FormatLessonType(item.LessonType)}
            ------------------
            """ + "\n");
    }

    private string FormatWeekSchedule(List<ScheduleItem> schedule)
    {
        if (schedule.Count == 0) return "📭 Расписание на неделю отсутствует";

        var scheduleByDay = schedule
            .GroupBy(item => item.LessonDate)
            .OrderBy(group => group.Key);

        var result = new StringBuilder("<b>📅 Расписание на неделю:</b>\n\n");

        foreach (var dayGroup in scheduleByDay)
        {
            var date = DateTime.Parse(dayGroup.Key);
            var dayOfWeek = date.ToString("dddd", new CultureInfo("ru-RU"));

            result.AppendLine($"<b>📆 {dayOfWeek} ({date:dd.MM.yyyy})</b>\n");

            foreach (var item in dayGroup.OrderBy(item => item.StartTime))
            {
                result.AppendLine($"""
                                   🕒 {item.StartTime[..5]}-{item.EndTime[..5]}
                                   📚 <i>{item.SubjectName}</i>
                                   🏫 Ауд. {item.Classroom}
                                   👨🏫 {item.TeacherName}
                                   🔢 {FormatLessonType(item.LessonType)}
                                   ------------------
                                   """);
            }

            result.AppendLine();
        }

        return result.ToString();
    }

    private string FormatLessonType(string type) => type switch
    {
        "LECTURE" => "Лекция",
        "PRACTICAL" => "Практика",
        "LAB" => "Лабораторная",
        _ => "Занятие"
    };
    private string GetDeadlines()
    {
        if (_cache.TryGetValue("deadlines", out string cachedDeadlines))
        {
            return cachedDeadlines;
        }

        var deadlines = """
            <b>📝 Актуальные дедлайны:</b>
            
            1. Курсовая работа по оптике - 2024-05-25
            2. Лабораторная по динамике - 2024-05-30
            """;
        _cache.Set("deadlines", deadlines, GetTimeUntilMidnightUTC());
        return deadlines;
    }

    private string ProcessNotification(string message)
    {
        var reason = message.Length > "/notify".Length
            ? message["/notify".Length..].Trim()
            : null;

        return reason != null
            ? $"✅ Уведомление отправлено старосте:\n<code>{reason}</code>"
            : "❌ Укажите причину пропуска: /notify [причина]";
    }

    private async Task<string> ProcessBroadcast(string message)
    {
        var content = message.Length > "/broadcast".Length
            ? message["/broadcast".Length..].Trim()
            : null;

        if (string.IsNullOrEmpty(content))
            return "❌ Укажите сообщение для рассылки";

        var successCount = 0;
        foreach (var userId in _userChatIds)
        {
            try
            {
                await _botClient.SendTextMessageAsync(
                    userId,
                    $"📢 <b>Важное объявление:</b>\n{content}",
                    parseMode: ParseMode.Html
                );
                successCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to send to {userId}");
            }
        }
        return $"📤 Рассылка выполнена: {successCount}/{_userChatIds.Count} получателей";
    }

    private bool IsAdmin(long chatId) => _adminWhitelist.Contains(chatId);

    private TimeSpan GetTimeUntilMidnightUTC()
    {
        var now = DateTimeOffset.UtcNow;
        var midnight = now.Date.AddDays(1);
        var timeUntilMidnight = midnight - now;

        if (timeUntilMidnight <= TimeSpan.Zero)
        {
            timeUntilMidnight = timeUntilMidnight.Add(TimeSpan.FromDays(1));
        }

        return timeUntilMidnight;
    }

    private IReplyMarkup GetMainKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("Распписание на сегодня") },
            new[]{new KeyboardButton("Расписание на неделю") },
            new[] { new KeyboardButton("📝 Дедлайны"),
                new KeyboardButton("❓ Помощь") }
        })
        {
            ResizeKeyboard = true,
            Selective = true
        };
    }
}