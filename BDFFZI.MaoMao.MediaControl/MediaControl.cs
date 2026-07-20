using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.Media.Control;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;

namespace BDFFZI.MaoMao.MediaControl;

[Module("媒体控制", "获取当前系统播放的媒体信息，支持播放控制",
    defaultCategory: "真央的小工具")]
public class MediaControlModule(
    XmlFunctionCaller functionService,
    ILogger<NowPlayingModule> logger
) : InteractiveModule<NowPlayingModule>
{
    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        XmlHandler xmlHandler = new(this)
        {
            Description = "获取当前Windows系统正在播放的媒体信息，支持各种音乐App和浏览器，也支持播放控制。",
        };
        functionService.RegisterHandler(xmlHandler);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取当前Windows系统正在播放的媒体信息，包括歌曲名、歌手、播放状态和进度")]
    public async Task GetNowPlaying()
    {
        try
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = mgr.GetCurrentSession();
            if (session == null)
            {
                Poke("当前没有检测到任何播放内容");
                return;
            }

            var mediaProps = await session.TryGetMediaPropertiesAsync();
            var playbackInfo = session.GetPlaybackInfo();

            string song = mediaProps?.Title ?? "未知";
            string artist = mediaProps?.Artist ?? "未知";
            string status = playbackInfo?.PlaybackStatus.ToString() ?? "未知";

            string result = $"{status}\n歌曲：{song}\n歌手：{artist}";
            if (mediaProps?.Thumbnail != null)
                result += "\n[有专辑封面]";

            Poke(result);
        }
        catch (Exception ex)
        {
            Poke($"获取播放信息失败：{ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("控制媒体播放，command参数支持：Play播放/Pause暂停/Next下一首/Previous上一首")]
    public async Task ControlPlayback([Description("控制命令：Play,Pause,Next,Previous")] string command)
    {
        try
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = mgr.GetCurrentSession();
            if (session == null)
            {
                Poke("没有检测到可控制的播放会话");
                return;
            }

            switch (command.ToLower())
            {
                case "play":
                    await session.TryPlayAsync();
                    Poke("已播放");
                    break;
                case "pause":
                    await session.TryPauseAsync();
                    Poke("已暂停");
                    break;
                case "next":
                    await session.TrySkipNextAsync();
                    Poke("已下一首");
                    break;
                case "previous":
                    await session.TrySkipPreviousAsync();
                    Poke("已上一首");
                    break;
                default:
                    Poke($"不支持的命令：{command}，支持：Play/Pause/Next/Previous");
                    break;
            }
        }
        catch (Exception ex)
        {
            Poke($"控制播放失败：{ex.Message}");
        }
    }
}
