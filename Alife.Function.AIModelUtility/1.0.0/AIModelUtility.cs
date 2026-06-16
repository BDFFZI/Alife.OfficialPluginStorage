using System;
using System.IO;
using Alife.Platform;

namespace Alife.Function.AIModelUtility;

/// <summary>
/// 通用的模型与资源下载引导器。
/// 负责检测文件完整性并调用独立的 WPF 下载器窗口。
/// </summary>
public static class AIModelUtility
{
    public static string EnsureModelExisting(string modelId, string? targetFile = null)
    {
        string localPath = Path.Combine(ModelScopeModelPath, modelId.Replace(".", "___"));
        string checkFile = Path.Combine(localPath, targetFile ?? "README.md");

        if (!File.Exists(checkFile))
            AlifePlatform.Command("python",
            $"-c \"from modelscope import snapshot_download; snapshot_download('{modelId}')\"");
        if (!File.Exists(checkFile))
            throw new DirectoryNotFoundException($"模型下载失败，目录不存在：{localPath}");

        return targetFile != null ? checkFile : localPath;
    }

    public static string ModelScopeModelPath { get; }

    static AIModelUtility()
    {
        string modelScopeCachePath = Environment.GetEnvironmentVariable("MODELSCOPE_CACHE") ??
                                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "modelscope", "hub");
        ModelScopeModelPath = Path.Combine(modelScopeCachePath, "models").Replace(Path.DirectorySeparatorChar, '/');
    }
}
