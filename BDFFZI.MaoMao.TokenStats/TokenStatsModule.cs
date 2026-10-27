using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Platform;
using Microsoft.Extensions.Logging;
using Alife.Function.Client.TokenStatistics;
using Alife.Function.FunctionCaller;

namespace BDFFZI.MaoMao.TokenStats;

public class TokenStatsConfig
{
    [System.ComponentModel.DisplayName("启用采集")]
    [System.ComponentModel.Description("关闭后本角色不再记录 token 用量，已落盘历史不受影响。")]
    public bool Enabled { get; set; } = true;
}

[Module("Token用量采集",
    "采集本角色每轮对话的 token 用量，反查模型/渠道/来源维度，按角色分文件+汇总双写落盘到 Storage/Tokenlog。" +
    "数据格式 jsonl：每行一条记录，字段 t=时间 i=输入 o=输出 c=缓存 v=总额 m=模型 s=来源 ch=渠道 h=域名 n=角色名。" +
    "本模块仅负责采集落盘，展示由全局统计页读取。",
    defaultCategory: "真央的小工具")]
public class TokenStatsModule(
    ILogger<TokenStatsModule> logger,
    XmlFunctionCaller functionCaller,
    Interactor<TokenStatsModule> interactor) :
    ChatBehaviour,
    IConfigurable<TokenStatsConfig>
{
    public TokenStatsConfig Configuration { get; set; } = null!;

    readonly object sync = new();
    ITokenUser? subscribedUser;   // 当前已订阅 TokenUsed 的模型实例（用于换模型重订阅）

    static readonly Regex SourceTagRegex = new(@"\[消息来源\(([^)\]]+)\)\]", RegexOptions.Compiled);

    protected override Task OnAwake()
    {
        Subscribe();
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    // 订阅当前 LanguageModel 的 TokenUsed；记录实例以便换模型时重订阅
    void Subscribe()
    {
        ITokenUser? user = ChatBot.LanguageModel as ITokenUser;
        if (user != null)
        {
            user.TokenUsed += OnTokenUsed;
            subscribedUser = user;
        }
        else
        {
            logger?.LogWarning("[TokenStats] 当前 LanguageModel 未实现 ITokenUser，本轮无法订阅用量");
        }
    }

    void Unsubscribe()
    {
        if (subscribedUser != null)
        {
            subscribedUser.TokenUsed -= OnTokenUsed;
            subscribedUser = null;
        }
    }

    void OnTokenUsed(TokenUsageContext ctx)
    {
        try
        {
            if (Configuration?.Enabled == false) return;
            // 空用量（流式失败等异常轮）不入账
            if (ctx.TokenUsage.Total <= 0) return;

            // 换模型重订阅：若当前模型实例与已订阅的不同，先退订旧的再订新的
            ITokenUser? cur = ChatBot.LanguageModel as ITokenUser;
            if (cur != null && !ReferenceEquals(cur, subscribedUser))
            {
                Unsubscribe();
                Subscribe();
            }

            DateTime now = DateTime.Now;
            string role = Character?.Name ?? "未知角色";
            string source = ClassifySource(ctx.UserMessage);
            (string channel, string model, string host) = ResolveChannelAndModel();

            string line = BuildLine(now, ctx.TokenUsage, model, source, channel, host, role);
            Persist(line, role);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[TokenStats] 采集落盘异常");
        }
    }

    static string ClassifySource(string userMsg)
    {
        if (string.IsNullOrEmpty(userMsg)) return "系统";
        Match m = SourceTagRegex.Match(userMsg);
        if (m.Success) return m.Groups[1].Value;
        if (userMsg.Contains("消息来源:[ChatWindow]")) return "ChatWindow";
        if (userMsg.Contains("[QQ官方群聊消息")) return "qchat";
        if (userMsg.Contains("[QQ官方私聊消息")) return "qchat";
        if (userMsg.Contains("系统周期报点")) return "报点";
        return "系统";
    }

    // 反查模型/渠道/endpoint域名（照搬1chuxin思路：反射读属性，灵枢归因优先）
    (string Channel, string Model, string Host) ResolveChannelAndModel()
    {
        object? lm = ChatBot.LanguageModel;
        if (lm == null) return ("未配置", "未配置", "");

        string model = ReadStringMember(lm, "ModelId") ?? ReadStringMember(lm, "ModelName")
                       ?? ReadStringMember(lm, "modelId") ?? lm.GetType().Name;

        // 灵枢归因：LastServedGroupIndex(≥4.5) → ForcedGroupIndex(旧版)
        int served = ReadIntMember(lm, "LastServedGroupIndex") ?? -1;
        if (served < 0) served = ReadIntMember(lm, "ForcedGroupIndex") ?? -1;

        string channel = "默认渠道";
        string host = "";
        string? endpoint = ReadStringMember(lm, "Endpoint") ?? ReadStringMember(lm, "endpoint")
                           ?? ReadStringMember(lm, "BaseUrl") ?? ReadStringMember(lm, "baseUrl");
        if (endpoint != null && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? u) && u.Host.Length > 0)
            host = u.Host;

        if (host.Length > 0) channel = host;
        return (channel, model.Length > 0 ? model : lm.GetType().Name, host);
    }

    static string? ReadStringMember(object obj, string name)
    {
        try
        {
            var p = obj.GetType().GetProperty(name);
            if (p == null) return null;
            return p.GetValue(obj) as string;
        }
        catch { return null; }
    }

    static int? ReadIntMember(object obj, string name)
    {
        try
        {
            var p = obj.GetType().GetProperty(name);
            if (p == null) return null;
            object? v = p.GetValue(obj);
            if (v is int i) return i;
            return null;
        }
        catch { return null; }
    }

    static string BuildLine(DateTime now, TokenUsage u, string model, string source,
                            string channel, string host, string role)
    {
        // 字段对齐1chuxin：t i o c v m s ch h n
        var sb = new StringBuilder(160);
        sb.Append('{');
        AppendStr(sb, "t", now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)); sb.Append(',');
        sb.Append("\"i\":").Append(u.Input).Append(',');
        sb.Append("\"o\":").Append(u.Output).Append(',');
        sb.Append("\"c\":").Append(u.Cached).Append(',');
        sb.Append("\"v\":").Append(u.Total).Append(',');
        AppendStr(sb, "m", model); sb.Append(',');
        AppendStr(sb, "s", source); sb.Append(',');
        AppendStr(sb, "ch", channel); sb.Append(',');
        AppendStr(sb, "h", host); sb.Append(',');
        AppendStr(sb, "n", role);
        sb.Append('}');
        return sb.ToString();
    }

    static void AppendStr(StringBuilder sb, string key, string val)
    {
        sb.Append('"').Append(key).Append("\":\"");
        sb.Append(JsonEncodedText.Encode(val ?? "", JavaScriptEncoder.Default));
        sb.Append('"');
    }

    // 双写落盘：角色分文件 + 汇总文件，均 Storage/Tokenlog/usage-log*.jsonl
    void Persist(string line, string role)
    {
        string logDir = Path.Combine(AlifePath.StorageFolderPath, "Tokenlog");
        Directory.CreateDirectory(logDir);
        string safeRole = string.Join("_", role.Split(Path.GetInvalidFileNameChars()));
        string roleFile = Path.Combine(logDir, $"usage-log.{safeRole}.jsonl");
        string masterFile = Path.Combine(logDir, "usage-log.jsonl");
        lock (sync)
        {
            File.AppendAllText(roleFile, line + Environment.NewLine, Encoding.UTF8);
            File.AppendAllText(masterFile, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
