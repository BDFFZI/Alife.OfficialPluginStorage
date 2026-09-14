using System.ComponentModel;

namespace BDFFZI.VibeCode.Language.LFM;

public class LFMLanguageModelConfig
{
    // ═══════════ 内核后端选择 ═══════════

    [DisplayName("使用 llama.cpp 后端（开箱即用）")]
    [Description("开启后自动下载 llama.cpp 的 llama-server 与 LFM GGUF 量化模型并本地自启服务，占用小、速度快；关闭则回落到下方本地 transformers 或远程模式。")]
    public bool UseLlamaCpp { get; set; } = true;

    [DisplayName("GGUF 模型文件名（modelscope）")]
    [Description("从 modelscope 的 LiquidAI/LFM2.5-2.6B-GGUF 仓库下载的 GGUF 文件：LFM2.5-2.6B-Q4_K_M.gguf（日常推荐，1.7GB）、Q4_0（1.6GB）、Q5_K_M（1.9GB）、Q6_K（2.2GB）、Q8_0（2.9GB）。")]
    public string LlamaCppModelFile { get; set; } = "LFM2.5-2.6B-Q4_K_M.gguf";

    [DisplayName("llama.cpp GPU 层数 (-1=全部)")]
    [Description("加载到 GPU 的层数。设 -1 表示全部层用 GPU（需 NVIDIA 显卡）；0 表示纯 CPU。无显卡时自动回落到 CPU。")]
    public int LlamaCppGpuLayers { get; set; } = -1;

    [DisplayName("llama.cpp 线程数 (0=自动)")]
    [Description("CPU 推理线程数，0 表示自动。")]
    public int LlamaCppThreads { get; set; } = 0;

    // ═══════════ 本地 transformers 推理模式 ═══════════

    [DisplayName("启用 transformers 本地推理")]
    [Description("关闭 llama.cpp 后可用此选项：通过 modelscope 下载 safetensors 模型并在本机用 torch/transformers 加载推理。")]
    public bool UseLocalModel { get; set; } = false;

    [DisplayName("本地模型ID (modelscope)")]
    [Description("需要下载的 LFM 模型，例如 LiquidAI/LFM2.5-1.2B-Instruct、LiquidAI/LFM2.5-1.2B-Thinking、LiquidAI/LFM2.5-2.6B。")]
    public string LocalModelId { get; set; } = "LiquidAI/LFM2.5-1.2B-Instruct";

    [DisplayName("本地服务端口")]
    [Description("本地推理服务的 HTTP 端口，默认 18080。被占用会自动向后顺延。")]
    public int LocalPort { get; set; } = 18080;

    [DisplayName("推理设备 (auto/cuda/cpu)")]
    [Description("auto 自动选择 GPU/CPU，cuda 强制 GPU，cpu 强制 CPU。")]
    public string Device { get; set; } = "auto";

    [DisplayName("最大生成长度 (MaxNewTokens)")]
    [Description("单次回复最多生成的 token 数。")]
    public int MaxNewTokens { get; set; } = 2048;

    [DisplayName("温度 (Temperature)")]
    [Description("采样温度，越高越随机。LFM 官方推荐思考模型 0.05-0.6，普通对话 0.6-0.8。")]
    public float Temperature { get; set; } = 0.6f;

    [DisplayName("Top-P")]
    [Description("核采样阈值，0-1 之间。")]
    public float TopP { get; set; } = 0.95f;

    [DisplayName("Top-K")]
    [Description("限定采样时排名前 K 的候选 token。")]
    public int TopK { get; set; } = 50;

    [DisplayName("重复惩罚 (Repetition Penalty)")]
    [Description("大于 1 会抑制重复，官方推荐 1.05。")]
    public float RepetitionPenalty { get; set; } = 1.05f;

    // ═══════════ 远程 OpenAI 兼容模式 ═══════════

    [DisplayName("Endpoint")]
    [Description("远程 OpenAI 兼容接口地址。OpenRouter：https://openrouter.ai/api/v1 ；或本机已部署的 vLLM/Ollama 地址。")]
    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1";

    [DisplayName("API Key")]
    [Description("远程服务商密钥。OpenRouter 为 sk-or- 开头；本地服务无鉴权时可随便填写。")]
    public string ApiKey { get; set; } = "";

    [DisplayName("Model ID")]
    [Description("远程模型ID，例如 OpenRouter 上的 liquid/lfm2.5-1.2b-instruct、liquid/lfm-40b-moe。")]
    public string ModelId { get; set; } = "liquid/lfm2.5-1.2b-instruct";

    // ═══════════ 通用参数 ═══════════

    [DisplayName("默认启用思考")]
    [Description("开启后默认走思考模式（对 LFM2.5-Thinking 等推理模型表现更好）。")]
    public bool DefaultThinking { get; set; } = false;

    [DisplayName("思考强度 (Reasoning Effort)")]
    [Description("仅对支持该参数的服务商有效，可填写 low/medium/high，留空则不发送该参数。")]
    public string ReasoningEffort { get; set; } = "";

    [DisplayName("思考模式附加请求体 (JSON)")]
    [Description("思考模式下附加的请求体参数，例如 OpenRouter 的 {\"route\":\"fallback\"}。留空则只发送标准字段。")]
    public string ExtraBody { get; set; } = "";

    [DisplayName("非思考模式附加请求体 (JSON)")]
    [Description("非思考模式下附加的请求体参数，留空则只发送标准字段。")]
    public string ExtraBodyNotThinking { get; set; } = "";

    [DisplayName("自定义请求头 (JSON)")]
    [Description("附加到每个请求的请求头。OpenRouter 推荐 {\"HTTP-Referer\":\"https://alife.local\",\"X-Title\":\"Alife\"}")]
    public string ExtraHeaders { get; set; } = "";
}

