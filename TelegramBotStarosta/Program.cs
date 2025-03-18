using System.Text.Json;
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
    logger.LogInformation("Starting bot..."); // Убран лишний параметр message
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

        var response = await ProcessCommand(messageText, chat.Id);
        await botClient.SendTextMessageAsync(chat.Id, response);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error handling message");
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
{
    var errorMessage = exception switch
    {
        ApiRequestException apiEx => $"Telegram API Error: {apiEx.ErrorCode} - {apiEx.Message}",
        _ => exception.ToString()
    };

    logger.LogError(errorMessage);
    return Task.CompletedTask;
}

async Task<string> ProcessCommand(string message, long chatId)
{
    userChatIds.Add(chatId);

    return message.Split(' ')[0] switch
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
    🎓 Бот старосты группы М3О-303С-22
    
    Доступные команды:
    /schedule - Расписание на сегодня
    /deadlines - Актуальные дедлайны
    /notify [причина] - Уведомить о пропуске
    /help - Справка по командам
    """;

string GetHelpMessage(bool isAdmin) => isAdmin 
    ? """
      👑 Админ-команды:
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
        
        return FormatSchedule(schedule);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Schedule error");
        return "⏳ Сервис расписания временно недоступен";
    }
}

string FormatSchedule(List<ScheduleItem> schedule)
{
    if (schedule?.Count == 0) return "📭 Расписание на сегодня отсутствует";

    return schedule!.Aggregate("📅 Расписание на сегодня:\n\n", (current, item) => current + $"""
        📚 {item.SubjectName}
        🕒 {item.StartTime[..5]}-{item.EndTime[..5]}
        🏫 Ауд. {item.Classroom}
        👨🏫 {item.TeacherName}
        🔢 {item.LessonType switch {
            "LECTURE" => "Лекция",
            "PRACTICAL" => "Практика",
            _ => "Занятие"
        }}
        ------------------
        """);
}

string GetDeadlines() => """
    📝 Актуальные дедлайны:
    
    1. Курсовая работа по оптике - 2024-05-25
    2. Лабораторная по динамике - 2024-05-30
    """;

string ProcessNotification(string message) => 
    message.Length > "/notify".Length 
        ? $"✅ Уведомление отправлено: {message["/notify".Length..].Trim()}"
        : "❌ Укажите причину пропуска: /notify [причина]";

async Task<string> ProcessBroadcast(string message)
{
    if (message.Length <= "/broadcast".Length) 
        return "❌ Укажите сообщение для рассылки";

    var broadcastMessage = message["/broadcast".Length..].Trim();
    var successCount = 0;

    foreach (var userId in userChatIds)
    {
        try
        {
            await botClient.SendTextMessageAsync(userId, $"📢 Важное объявление:\n{broadcastMessage}");
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
    public string SubjectName { get; set; } = null!;
    public string StartTime { get; set; } = null!;
    public string EndTime { get; set; } = null!;
    public string Classroom { get; set; } = null!;
    public string TeacherName { get; set; } = null!;
    public string LessonType { get; set; } = null!;
}