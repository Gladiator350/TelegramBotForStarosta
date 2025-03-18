using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

// Классы для десериализации JSON
var botClient = new TelegramBotClient("8107055966:AAEyU-mnIvNK-J2hDxQJ3bno1z5PAiHCf7Q");
var userChatIds = new List<long>();

// Замените на ваш Chat ID (получите его через код или @userinfobot)
long YOUR_CHAT_ID = 1563759837; // Пример Chat ID
var adminWhiteList = new List<long>
{
    1563759837, // Пример Chat ID старосты
    960762871  // Пример Chat ID другого админа
};

// Пример данных о дедлайнах (можно заменить на запрос к API)
var deadlines = new List<DeadlineItem>
{
    new DeadlineItem { TaskName = "Лабораторная работа по динамике полета", DueDate = "2025-03-20" },
    new DeadlineItem { TaskName = "Курсовой проект по оптике", DueDate = "2025-03-25" }
};

botClient.StartReceiving(UpdateHandler, ErrorHandler);
var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<Program>();
logger.LogInformation("Приложение запущено");

async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
    if (update.Type == UpdateType.Message)
    {
        var message = update.Message;
        Console.WriteLine($"Получено сообщение: {message.Text}, {message.Chat.Id}");
        if (!userChatIds.Contains(message.Chat.Id))
        {
            userChatIds.Add(message.Chat.Id);
        }

        if (message.Text == "/start")
        {
            await botClient.SendTextMessageAsync(message.Chat.Id, "Привет! Я бот старосты.\n\n" +
                                                                  "Доступные команды:\n" +
                                                                  "/расписание - Показать расписание\n" +
                                                                  "/notify [причина] - Уведомить о пропуске пары\n" +
                                                                  "/deadlines - Показать дедлайны\n" +
                                                                  "/help - Показать список команд");
        }
        else if (message.Text == "/help")
        {
            await ShowHelp(message.Chat.Id, IsAdmin(message.Chat.Id));
        }
        else if (message.Text == "/расписание")
        {
            var scheduleJson = await GetScheduleAsync();
            var formattedSchedule = FormatSchedule(scheduleJson);
            await botClient.SendTextMessageAsync(message.Chat.Id, formattedSchedule);
        }
        else if (message.Text.StartsWith("/notify"))
        {
            var reason = message.Text.Replace("/notify", "").Trim();
            if (string.IsNullOrEmpty(reason))
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, "Укажите причину пропуска.");
            }
            else
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, $"Ваше уведомление отправлено: {reason}");
                // Здесь можно добавить логику отправки уведомления старосте
            }
        }
        else if (message.Text == "/deadlines")
        {
            var formattedDeadlines = FormatDeadlines(deadlines);
            await botClient.SendTextMessageAsync(message.Chat.Id, formattedDeadlines);
        }
        else if (message.Text.StartsWith("/broadcast"))
        {
            if (IsAdmin(message.Chat.Id))
            {
                var broadcastMessage = message.Text.Replace("/broadcast", "").Trim();
                if (string.IsNullOrEmpty(broadcastMessage))
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Укажите сообщение для рассылки.");
                }
                else
                {
                    // Рассылка сообщения всем пользователям
                    await BroadcastMessage(broadcastMessage);
                    await botClient.SendTextMessageAsync(message.Chat.Id, $"Рассылка выполнена: {broadcastMessage}");
                }
            }
            else
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, "У вас нет доступа к этой команде.");
            }
        }
        else
        {
            // Если команда не распознана
            await botClient.SendTextMessageAsync(message.Chat.Id, "Неизвестная команда. Используйте /help для списка команд.");
        }
    }
}

Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
    Console.WriteLine($"Ошибка: {exception.Message}");
    return Task.CompletedTask;
}

async Task<string> GetScheduleAsync()
{
    using (var client = new HttpClient())
    {
        var response = await client.GetAsync("https://telegram-bot-starosta-backend.onrender.com/api/v1/schedule/currentDay?groupName=М3О-303С-22");
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Ошибка: {response.StatusCode}");
            return null;
        }
        var json = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"Полученный JSON: {json}");
        return json;
    }
}

string FormatSchedule(string json)
{
    if (string.IsNullOrEmpty(json))
    {
        return "Не удалось загрузить расписание.";
    }

    try
    {
        var scheduleItems = JsonSerializer.Deserialize<List<ScheduleItem>>(json);
        if (scheduleItems == null || scheduleItems.Count == 0)
        {
            return "Расписание пусто.";
        }

        var formattedSchedule = "📅 Расписание:\n\n";
        foreach (var item in scheduleItems)
        {
            var startTime = item.StartTime[..5]; // Более короткий синтаксис
            var endTime = item.EndTime[..5];

            // Перевод LessonType
            string lessonType = item.LessonType switch
            {
                "LECTURE" => "Лекция",
                "PRACTICAL" => "Практика",
                "LAB" => "Лабораторная работа",
                _ => item.LessonType // Если тип неизвестен
            };

            formattedSchedule += $@"📚 {item.SubjectName}
👨‍🏫 Преподаватель: {item.TeacherName}
🏫 Аудитория: {item.Classroom}
🕒 Время: {startTime} - {endTime}
📅 Дата: {item.LessonDate}
🔢 Тип занятия: {lessonType}
——————————
";
        }

        return formattedSchedule;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при десериализации: {ex.Message}");
        return "Ошибка при обработке расписания.";
    }
}

string FormatDeadlines(List<DeadlineItem> deadlines)
{
    if (deadlines == null || deadlines.Count == 0)
    {
        return "Дедлайнов нет.";
    }

    var formattedDeadlines = "📝 Дедлайны:\n\n";
    foreach (var deadline in deadlines)
    {
        formattedDeadlines += $"📌 {deadline.TaskName}\n";
        formattedDeadlines += $"📅 Срок: {deadline.DueDate}\n";
        formattedDeadlines += "——————————\n";
    }

    return formattedDeadlines;
}

async Task ShowHelp(long chatId, bool isAdmin)
{
    var helpMessage = "📋 Доступные команды:\n\n" +
                      "/schedule - Показать расписание\n" +
                      "/notify [причина] - Уведомить о пропуске пары\n" +
                      "/deadlines - Показать дедлайны\n" +
                      "/help - Показать список команд\n";

    if (isAdmin)
    {
        helpMessage += "/broadcast [сообщение] - Рассылка информации (только для старосты)\n";
    }

    await botClient.SendTextMessageAsync(chatId, helpMessage);
}

Console.ReadLine();
bool IsAdmin(long chatId)
{
    return adminWhiteList.Contains(chatId);
}

async Task BroadcastMessage(string message)
{
    foreach (var chatId in userChatIds)
    {
        try
        {
            await botClient.SendTextMessageAsync(chatId, $"📢 Рассылка: {message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при отправке сообщения пользователю {chatId}: {ex.Message}");
        }
    }
}

public class ScheduleItem
{
    [JsonPropertyName("groupName")]
    public string GroupName { get; set; }

    [JsonPropertyName("subjectName")]
    public string SubjectName { get; set; }

    [JsonPropertyName("lessonType")]
    public string LessonType { get; set; }

    [JsonPropertyName("teacherName")]
    public string TeacherName { get; set; }

    [JsonPropertyName("classroom")]
    public string Classroom { get; set; }

    [JsonPropertyName("lessonDate")]
    public string LessonDate { get; set; }

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; }
}

public class DeadlineItem
{
    public string TaskName { get; set; }
    public string DueDate { get; set; }
}