
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
using Microsoft.Extensions.Logging;

public class MaoMaoMusicPluginData
{
    [Description("下载目录")]
    public string DownloadPath { get; set; } = @"C:\Users\13309\Documents\Alife\Storage\Cache";
}

[Module("在线点歌助手", "输入歌名就能从网易云搜索下载音频文件", defaultCategory: "MaoMao/音乐点歌")]
[Category("音乐点歌")]
public class MaoMaoMusicPlugin(
    XmlFunctionCaller functionService,
    ILogger<MaoMaoMusicPlugin> logger
) :
    InteractiveModule<MaoMaoMusicPlugin>,
    IConfigurable<MaoMaoMusicPluginData>
{
    private static readonly HttpClient _http = new();
    public MaoMaoMusicPluginData? Configuration { get; set; }
    private string DownloadPath => Configuration?.DownloadPath ?? @"C:\Users\13309\Documents\Alife\Storage\Cache";

    [XmlFunction(FunctionMode.OneShot)]
    [Description("搜索音乐，输入关键词返回网易云搜索结果")]
    public async Task SearchMusic([Description("搜索关键词")] string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            Poke("请输入搜索关键词喵～");
            return;
        }
        try
        {
            string url = $"https://music.163.com/api/search/get?s={Uri.EscapeDataString(keyword)}&type=1&limit=5";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", "https://music.163.com/");
            req.Headers.Add("User-Agent", "Mozilla/5.0");
            var resp = await _http.SendAsync(req);
            var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var songs = json.RootElement.GetProperty("result").GetProperty("songs");
            var msgs = new System.Collections.Generic.List<string>();
            int idx = 1;
            foreach (var s in songs.EnumerateArray())
            {
                var id = s.GetProperty("id").GetInt32();
                var name = s.GetProperty("name").GetString();
                var artists = string.Join("/", s.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
                var dur = s.GetProperty("duration").GetInt32() / 1000;
                msgs.Add($"{idx}. {name} - {artists} ({dur / 60}:{dur % 60:D2})");
                idx++;
            }
            if (msgs.Count > 0)
            {
                // 存储第一首歌的ID供下载用
                var first = songs.EnumerateArray().First();
                _lastSongId = first.GetProperty("id").GetInt32();
                _lastSongName = first.GetProperty("name").GetString();
                Poke("搜索「" + keyword + "」找到：\n" + string.Join("\n", msgs) + "\n\n用 DownloadMusic(序号) 下载喵～");
            }
            else
                Poke("搜索「" + keyword + "」没有结果喵～");
        }
        catch (Exception ex)
        {
            Poke("搜索出错了：" + ex.Message);
        }
    }

    private int _lastSongId;
    private string _lastSongName = "";

    [XmlFunction(FunctionMode.OneShot)]
    [Description("下载音乐，输入歌曲名称或ID来下载")]
    public async Task DownloadMusic([Description("歌曲名称或网易云ID")] string query)
    {
        DirHelper.MakeDirectory(DownloadPath);
        try
        {
            var proc = new Process();
            proc.StartInfo.FileName = "cmd.exe";
            proc.StartInfo.Arguments = $"/c yt-dlp --extract-audio --audio-format mp3 -o \"{DownloadPath}\\%(title)s.%(ext)s\" \"ytsearch1:{query}\" --no-check-certificate";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.Start();
            string output = await proc.StandardOutput.ReadToEndAsync();
            string error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0)
                Poke("下载完成喵～已保存到缓存文件夹！");
            else
                Poke("下载出错了：" + (string.IsNullOrEmpty(error) ? output : error)[..200]);
        }
        catch (Exception ex)
        {
            Poke("下载出错：" + ex.Message);
        }
    }
}
