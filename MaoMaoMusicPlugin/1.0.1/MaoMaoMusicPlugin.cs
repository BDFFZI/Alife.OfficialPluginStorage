using System;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;

public class MaoMaoMusicPluginData
{
    [Description("下载目录")]
    public string DownloadPath { get; set; } = Path.Combine(Path.GetTempPath(), "AlifeMusicCache");
}

[Module("在线点歌助手", "输入歌名就能从网易云搜索下载音频文件。", defaultCategory: "MaoMao/音乐点歌")]
public class MaoMaoMusicPlugin(
    XmlFunctionCaller functionService,
    ILogger<MaoMaoMusicPlugin> logger
) : InteractiveModule<MaoMaoMusicPlugin>, IConfigurable<MaoMaoMusicPluginData>
{
    private static readonly HttpClient _http = new();
    public MaoMaoMusicPluginData? Configuration { get; set; }

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        XmlHandler xmlHandler = new(this)
        {
            Description = "在线点歌助手，可搜索网易云音乐并下载。",
        };
        functionService.RegisterHandler(xmlHandler);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("搜索音乐，输入关键词返回网易云搜索结果")]
    public async Task SearchMusic([Description("搜索关键词，如千本桜")] string query)
    {
        if (string.IsNullOrWhiteSpace(query)) { Poke("请输入搜索关键词喵～"); return; }
        string downloadPath = Configuration?.DownloadPath ?? Path.Combine(Path.GetTempPath(), "AlifeMusicCache");
        try
        {
            string url = $"https://music.163.com/api/search/get?s={Uri.EscapeDataString(query)}&type=1&limit=5";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", "https://music.163.com/");
            req.Headers.Add("User-Agent", "Mozilla/5.0");
            var resp = await _http.SendAsync(req);
            var raw = await resp.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(raw);
            var songs = json.RootElement.GetProperty("result").GetProperty("songs");
            var msgs = new System.Collections.Generic.List<string>();
            int idx = 1;
            foreach (var s in songs.EnumerateArray())
            {
                try
                {
                    int id;
                    var idProp = s.GetProperty("id");
                    if (idProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        id = int.Parse(idProp.GetString());
                    else
                        id = idProp.GetInt32();

                    var name = s.GetProperty("name").GetString();
                    var artists = string.Join("/", s.GetProperty("artists").EnumerateArray().Select(a => { var n = a.GetProperty("name").GetString(); return string.IsNullOrEmpty(n) ? "未知艺术家" : n; }));
                    var dur = s.GetProperty("duration").GetInt32() / 1000;
                    msgs.Add($"{idx}. {name} - {artists} ({dur/60}:{dur%60:D2})\n   ID: {id}");
                    idx++;
                }
                catch (Exception ex)
                {
                    Poke($"跳过异常歌曲: {ex.Message}");
                }
            }
            if (msgs.Count > 0)
                Poke($"搜索「{query}」找到以下歌曲：\n" + string.Join("\n", msgs) + $"\n\n用 DownloadMusicById(歌曲ID) 来下载喵～");
            else
                Poke($"搜索「{query}」没有找到结果，换个关键词试试喵～");
        }
        catch (Exception ex) { Poke($"搜索出错了：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按歌曲ID下载音乐，如26220167，可指定音质：low(128k)/medium(192k)/high(256k)/lossless(无损)")]
    public async Task DownloadMusicById(
        [Description("网易云歌曲ID")] string songId,
        [Description("音质：low(128k)/medium(192k)/high(256k)/lossless(无损)，默认low")] string quality = "low")
    {
        if (string.IsNullOrWhiteSpace(songId)) { Poke("请输入歌曲ID喵～"); return; }
        string downloadPath = Configuration?.DownloadPath ?? Path.Combine(Path.GetTempPath(), "AlifeMusicCache");
        if (!Directory.Exists(downloadPath)) Directory.CreateDirectory(downloadPath);
        try
        {
            // 音质参数映射
            string qualityArg = quality.ToLower() switch
            {
                "low" or "128" => "--audio-quality 9",
                "medium" or "192" => "--audio-quality 5",
                "high" or "256" => "--audio-quality 2",
                "lossless" or "无损" => "",
                _ => "--audio-quality 9"
            };

            var proc = new Process();
            proc.StartInfo.FileName = "cmd.exe";
            proc.StartInfo.Arguments = $"/c yt-dlp --extract-audio --audio-format mp3 {qualityArg} -o \"{downloadPath}\\{songId}\\%(title)s.%(ext)s\" \"https://music.163.com/song?id={songId}\" --no-check-certificate";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60000);
            string songDir = Path.Combine(downloadPath, songId);
            var mp3s = Directory.GetFiles(songDir, "*.mp3");
            if (mp3s.Length > 0)
            {
                var f = mp3s[0];
                var fi = new FileInfo(f);
                string qName = quality.ToLower() switch
                {
                    "low" or "128" => "低音质(128k)",
                    "medium" or "192" => "中音质(192k)",
                    "high" or "256" => "高音质(256k)",
                    "lossless" or "无损" => "无损",
                    _ => "默认"
                };
                Poke($"下载完成！{qName}：{fi.FullName} ({fi.Length/1024}KB)");
            }
            else
                Poke($"歌曲ID {songId} 下载出错啦，看看yt-dlp输出：{output[..Math.Min(output.Length,200)]}喵～");
        }
        catch (Exception ex) { Poke($"下载出错了：{ex.Message}"); }
    }
}