using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

#region  ==== модели ====

record AdItem
{
    public int                        Id          { get; init; }
    public List<long>                 ThreadIds   { get; set; } = new();        // ID веток (topics)
    public string                     Text        { get; set; } = "";
    public TimeOnly[]                 Times       { get; set; } = Array.Empty<TimeOnly>();
    public Dictionary<long, DateOnly> LastPosted  { get; set; } = new();        // когда постили в каждом топике
}

#endregion

#region ==== репозиторий ====

class AdRepository
{
    private readonly string _file;
    private List<AdItem> _cache;

    public AdRepository(string file = "ads.json")
    {
        _file  = file;
        _cache = File.Exists(_file)
            ? JsonSerializer.Deserialize<List<AdItem>>(File.ReadAllText(_file)) ?? []
            : [];
    }

    public IReadOnlyList<AdItem> All() => _cache;

    public int NextId() => _cache.Count == 0 ? 1 : _cache.Max(a => a.Id) + 1;

    public void Add(AdItem ad)               { _cache.Add(ad);  Save(); }
    public void Update(AdItem ad)            { Save(); }
    public void Remove(int id)               { _cache.RemoveAll(a => a.Id == id);  Save(); }

    private void Save() =>
        File.WriteAllText(_file, JsonSerializer.Serialize(_cache, new() { WriteIndented = true }));
}

#endregion

#region ==== бот ====

class Bot
{
    private readonly TelegramBotClient _bot;
    private readonly AdRepository      _repo = new();
    private readonly string[]          _admins =
        { "metamodernismus", "PetrK19" };                // <<< ваши юзернеймы

    // карта состояний для «мастера» создания/редактирования
    private readonly Dictionary<long, State> _state = new();
    private readonly Dictionary<long, AdItem> _draft = new();

    // список доступных топиков канала: «псевдоним» → thread_id
    private readonly Dictionary<string,long> _topics = new()
    {
        ["Sound"]   = 7,
        ["Light"]   = 11,
        ["Video"]   = 15,
        ["Kitchen"] = 23
    };

    enum State { None, WaitTopics, WaitTimes, WaitText, EditingText }

    public Bot(string token) => _bot = new(token);

    public void Run()
    {
        // планировщик: каждую минуту CheckSchedule
        var _ = new Timer(_ => CheckSchedule(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

        _bot.StartReceiving(HandleUpdate,
                            HandleErr,
                            new ReceiverOptions { AllowedUpdates = [UpdateType.Message] });

        Console.WriteLine("Bot started.");
        Console.ReadLine();
    }

    #region ==== постинг ====

    private void CheckSchedule()
    {
        var now   = TimeOnly.FromDateTime(DateTime.Now);
        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var ad in _repo.All())
        {
            if (!ad.Times.Contains(now)) continue;

            foreach (var th in ad.ThreadIds)
            {
                if (ad.LastPosted.TryGetValue(th, out var d) && d == today)
                    continue;

                try
                {
                    _bot.SendTextMessageAsync(th, ad.Text, ParseMode.Html).Wait();
                    ad.LastPosted[th] = today;
                    _repo.Update(ad);
                }
                catch (Exception ex) { Console.WriteLine(ex.Message); }
            }
        }
    }

    #endregion

    #region ==== обработка команд ====

    private async Task HandleUpdate(ITelegramBotClient _, Update u, CancellationToken ct)
    {
        if (u.Message is not { } m || m.From?.Username is null) return;
        if (!_admins.Contains(m.From.Username)) return;          // не-админ => молчим

        var chat = m.Chat.Id;
        var txt  = m.Text?.Trim() ?? "";

        // 1) пользователь в «мастере»?
        if (_state.TryGetValue(chat, out var s) && s != State.None)
        {
            await HandleWizard(chat, txt, s, ct); return;
        }

        // 2) обычные one-shot команды
        var parts = txt.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var cmd   = parts[0].ToLower();

        switch (cmd)
        {
            case "/add":  StartWizard(chat);                            break;
            case "/list": await SendList(chat, ct);                     break;
            case "/del":  await Delete(parts, chat, ct);                break;
            case "/post": await PostNow(parts, chat, ct);               break;
            case "/edit": StartEdit(parts, chat);                       break;
            case "/time": await ChangeTime(parts, chat, ct);            break;
        }
    }

    private Task HandleErr(ITelegramBotClient b, Exception e, CancellationToken c)
    {
        Console.WriteLine(e); return Task.CompletedTask;
    }

    #endregion

    #region ==== команды ====

    private void StartWizard(long chat)
    {
        _draft[chat] = new AdItem { Id = _repo.NextId() };
        _state[chat] = State.WaitTopics;
        _bot.SendTextMessageAsync(chat,
            "Шаг 1/3: выберите топики:\n" + TopicsChecklist(chat));
    }

    private async Task HandleWizard(long chat, string txt, State s, CancellationToken ct)
    {
        var d = _draft[chat];

        switch (s)
        {
            case State.WaitTopics:
                if (ToggleTopics(d, txt))
                {
                    await _bot.SendTextMessageAsync(chat, TopicsChecklist(chat));
                }
                else if (txt.Equals("далее", StringComparison.OrdinalIgnoreCase))
                {
                    if (d.ThreadIds.Count == 0)
                    {
                        await _bot.SendTextMessageAsync(chat, "Выберите хотя бы один топик");
                    }
                    else
                    {
                        _state[chat] = State.WaitTimes;
                        await _bot.SendTextMessageAsync(chat,
                            "Шаг 2/3: время (через пробел, 24 ч, HH:mm)\nнапр. `09:00 15:00`",
                            parseMode: ParseMode.Markdown);
                    }
                }
                else
                    await _bot.SendTextMessageAsync(chat, TopicsChecklist(chat));
                break;

            case State.WaitTimes:
                if (!TimeOnly.TryParseExact(txt, "HH:mm", out _))
                {
                    var tokens = txt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var times  = tokens.Select(t => TimeOnly.TryParseExact(t,"HH:mm",out var t2)?t2:null)
                                       .Where(t => t!=null).Select(t=>t!.Value).ToArray();
                    if (times.Length == 0)
                    {
                        await _bot.SendTextMessageAsync(chat,"Неверный формат. Пример: `09:00 15:00`",
                                                        parseMode: ParseMode.Markdown);
                        break;
                    }
                    d.Times = times;  _state[chat] = State.WaitText;
                    await _bot.SendTextMessageAsync(chat, "Шаг 3/3: пришлите текст объявления");
                }
                break;

            case State.WaitText:
                if (string.IsNullOrWhiteSpace(txt))
                    await _bot.SendTextMessageAsync(chat,"Текст пустой, пришлите снова");
                else
                {
                    d.Text = txt;
                    _repo.Add(d);
                    _state[chat] = State.None; _draft.Remove(chat);
                    await _bot.SendTextMessageAsync(chat, "✅ Объявление сохранено");
                }
                break;

            case State.EditingText:
                d.Text = txt;
                _repo.Update(d);
                _state[chat] = State.None;
                await _bot.SendTextMessageAsync(chat,"Текст обновлён");
                break;
        }
    }

    private async Task SendList(long chat, CancellationToken ct)
    {
        if (_repo.All().Count == 0)
        {
            await _bot.SendTextMessageAsync(chat,"Объявлений нет", cancellationToken: ct); return;
        }

        var sb = new StringBuilder("```\nID  Threads         Время       Текст\n");
        foreach (var a in _repo.All())
        {
            var t = string.Join(',', a.Times.Select(x => x.ToString("HH:mm")));
            var th = string.Join(',', a.ThreadIds);
            sb.Append($"{a.Id,-3} {th,-14} {t,-10} {Truncate(a.Text)}\n");
        }
        sb.Append("```");
        await _bot.SendTextMessageAsync(chat, sb.ToString(),
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task Delete(string[] parts, long chat, CancellationToken ct)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id))
        {
            await _bot.SendTextMessageAsync(chat,"Usage: /del <id>", cancellationToken: ct); return;
        }
        _repo.Remove(id);
        await _bot.SendTextMessageAsync(chat,"Удалено", cancellationToken: ct);
    }

    private async Task PostNow(string[] parts,long chat, CancellationToken ct)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id))
        {
            await _bot.SendTextMessageAsync(chat,"Usage: /post <id>", cancellationToken: ct); return;
        }
        var ad = _repo.All().FirstOrDefault(a => a.Id == id);
        if (ad is null) { await _bot.SendTextMessageAsync(chat,"Нет такого id"); return; }

        foreach (var th in ad.ThreadIds)
            await _bot.SendTextMessageAsync(th, ad.Text, parseMode: ParseMode.Html, cancellationToken: ct);
        await _bot.SendTextMessageAsync(chat, "Отправлено", cancellationToken: ct);
    }

    private void StartEdit(string[] parts,long chat)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var id))
        {
            _bot.SendTextMessageAsync(chat,"Usage: /edit <id>"); return;
        }
        var ad = _repo.All().FirstOrDefault(a => a.Id == id);
        if (ad is null) { _bot.SendTextMessageAsync(chat,"Нет такого id"); return; }

        _draft[chat] = ad;
        _state[chat] = State.EditingText;
        _bot.SendTextMessageAsync(chat,"Пришлите новый текст\n\n—-------\n⚠️ старый:\n"+ad.Text);
    }

    private async Task ChangeTime(string[] parts,long chat,CancellationToken ct)
    {
        if (parts.Length < 3 || !int.TryParse(parts[1], out var id))
        {
            await _bot.SendTextMessageAsync(chat,"Usage: /time <id> HH:mm HH:mm",cancellationToken: ct); return;
        }
        var ad = _repo.All().FirstOrDefault(a => a.Id == id);
        if (ad is null) { await _bot.SendTextMessageAsync(chat,"Нет такого id"); return; }

        var times = parts.Skip(2)
                         .Select(t => TimeOnly.TryParseExact(t,"HH:mm",out var t2)?t2:null)
                         .Where(t => t!=null).Select(t=>t!.Value).ToArray();
        if (times.Length == 0)
        {
            await _bot.SendTextMessageAsync(chat,"Формат времени HH:mm",cancellationToken: ct); return;
        }
        ad.Times = times; _repo.Update(ad);
        await _bot.SendTextMessageAsync(chat,"Время обновлено",cancellationToken: ct);
    }

    #endregion

    #region ==== утилиты ====

    private string TopicsChecklist(long chat)
    {
        var d = _draft[chat];
        var sb = new StringBuilder("Нажимайте, чтобы включать/выключать:\n");
        foreach (var kv in _topics)
        {
            var mark = d.ThreadIds.Contains(kv.Value) ? "✅" : "❌";
            sb.AppendLine($"{mark} {kv.Key}");
        }
        sb.Append("\n«Далее» — переход к выбору времени");
        return sb.ToString();
    }

    private bool ToggleTopics(AdItem d, string txt)
    {
        if (_topics.TryGetValue(txt, out var id))
        {
            if (d.ThreadIds.Contains(id))
                d.ThreadIds.Remove(id);
            else
                d.ThreadIds.Add(id);
            return true;
        }
        return false;
    }

    private static string Truncate(string s, int len = 20) =>
        s.Length <= len ? s : s[..len] + "…";

    #endregion
}

#endregion

class Program
{
    static void Main()
    {
        var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
                    ?? throw new("env BOT_TOKEN not set");
        new Bot(token).Run();
    }
}
