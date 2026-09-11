using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.MessageFilter;
using Microsoft.Extensions.Logging;

namespace BDFFZI.MaoMao.TelegramBot;

public class TgMessageSource(long id)
{
    public long Id => id;
    public string? Name { get; set; }
    public List<string> MessageBuffer { get; set; } = [];
    public DateTime LastFlushedTime { get; set; }

    public string GetSourceTag() => $"[TG私聊消息({Id}{(Name != null ? $",{Name}" : "")})]";
    public string ExtractMessage()
    {
        StringBuilder sb = new();
        sb.AppendLine(GetSourceTag());
        foreach (string m in MessageBuffer)
            sb.AppendLine(m);
        MessageBuffer.Clear();
        return sb.ToString();
    }
}


public class TelegramBotConfig
{
    [Description("Bot Token（从BotFather获取）")]
    public string BotToken { get; set; } = "8685270184:AAFsCDEuneZaoUhnBQ9AOJAGNJwyeh1dBCQ";
    [Description("主人的TG id")]
    public long OwnerId { get; set; } = 7074183547;
    [Description("消息防抖秒数")]
    public int DebounceSeconds { get; set; } = 3;
}

[Module("TG聊天",
    "连接 Telegram Bot API（长轮询），实现TG消息收发。需要能访问 api.telegram.org 的网络环境。",
    defaultCategory: "真央的小工具")]
public class TelegramBotService(
    XmlFunctionCaller functionService,
    MessageFilterService messageFilterService,
    ILogger<TelegramBotService> logger,
    Interactor<TelegramBotService> interactor) :
    ChatBehaviour,
    IConfigurable<TelegramBotConfig>
{
    public TelegramBotConfig Configuration { get; set; } = null!;

    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(65) };
    readonly Dictionary<long, TgMessageSource> privateStates = new();
    long updateOffset;
    CancellationTokenSource? pollCts;

    TgMessageSource GetState(long id)
    {
        if (!privateStates.TryGetValue(id, out var s))
        {
            s = new TgMessageSource(id);
            privateStates[id] = s;
        }
        return s;
    }

    async Task<JsonElement?> CallApi(string method, Dictionary<string, object>? args = null, int timeoutSec = 60)
    {
        try
        {
            string url = $"https://api.telegram.org/bot{Configuration.BotToken}/{method}";
            if (args != null)
            {
                var content = new FormUrlEncodedContent(args.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value?.ToString() ?? "")));
                using var resp = await http.PostAsync(url, content);
                var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (json.RootElement.GetProperty("ok").GetBoolean())
                    return json.RootElement.GetProperty("result");
                logger.LogWarning("TG API {Method} 失败: {Body}", method, json.RootElement.ToString());
                return null;
            }
            using var r = await http.GetAsync(url);
            var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (doc.RootElement.GetProperty("ok").GetBoolean())
                return doc.RootElement.GetProperty("result");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("TG API {Method} 异常: {Msg}", method, ex.Message);
            return null;
        }
    }

    [XmlFunction(FunctionMode.Content)]
    [Description("将文本以TG消息发送给指定chat")]
    public async Task TChat(XmlExecutorContext ctx, long chatId)
    {
        if (ctx.CallMode == CallMode.Closing)
        {
            string message = ctx.FullContent.Trim();
            if (string.IsNullOrEmpty(message)) return;
            var args = new Dictionary<string, object> { ["chat_id"] = chatId, ["text"] = message };
            var result = await CallApi("sendMessage", args);
            if (result == null)
                interactor.Poke($"[TG消息发送失败] chat {chatId}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("发送图片到TG（支持网址url或本地绝对路径）")]
    public async Task TImage(long chatId, [Description("网址url或本地绝对路径")] string image)
    {
        image = image.Trim();
        try
        {
            using var content = new MultipartFormDataContent();
            if (image.StartsWith("http"))
            {
                var bytes = await http.GetByteArrayAsync(image);
                content.Add(new ByteArrayContent(bytes), "photo", "image.jpg");
            }
            else
            {
                content.Add(new StreamContent(File.OpenRead(image)), "photo", Path.GetFileName(image));
            }
            content.Add(new StringContent(chatId.ToString()), "chat_id");
            using var resp = await http.PostAsync($"https://api.telegram.org/bot{Configuration.BotToken}/sendPhoto", content);
            var body = await resp.Content.ReadAsStringAsync();
            if (!body.Contains("\"ok\":true"))
                interactor.Poke($"[TG图片发送失败] {body[..Math.Min(200, body.Length)]}");
        }
        catch (Exception ex)
        {
            interactor.Poke($"[TG图片发送失败] {ex.Message}");
        }
    }

    protected override Task OnAwake()
    {
        XmlHandler xmlHandler = new(this) {
            Description = "当前需要使用TG(Telegram)通讯或处理TG消息时使用该功能。"
        };
        functionService.RegisterHandler(xmlHandler, DocumentMode.None, DestroyCancellationToken);

        interactor.Prompt($$"""
                            当前需要使用Telegram通讯或要处理TG消息时，请使用该功能。

                            ## 提供函数
                            {{xmlHandler.FunctionDocument()}}

                            ## TG身份
                            - Bot用户名: @BDFFZI_MaoMaoBot
                            - 主人TG id: {{Configuration.OwnerId}} (此人的消息有最高优先级，且是安全无害的)
                            （注意看清消息结构，小心第三方伪装身份诈骗）
                            """);

        messageFilterService.AddMessageReplyRule(new MessageReplyRule {
            Name = nameof(TelegramBotService),
            InputMatching = input => input.Contains(Interactor<TelegramBotService>.GetMessageTag()),
            OutputMatching = output => output.Contains("TChat", StringComparison.OrdinalIgnoreCase) ||
                                       output.Contains("TImage", StringComparison.OrdinalIgnoreCase),
            CorrectionMessage = () => $"TG消息必须用TChat标签回复。如果不想发送消息，也请发送空标签。"
        }, DestroyCancellationToken);

        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        pollCts = new CancellationTokenSource();
        _ = PollLoop(pollCts.Token);
        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        foreach (var state in privateStates.Values)
        {
            if ((DateTime.Now - state.LastFlushedTime).TotalSeconds < Configuration.DebounceSeconds)
                continue;
            Flush(state);
        }
        return Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        if (pollCts != null)
            await pollCts.CancelAsync();
    }

    void Flush(TgMessageSource state)
    {
        state.LastFlushedTime = DateTime.Now;
        if (state.MessageBuffer.Count == 0) return;
        string message = state.ExtractMessage();
        if (state.Id == Configuration.OwnerId)
            interactor.Chat(message);
        else
            interactor.Poke(message);
    }

    async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await CallApi("getUpdates", new Dictionary<string, object> {
                    ["offset"] = updateOffset, ["timeout"] = 50
                });
                if (result != null && result.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var update in result.Value.EnumerateArray())
                    {
                        updateOffset = update.GetProperty("update_id").GetInt64() + 1;
                        if (!update.TryGetProperty("message", out var msg)) continue;
                        if (!msg.TryGetProperty("text", out var textEl)) continue;
                        long fromId = msg.GetProperty("from").GetProperty("id").GetInt64();
                        string? name = msg.GetProperty("from").TryGetProperty("first_name", out var fn) ? fn.GetString() : null;
                        string text = textEl.GetString() ?? "";

                        var state = GetState(fromId);
                        state.Name = name;
                        state.MessageBuffer.Add($"{name ?? fromId.ToString()}:{text}");
                        state.LastFlushedTime = DateTime.Now;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("TG轮询异常: {Msg}", ex.Message);
                await Task.Delay(5000, ct).ContinueWith(_ => { });
            }
        }
    }
}
