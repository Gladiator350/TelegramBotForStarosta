using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Polly;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Net;
using System.Web;
using Telegram.Bot.Types.ReplyMarkups;

// Конфигурация
var botToken = "8107055966:AAEyU-mnIvNK-J2hDxQJ3bno1z5PAiHCf7Q";
var apiUrl = "https://telegram-bot-starosta-backend.onrender.com/api/v1/schedule";
var adminWhitelist = new List<long> { 1563759837, 960762871 };
var userChatIds = new HashSet<long>();
var httpClient = new HttpClient();

// Создаем in‑memory кэш
var cache = new MemoryCache(new MemoryCacheOptions());

// Параметры ограничения повторного использования команды (например, 60 секунд)
const int CooldownSeconds = 60;
// Словарь для отслеживания времени последнего использования команды каждым пользователем
var lastCommandUsage = new ConcurrentDictionary<(long chatId, string command), DateTime>();

var botClient = new TelegramBotClient(botToken);
var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<Program>();
var cts = new CancellationTokenSource();

// Метод для создания основной клавиатуры
IReplyMarkup GetMainKeyboard()
{
    
    return new ReplyKeyboardMarkup(new[]
    {
        new[] { new KeyboardButton("/schedule") },
        new[]{new KeyboardButton("/scheduleWeek") },
        new[] { new KeyboardButton("📝 Дедлайны"), new KeyboardButton("❓ Помощь") }
    })
    {
        ResizeKeyboard = true,
        Selective = true // Показывает только тем, кто отправил команду
    };
}


try
{
    logger.LogInformation("Starting bot...");
    var updateTask = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            await UpdateScheduleAsync();
            await Task.Delay(TimeSpan.FromMinutes(10));
        }
    });
    // Настройка обработки сообщений
    botClient.StartReceiving(
        updateHandler: HandleUpdateAsync,
        HandlePollingErrorAsync,
        receiverOptions: new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message },
        }
    );

    logger.LogInformation("Bot started. Press Ctrl+C to exit");
    await Task.Delay(-1); // Бесконечное ожидание
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Fatal error occurred");
}

async Task UpdateScheduleAsync()
{
    try
    {
        logger.LogInformation("⏳ Обновление расписания...");

        string groupName = "М3О-303С-22";
        string encodedGroupName = HttpUtility.UrlEncode(groupName);
        
        // Политика повтора для запроса
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        var response = await retryPolicy.ExecuteAsync(async () =>
        {
            return await httpClient.GetAsync($"{apiUrl}/currentDay?groupName={encodedGroupName}");
        });

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var schedule = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
        string formattedSchedule = FormatSchedule(schedule ?? new List<ScheduleItem>());

        // Обновляем кэш
        cache.Set("schedule", formattedSchedule, GetTimeUntilMidnightUTC());
        
        logger.LogInformation("✅ Расписание обновлено");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "❌ Ошибка обновления расписания через таймер");
    }
}
async Task UpdateScheduleWeekAsync()
{
    try
    {
        logger.LogInformation("⏳ Обновление расписания...");

        string groupName = "М3О-303С-22";
        string encodedGroupName = HttpUtility.UrlEncode(groupName);
        
        // Политика повтора для запроса
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        var response = await retryPolicy.ExecuteAsync(async () =>
        {
            return await httpClient.GetAsync($"{apiUrl}/week?groupName={encodedGroupName}");
        });

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var schedule = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
        string formattedSchedule = FormatSchedule(schedule ?? new List<ScheduleItem>());

        // Обновляем кэш
        cache.Set("schedule", formattedSchedule, GetTimeUntilMidnightUTC());
        
        logger.LogInformation("✅ Расписание обновлено");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "❌ Ошибка обновления расписания через таймер");
    }
}
async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
{
    try
    {
        if (update.Message is not { Text: { } messageText, Chat: { } chat }) return;

        logger.LogInformation($"Received: '{messageText}' from {chat.Id}");
        userChatIds.Add(chat.Id);

        var response = await ProcessCommand(messageText, chat.Id);
        // Отправляем ответ с клавиатурой
        await botClient.SendTextMessageAsync(
            chat.Id,
            response,
            parseMode: ParseMode.Html,
            replyMarkup: GetMainKeyboard() // Добавляем клавиатуру
        );
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error handling message");
    }
}

async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
    var errorMessage = exception switch
    {
        ApiRequestException apiEx => $"Telegram API Error ({apiEx.ErrorCode}): {apiEx.Message}",
        _ => exception.ToString()
    };

    logger.LogError(errorMessage);
}

async Task<string> ProcessCommand(string message, long chatId)
{
    var command = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
    
    // Ограничиваем выполнение команды /schedule не чаще одного раза за CooldownSeconds
    if (command == "/schedule")
    {
        if (lastCommandUsage.TryGetValue((chatId, command), out var lastUsage))
        {
            var nextAllowedTime = lastUsage.AddSeconds(CooldownSeconds);
            if (false/*DateTime.UtcNow < nextAllowedTime*/)
            {
                var remaining = (int)(nextAllowedTime - DateTime.UtcNow).TotalSeconds;
                return $"⏳ Пожалуйста, подождите {remaining} секунд перед повторным использованием команды.";
            }
        }
        lastCommandUsage[(chatId, command)] = DateTime.UtcNow;
    }

    return command switch
    {
        "/start" => GetWelcomeMessage(),
        "/scheduleWeek" => await GetScheduleWeek(),
        "📝 дедлайны" => GetDeadlines(),
        "❓ помощь" => GetHelpMessage(IsAdmin(chatId)),
        "/help" => GetHelpMessage(IsAdmin(chatId)),
        "/schedule" => await GetSchedule(),
        "/deadlines" => GetDeadlines(),
        "/notify" => ProcessNotification(message),
        "/broadcast" when IsAdmin(chatId) => await ProcessBroadcast(message),
        _ => "⚠️ Неизвестная команда. Используйте /help"
    };
}

string GetWelcomeMessage() => """
    <b>🎓 Бот старосты группы М3О-303С-22</b>
    
    <i>Доступные команды:</i>
    /schedule - Расписание на сегодня
    /deadlines - Актуальные дедлайны
    /notify [причина] - Уведомить о пропуске
    /help - Справка по командам
    """;


string GetHelpMessage(bool isAdmin) => isAdmin 
    ? """
      <b>👑 Админ-команды:</b>
      /broadcast [сообщение] - Рассылка всем пользователям
      
      """ + GetWelcomeMessage()
    : GetWelcomeMessage();

// Вспомогательная функция для вычисления времени до полуночи (UTC)
TimeSpan GetTimeUntilMidnightUTC()
{
    var now = DateTimeOffset.UtcNow;
    return now.Date.AddDays(1) - now;
}

async Task<string> GetSchedule()
{
    // Проверяем кэш по ключу "schedule"
    if (cache.TryGetValue("schedule", out string cachedSchedule))
    {
        return cachedSchedule;
    }
    var keyboard = new InlineKeyboardMarkup(new[]
    {
        new []
        {
            InlineKeyboardButton.WithCallbackData("Обновить расписание", "refresh_schedule")
        }
    });
    
    var retryPolicy = Policy
        .Handle<HttpRequestException>()
        .WaitAndRetryAsync(3, retryAttempt => 
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

    string scheduleString = await retryPolicy.ExecuteAsync(async () =>
    {
        string groupName = "М3О-303С-22";
        string encodedGroupName = HttpUtility.UrlEncode(groupName);
        var response = await httpClient.GetAsync($"{apiUrl}/currentDay?groupName={encodedGroupName}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var schedule = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
        return FormatSchedule(schedule ?? new List<ScheduleItem>());
    });

    // Сохраняем результат в кэше до полуночи
    cache.Set("schedule", scheduleString, GetTimeUntilMidnightUTC());
    return scheduleString;
}
async Task<string> GetScheduleWeek()
{
    // Проверяем кэш по ключу "schedule"
    if (cache.TryGetValue("scheduleWeek", out string cachedSchedule))
    {
        return cachedSchedule;
    }
    var keyboard = new InlineKeyboardMarkup(new[]
    {
        new []
        {
            InlineKeyboardButton.WithCallbackData("Обновить расписание", "refresh_schedule")
        }
    });
    
    var retryPolicy = Policy
        .Handle<HttpRequestException>()
        .WaitAndRetryAsync(3, retryAttempt => 
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

    string scheduleString = await retryPolicy.ExecuteAsync(async () =>
    {
        string groupName = "М3О-303С-22";
        string encodedGroupName = HttpUtility.UrlEncode(groupName);
        var response = await httpClient.GetAsync($"{apiUrl}/week?groupName={encodedGroupName}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var schedule = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
        return FormatSchedule(schedule ?? new List<ScheduleItem>());
    });

    // Сохраняем результат в кэше до полуночи
    cache.Set("scheduleWeek", scheduleString, GetTimeUntilMidnightUTC());
    return scheduleString;
}
string FormatSchedule(List<ScheduleItem> schedule)
{
    if (schedule.Count == 0) return "📭 Расписание на неделю отсутствует";

    return schedule.Aggregate("<b>📅 Расписание на неделю:</b>\n\n", (current, item) => current + $"""
        📚 <i>{item.SubjectName}</i>
        🕒 {item.StartTime[..5]}-{item.EndTime[..5]}
        🏫 Ауд. {item.Classroom}
        👨🏫 {item.TeacherName}
        🔢 {FormatLessonType(item.LessonType)}
        ------------------
        """ + "\n");
}

string FormatLessonType(string type) => type switch
{
    "LECTURE" => "Лекция",
    "PRACTICAL" => "Практика",
    _ => "Занятие"
};

string GetDeadlines()
{
    // Проверяем кэш по ключу "deadlines"
    if (cache.TryGetValue("deadlines", out string cachedDeadlines))
    {
        return cachedDeadlines;
    }
    
    var deadlines = """
    <b>📝 Актуальные дедлайны:</b>
    
    1. Курсовая работа по оптике - 2024-05-25
    2. Лабораторная по динамике - 2024-05-30
    """;
    // Кэшируем до полуночи
    cache.Set("deadlines", deadlines, GetTimeUntilMidnightUTC());
    return deadlines;
}

string ProcessNotification(string message)
{
    var reason = message.Length > "/notify".Length 
        ? message["/notify".Length..].Trim()
        : null;

    return reason != null 
        ? $"✅ Уведомление отправлено старосте:\n<code>{reason}</code>" 
        : "❌ Укажите причину пропуска: /notify [причина]";
}

async Task<string> ProcessBroadcast(string message)
{
    var content = message.Length > "/broadcast".Length 
        ? message["/broadcast".Length..].Trim()
        : null;

    if (string.IsNullOrEmpty(content)) 
        return "❌ Укажите сообщение для рассылки";

    var successCount = 0;
    foreach (var userId in userChatIds)
    {
        try
        {
            await botClient.SendTextMessageAsync(
                userId, 
                $"📢 <b>Важное объявление:</b>\n{content}",
                parseMode: ParseMode.Html
            );
            successCount++;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to send to {userId}");
        }
    }
    return $"📤 Рассылка выполнена: {successCount}/{userChatIds.Count} получателей";
}

bool IsAdmin(long chatId) => adminWhitelist.Contains(chatId);


public class ScheduleItem
{
    [JsonPropertyName("subjectName")]
    public string SubjectName { get; set; } = null!;
    
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = null!;
    
    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = null!;
    
    [JsonPropertyName("classroom")]
    public string Classroom { get; set; } = null!;
    
    [JsonPropertyName("teacherName")]
    public string TeacherName { get; set; } = null!;
    
    [JsonPropertyName("lessonType")]
    public string LessonType { get; set; } = null!;
}
