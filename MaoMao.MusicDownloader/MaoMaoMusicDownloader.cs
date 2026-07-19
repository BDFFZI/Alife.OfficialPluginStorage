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
using Alife.Function.Interpreter;

public class MaoMaoMusicDownloaderData
{
    [Description("下载目录")]
    public string DownloadPath { get; set; } = @"C:\Users\13309\Documents\Alife\Storage\Files";
    
    [Description("默认音质")]
    public string DefaultQuality { get; set; } = "standard";
}

[Module(
    "在线点歌助手", "输入歌名就能从网易云搜索下载音频文件。支持关键词搜索和ID直链下载。"
)]
public class MaoMaoMusicDownloader(
    XmlFunctionCaller functionService,
    ILogger<MaoMaoMusicDownloader> logger
) :
    InteractiveModule<MaoMaoMusicDownloader>,
    IConfigurable<MaoMaoMusicDownloaderData>
{
    private static readonly HttpClient _http = new();
    public MaoMaoMusicDownloaderData? Configuration { get; set; }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("搜索音乐，输入关键词返回网易云搜索结果")]
    public async Task SearchMusic(
        [Description("搜索关键词，如'千本桜'")] string keyword
    )
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
                msgs.Add($"{idx}. {name} - {artists} ({dur/60}:{dur%60:D2})\n   ID: {id}");
                idx++;
            }
            if (msgs.Count > 0)
            {
                Poke($"搜索「{keyword}」找到以下歌曲：\n" + string.Join("\n", msgs) + "\n\n用 DownloadMusicById(歌曲ID) 来下载喵～");
            }
            else
            {
                Poke($"搜索「{keyword}」没有找到结果，换个关键词试试喵～");
            }
        }
        catch (System.Exception ex)
        {
            Poke($"搜索出错了：{ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("下载音乐，按歌名从网易云下载")]
    public async Task DownloadMusic(
        [Description("歌曲名称，如'旅の途中'")] string songName,
        [Description("音质：standard/higher/exhigh/lossless，默认standard")] string? quality = null
    )
    {
        if (string.IsNullOrWhiteSpace(songName))
        {
            Poke("请输入歌曲名称喵～");
            return;
        }
        quality ??= Configuration?.DefaultQuality ?? "standard";
        string downloadPath = Configuration?.DownloadPath ?? @"C:\Users\13309\Documents\Alife\Storage\Files";
        DirHelper.MakeDirectory(downloadPath);
        try
        {
            var proc = new Process();
            proc.StartInfo.FileName = "cmd.exe";
            proc.StartInfo.Arguments = $"/c python -m yt_dlp -f \"{quality}\" --extract-audio --audio-format mp3 -o \"{downloadPath}\\%%(title)s.%%(ext)s\" \"ytsearch1:{songName} 网易云\" --no-check-certificate";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60000);
            var mp3s = Directory.GetFiles(downloadPath, "*.mp3");
            if (mp3s.Length > 0)
            {
                var f = mp3s[^1];
                var fi = new FileInfo(f);
                Poke($"下载完成！{Path.GetFileName(f)} ({fi.Length/1024}KB)");
            }
            else
            {
                Poke($"没找到《{songName}》这首歌，换换关键词试试喵～");
            }
        }
        catch (System.Exception ex)
        {
            Poke($"下载出错了：{ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按歌曲ID下载音乐，如26220167")]
    public async Task DownloadMusicById(
        [Description("网易云歌曲ID")] string songId,
        [Description("音质：standard/higher/exhigh/lossless，默认standard")] string? quality = null
    )
    {
        quality ??= Configuration?.DefaultQuality ?? "standard";
        string downloadPath = Configuration?.DownloadPath ?? @"C:\Users\13309\Documents\Alife\Storage\Files";
        try
        {
            var url = $"https://music.163.com/song?id={songId}";
            var proc = new Process();
            proc.StartInfo.FileName = "cmd.exe";
            proc.StartInfo.Arguments = $"/c python -m yt_dlp -f \"{quality}\" --extract-audio --audio-format mp3 -o \"{downloadPath}\\%%(title)s.%%(ext)s\" \"{url}\" --no-check-certificate";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60000);
            var mp3s = Directory.GetFiles(downloadPath, "*.mp3");
            if (mp3s.Length > 0)
            {
                var f = mp3s[^1];
                var fi = new FileInfo(f);
                Poke($"歌曲ID {songId} 下载完成！{Path.GetFileName(f)} ({fi.Length/1024}KB)");
            }
            else
            {
                Poke($"歌曲ID {songId} 下载出错啦，检查一下ID对不对喵～");
            }
        }
        catch (System.Exception ex)
        {
            Poke($"下载出错了：{ex.Message}");
        }
    }

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        var handler = new XmlHandler(this)
        {
            Description = "在线点歌服务：输入歌名就能从网易云搜索下载音乐文件。支持SearchMusic（搜索）、DownloadMusic（按歌名下载）、DownloadMusicById（按歌曲ID下载）三个功能。"
        };
        functionService.RegisterHandler(handler);
    }

    private static class DirHelper
    {
        public static void MakeDirectory(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }
    }
}