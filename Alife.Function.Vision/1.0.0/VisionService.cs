using System;
using Alife.Platform;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;

namespace Alife.Function.Vision;

public record VisionServiceConfig
{
    //对图片的附加提示词
    public string AppendPrompt { get; set; } = "（请精简的描述一下图片大体内容，避免输出过多的文本，提高分析速度）";
}

[Module("视觉感知", "让 AI 能够看到屏幕内容，理解图片，观察世界。",
    defaultCategory: "Alife 官方/实用工具",
    EditorUI = typeof(VisionServiceUI))]
[Description($"此服务让你拥有视觉感知能力，你可以通过<{nameof(GetWindows)}>获取当前系统运行的窗口，然后传入到<{nameof(LookWindow)}>中进行视觉分析。或则直接将图片链接或地址，传入到<{nameof(LookImage)}>中分析")]
public class VisionService(XmlFunctionCaller functionService, IVisionModel? visionModel = null)
    : InteractiveModule<VisionService>, IConfigurable<VisionServiceConfig>
{
    public VisionServiceConfig? Configuration { get; set; }

    /// <summary>
    /// 获取当前可以截取的所有可用窗口的列表，供 AI 选择截屏目标。
    /// </summary>
    [XmlFunction(FunctionMode.OneShot)]
    public void GetWindows()
    {
        var windows = WindowCaptureHelper.EnumerateWindows()
            .Where(w => !string.IsNullOrWhiteSpace(w.Title))
            .ToList();

        Poke($"""
              【当前窗口列表】
              {string.Join("\n", windows.Select(info => $"Handle: {info.Handle.ToInt64()} | 标题: {info.Title}"))}
              Handle: -1 | 直接查看全屏内容
              【当前焦点窗口】
              {WindowsPlatform.GetActiveWindowTitle()}
              """);
    }

    /// <summary>
    /// 截取指定窗口或全屏并进行视觉理解，将结果反馈给 AI。
    /// </summary>
    [XmlFunction(FunctionMode.OneShot)]
    [Description("(使用后需等待结果返回)")]
    public async Task LookWindow(long hwnd, string prompt, int replyCharCount = 64)
    {
        if (AlifePlatform.IsLocking())
        {
            Poke("【屏幕分析结果】当前电脑处于锁屏状态，无法获取屏幕内容，用户应该不在电脑前。");
            return;
        }

        //截取目标画面
        string screenshotPath = Path.Combine(AlifePath.TempFolderPath, $"vision_capture_{DateTime.Now.Ticks}.png");
        {
            using var bmp = hwnd == -1
                ? await WindowCaptureHelper.CaptureFullscreenAsync()
                : await WindowCaptureHelper.CaptureWindowAsync(new IntPtr(hwnd));
            bmp.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        //获取深度识别结果
        prompt += Configuration!.AppendPrompt;
        if (hwnd == -1)
            prompt += $"（这是一张屏幕截图，当前焦点窗口为{WindowsPlatform.GetActiveWindowTitle()}）" + Configuration!.AppendPrompt;

        CancellationTokenSource cancellationTokenSource = new(30000);
        string deepVisionResult = visionModel != null
            ? $"{await visionModel.QueryAsync(
                screenshotPath,
                prompt,
                replyCharCount,
                cancellationToken: cancellationTokenSource.Token)}"
            : "未开启";

        if (hwnd == -1)
        {
            Poke($"""
                  【屏幕分析结果】
                  - 文字识别：全屏识图不支持文字识别，请针对特定窗口识别
                  - 深度视觉：{deepVisionResult}（内容不一定准确仅供参考）
                  """);
        }
        else
        {
            Poke($"""
                  【窗口分析结果】
                  - 文字识别：{await AlifePlatform.OcrAsync(screenshotPath)}
                  - 深度视觉：{deepVisionResult}（内容不一定准确仅供参考）
                  """);
        }
    }

    /// <summary>
    /// 分析指定路径的图片。
    /// </summary>
    [XmlFunction(FunctionMode.OneShot)]
    [Description("(使用后需等待结果返回)")]
    public async Task LookImage(
        string pathOrUrl, string prompt, int replyLength = 64)
    {
        // 处理网络图片
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string downloaded = $"{AlifePath.TempFolderPath}/vision_download.png";
            await AlifePlatform.DownloadFileAsync(pathOrUrl, downloaded);
            pathOrUrl = downloaded;
        }

        prompt += Configuration!.AppendPrompt;

        CancellationTokenSource cancellationTokenSource = new(30000);
        string deepVisionResult = visionModel != null
            ? await visionModel.QueryAsync(
                pathOrUrl,
                prompt,
                replyLength,
                cancellationToken: cancellationTokenSource.Token)
            : "未开启";

        Poke($"""
              【图片分析结果】
              - 文字识别：{await AlifePlatform.OcrAsync(pathOrUrl)}
              - 深度视觉：{deepVisionResult}（内容不一定准确仅供参考）
              """);
    }

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);
        functionService.RegisterHandler(this);
    }
}
