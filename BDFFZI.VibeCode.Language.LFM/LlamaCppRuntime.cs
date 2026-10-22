using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Microsoft.Extensions.Logging;

namespace BDFFZI.VibeCode.Language.LFM;

/// <summary>
/// 内置 llama.cpp 后端：自动下载 llama-server 与 GGUF 模型并自启本地 OpenAI 兼容服务。
/// 通过 Windows Job Object 保证父进程退出时自动结束子进程，避免服务残留。
/// </summary>
public sealed class LlamaCppRuntime : IAsyncDisposable
{
    public string Endpoint { get; private set; } = "";
    public string ModelId { get; private set; } = "";

    readonly ILogger logger;
    readonly string modelRepo;
    readonly string modelFile;
    readonly int port;
    readonly int gpuLayers;
    readonly int threads;

    Process? serverProcess;
    IntPtr jobHandle;
    readonly string runtimeDir = Path.Combine(AlifePath.RuntimeFolderPath, "llama.cpp");
    readonly string modelDir = Path.Combine(AlifePath.RuntimeFolderPath, "LFM", "models");

    public LlamaCppRuntime(ILogger logger, string modelRepo, string modelFile, int port, int gpuLayers, int threads)
    {
        this.logger = logger;
        this.modelRepo = modelRepo;
        this.modelFile = modelFile;
        this.port = port;
        this.gpuLayers = gpuLayers;
        this.threads = threads;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await EnsureModelAsync();
        string serverExe = await EnsureServerAsync();
        await LaunchServerAsync(serverExe);
        await WaitReadyAsync(ct);
        Endpoint = $"http://127.0.0.1:{port}/v1";
        ModelId = modelFile;
        logger.LogInformation("LFM llama.cpp 推理服务已就绪: {Endpoint} ({ModelFile})", Endpoint, modelFile);
    }

    async Task EnsureModelAsync()
    {
        Directory.CreateDirectory(modelDir);
        string modelPath = Path.Combine(modelDir, modelFile);
        if (File.Exists(modelPath) == false)
        {
            logger.LogInformation("正在下载 LFM GGUF 模型：{Repo}/{ModelFile} ...", modelRepo, modelFile);
            string code = $"from modelscope import snapshot_download; snapshot_download('{modelRepo}', allow_file_pattern=['{modelFile}'], local_dir=r'{modelDir}')";
            CommandResult result = AlifeUtility.Command("python", $"-c \"{code}\"");
            if (result.ExitCode != 0 || File.Exists(modelPath) == false)
                throw new Exception($"模型下载失败：{result.StandardError}");
            logger.LogInformation("模型就绪：{Path}", modelPath);
        }
        else
        {
            logger.LogInformation("模型已存在：{Path}", modelPath);
        }
    }

    async Task<string> EnsureServerAsync()
    {
        string exePath = Path.Combine(runtimeDir, "llama-server.exe");
        if (File.Exists(exePath))
            return exePath;

        bool wantCuda = gpuLayers != 0 && HasNvidiaGpu();
        string[] candidates = wantCuda
            ? ["win-cuda-12.4-x64.zip", "win-cuda-13.3-x64.zip", "win-cpu-x64.zip"]
            : ["win-cpu-x64.zip"];

        // 获取最新发行版信息
        string json = await AlifeUtility.FetchStringAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");
        using var doc = JsonDocument.Parse(json);
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString() ?? "")
            .ToArray();

        string? assetName = null;
        foreach (var suffix in candidates)
        {
            string? match = assets.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (match != null) { assetName = match; break; }
        }
        if (assetName == null)
            throw new Exception("未找到合适的 llama.cpp 发行包。");

        string url = $"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{assetName}";
        string zipPath = Path.Combine(AlifePath.TempFolderPath, assetName);
        logger.LogInformation("正在下载 llama.cpp 运行时：{Asset} ...", assetName);
        await AlifeUtility.DownloadFileAsync(url, zipPath, (read, total) => {
            if (total > 0)
                logger.LogInformation("下载进度: {Pct:F1}% ({ReadMB}MB / {TotalMB}MB)",
                    (double)read / total * 100, read / 1024 / 1024, total / 1024 / 1024);
            else
                logger.LogInformation("下载进度: {ReadMB}MB", read / 1024 / 1024);
        }, TimeSpan.FromMinutes(30));
        Directory.CreateDirectory(runtimeDir);
        await System.IO.Compression.ZipFile.ExtractToDirectoryAsync(zipPath, runtimeDir, overwriteFiles: true);
        File.Delete(zipPath);

        // CUDA 构建需要额外的 CUDA 运行库（cudart/cublas），官网单独打包发布
        if (wantCuda)
        {
            string cudaAsset = assetName.Replace("llama-", "cudart-llama-");
            if (assets.Any(n => n.Equals(cudaAsset, StringComparison.OrdinalIgnoreCase)))
            {
                string cudaUrl = $"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{cudaAsset}";
                string cudaZipPath = Path.Combine(AlifePath.TempFolderPath, cudaAsset);
                logger.LogInformation("正在下载 CUDA 运行库：{Asset} ...", cudaAsset);
                await AlifeUtility.DownloadFileAsync(cudaUrl, cudaZipPath, null, TimeSpan.FromMinutes(30));
                await System.IO.Compression.ZipFile.ExtractToDirectoryAsync(cudaZipPath, runtimeDir, overwriteFiles: true);
                File.Delete(cudaZipPath);
            }
            else
            {
                logger.LogWarning("未找到对应的 CUDA 运行库包：{Asset}，GPU 加速可能不可用。", cudaAsset);
            }
        }

        if (File.Exists(exePath) == false)
            throw new Exception("解压后未找到 llama-server.exe。");
        return exePath;
    }

    async Task LaunchServerAsync(string exePath)
    {
        string modelPath = Path.Combine(modelDir, modelFile);
        string gpuArg = gpuLayers < 0 ? "-ngl 9999" : $"-ngl {gpuLayers}";
        string threadArg = threads > 0 ? $"-t {threads}" : "";
        var psi = new ProcessStartInfo {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = runtimeDir
        };
        psi.Arguments = $"-m \"{modelPath}\" --host 127.0.0.1 --port {port} {gpuArg} {threadArg} --no-webui";

        // 将子进程绑定到 Job Object，父进程退出时自动终止
        jobHandle = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        Marshal.StructureToPtr(info, infoPtr, false);
        SetInformationJobObject(jobHandle, 9, infoPtr, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        Marshal.FreeHGlobal(infoPtr);

        serverProcess = Process.Start(psi) ?? throw new Exception("无法启动 llama-server 进程。");
        if (jobHandle != IntPtr.Zero)
            AssignProcessToJobObject(jobHandle, serverProcess.Handle);

        serverProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) logger.LogInformation("[llama] {Log}", e.Data); };
        serverProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) logger.LogInformation("[llama] {Log}", e.Data); };
        serverProcess.BeginOutputReadLine();
        serverProcess.BeginErrorReadLine();
    }

    async Task WaitReadyAsync(CancellationToken ct)
    {
        DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(15);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        while (DateTime.Now < deadline)
        {
            if (serverProcess != null && serverProcess.HasExited)
                throw new Exception($"llama-server 提前退出，退出码 {serverProcess.ExitCode}。请查看日志。");
            try
            {
                var resp = await client.GetAsync($"http://127.0.0.1:{port}/v1/models", ct);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // 服务尚未就绪，继续等待
            }
            await Task.Delay(1000, ct);
        }
        throw new Exception("等待 llama-server 就绪超时（15分钟）。");
    }

    static bool HasNvidiaGpu()
    {
        try
        {
            CommandResult result = AlifeUtility.Command("nvidia-smi", "-L");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (serverProcess != null)
        {
            try { serverProcess.Kill(entireProcessTree: true); } catch { }
            try { serverProcess.Dispose(); } catch { }
            serverProcess = null;
        }
        if (jobHandle != IntPtr.Zero)
        {
            try { CloseHandle(jobHandle); } catch { }
            jobHandle = IntPtr.Zero;
        }
        await Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public long PerJobUserTimeLimit;
        public long PerProcessUserTimeLimit;
        public long LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public long JOB_OBJECT_BASIC_LIMIT_INFORMATION_UNION;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}


