

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

// Конфигурация
var botToken = "8107055966:AAEyU-mnIvNK-J2hDxQJ3bno1z5PAiHCf7Q";
var apiUrl = "https://telegram-bot-starosta-backend.onrender.com/api/v1";
var adminWhitelist = new List<long> { 1563759837, 960762871 };
var userChatIds = new HashSet<long>();
var httpClient = new HttpClient();

// Инициализация
var botClient = new TelegramBotClient(botToken);
var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<Program>();

try
{
    logger.LogInformation("Starting bot...");
    
    // Настройка обработки сообщений
    botClient.StartReceiving(
        updateHandler: HandleUpdateAsync,
        HandlePollingErrorAsync,
        receiverOptions: new ReceiverOptions // Исправлено: receiver0ptio → receiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message }, // Корректный синтаксис массива
        }
    );

    logger.LogInformation("Bot started. Press Ctrl+C to exit");
    await Task.Delay(-1); // Бесконечное ожидание
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Fatal error occurred");
}

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
{
    try
    {
        if (update.Message is not { Text: { } messageText, Chat: { } chat }) return;

        logger.LogInformation($"Received: '{messageText}' from {chat.Id}");
        userChatIds.Add(chat.Id);

        var response = await ProcessCommand(messageText, chat.Id);
        await botClient.SendTextMessageAsync(chat.Id, response, parseMode: ParseMode.Html);
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
    return message.Split(' ')[0].ToLower() switch
    {
        "/start" => GetWelcomeMessage(),
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

async Task<string> GetSchedule()
{
    try
    {
        var response = await httpClient.GetAsync($"{apiUrl}/currentDay?groupName=М3О-303С-22");
        if (!response.IsSuccessStatusCode) return "❌ Ошибка получения расписания";

        var json = await response.Content.ReadAsStringAsync();
        var schedule = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
        
        return FormatSchedule(schedule ?? new List<ScheduleItem>());
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Schedule error");
        return "⏳ Сервис расписания временно недоступен";
    }
}

string FormatSchedule(List<ScheduleItem> schedule)
{
    if (schedule.Count == 0) return "📭 Расписание на сегодня отсутствует";

    return schedule.Aggregate("<b>📅 Расписание на сегодня:</b>\n\n", (current, item) => current + $"""
        📚 <i>{item.SubjectName}</i>
        🕒 {item.StartTime[..5]}-{item.EndTime[..5]}
        🏫 Ауд. {item.Classroom}
        👨🏫 {item.TeacherName}
        🔢 {FormatLessonType(item.LessonType)}
        ------------------
        """);
}

string FormatLessonType(string type) => type switch
{
    "LECTURE" => "Лекция",
    "PRACTICAL" => "Практика",
    _ => "Занятие"
};

string GetDeadlines() => """
    <b>📝 Актуальные дедлайны:</b>
    
    1. Курсовая работа по оптике - 2024-05-25
    2. Лабораторная по динамике - 2024-05-30
    """;

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