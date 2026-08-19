using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.AIModelUtility;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace BDFFZI.VibeCode.Language.LFM;

[Module(
    "LFM语言模型（本地离线）",
    "本地离线的 LFM 语言模型，自动下载模型并在本机推理。注意：效果非常差，仅作本地部署测试用，请谨慎使用。",
    defaultCategory: "BDFFZI 插件/模型接入/语言模型"
)]
public class LFMLanguageModel(
    StorageSystem storageSystem,
    ILogger<LFMLanguageModel> logger) :
    ChatBehaviour,
    ILanguageModel,
    IConfigurable<LFMLanguageModelConfig>
{
    public LFMLanguageModelConfig Configuration { get; set; } = null!;

    public OccupationNotepad GetThinkingRequester()
    {
        return thinkingRequester;
    }

    public async Task<string> ChatStreamingAsync(
        ChatHistoryAgentThread chatHistoryAgentThread,
        Action<string>? textReceived = null,
        Action<string>? thinkReceived = null,
        Action<TokenUsage>? tokenUsed = null,
        Action<Exception>? exceptionThrow = null,
        CancellationToken cancellationToken = default)
    {
        StringBuilder nonThinkingContent = new(); //用于存储不含思考过程的最终回复
        ChatCompletionAgent agent = Configuration.DefaultThinking || GetThinkingRequester().IsOccupied
            ? chatCompletionAgent
            : chatCompletionAgentNotThinking;

        try
        {
            await foreach (AgentResponseItem<StreamingChatMessageContent> chatMessage in agent.InvokeStreamingAsync(
                               chatHistoryAgentThread, cancellationToken: cancellationToken))
            {
                string? content = chatMessage.Message.Content;
                if (content != null)
                {
                    //前置报文会对思考内容进行特殊处理，以便兼容思考模式
                    if (content.StartsWith(LFMCompatibleHandler.ThinkContentPrefix))
                    {
                        string reasoningPart = content.Substring(LFMCompatibleHandler.ThinkContentPrefix.Length);
                        thinkReceived?.Invoke(reasoningPart);
                    }
                    else
                    {
                        nonThinkingContent.Append(content);
                        textReceived?.Invoke(content);
                    }
                }

                var metaData = chatMessage.Message.Metadata;
                if (metaData != null)
                {
                    // 尝试从元数据中提取思考过程 (支持原生支持此字段的 SDK)
                    if (metaData.TryGetValue("ReasoningContent", out object? reasoning) ||
                        metaData.TryGetValue("reasoning_content", out reasoning))
                    {
                        string? reasoningStr = reasoning?.ToString();
                        if (string.IsNullOrEmpty(reasoningStr) == false)
                            thinkReceived?.Invoke(reasoningStr);
                    }

                    if (metaData.TryGetValue("Usage", out object? usage))
                    {
                        if (usage is ChatTokenUsage chatTokenUsage)
                        {
                            tokenUsed?.Invoke(new TokenUsage() {
                                Total = chatTokenUsage.TotalTokenCount,
                                Input = chatTokenUsage.InputTokenCount,
                                Output = chatTokenUsage.OutputTokenCount,
                                Cached = chatTokenUsage.InputTokenDetails?.CachedTokenCount ?? 0
                            });
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            exceptionThrow?.Invoke(e);
        }

        //受 SK 框架限制，思考只能存储在消息块中，所以需要额外的步骤修正内容。
        string aiMessage = nonThinkingContent.ToString();
        ChatMessageContent lastMsg = chatHistoryAgentThread.ChatHistory[^1];
        if (lastMsg.Role == AuthorRole.Assistant && (lastMsg.Content?.Contains(LFMCompatibleHandler.ThinkContentPrefix) ?? false))
            lastMsg.Content = aiMessage;

        return aiMessage;
    }

ChatCompletionAgent chatCompletionAgent = null!;
    ChatCompletionAgent chatCompletionAgentNotThinking = null!;
    readonly OccupationNotepad thinkingRequester = new();
    PythonPipeProcess? pythonPipe;
    LlamaCppRuntime? llamaCppRuntime;

    [Experimental("SKEXP0010")]
    protected override async Task OnAwake()
    {
        if (string.IsNullOrEmpty(Configuration.Endpoint))
            Configuration.Endpoint = storageSystem.GetProperty("endpoint", string.Empty)!;
        if (string.IsNullOrEmpty(Configuration.ApiKey))
            Configuration.ApiKey = storageSystem.GetProperty("apiKey", string.Empty)!;
        if (string.IsNullOrEmpty(Configuration.ModelId))
            Configuration.ModelId = storageSystem.GetProperty("modelId", string.Empty)!;

        // 内置 llama.cpp 模式：下载 GGUF 与 llama-server 并自启本地 OpenAI 兼容服务
        if (Configuration.UseLlamaCpp)
        {
            llamaCppRuntime = new LlamaCppRuntime(logger, "LiquidAI/LFM2.5-2.6B-GGUF", Configuration.LlamaCppModelFile, Configuration.LocalPort, Configuration.LlamaCppGpuLayers, Configuration.LlamaCppThreads);
            await llamaCppRuntime.StartAsync();
            Configuration.Endpoint = llamaCppRuntime.Endpoint;
            Configuration.ModelId = llamaCppRuntime.ModelId;
            Configuration.ApiKey = "local";
            logger.LogInformation("LFM llama.cpp 服务已连接：{Endpoint}", Configuration.Endpoint);
        }
        // 本地 transformers 推理模式：下载模型并启动本地 OpenAI 兼容服务
        else if (Configuration.UseLocalModel)
        {
            logger.LogInformation("正在通过 modelscope 下载 LFM 模型：{ModelId} ...", Configuration.LocalModelId);
            string modelPath = ModelDownloader.EnsureModelExisting(Configuration.LocalModelId);
            logger.LogInformation("模型就绪：{Path}", modelPath);

            pythonPipe = new PythonPipeProcess("lfm_server", PythonServerCode);
            await pythonPipe.StartAsync();
            string localEndpoint = await pythonPipe.InvokeAsync<string>("init",
                modelPath,
                Configuration.LocalPort,
                Configuration.Device,
                Configuration.MaxNewTokens,
                Configuration.Temperature,
                Configuration.TopP,
                Configuration.TopK,
                Configuration.RepetitionPenalty);

            // 等待本地服务就绪
            await WaitLocalServerReadyAsync(new Uri(localEndpoint));

            Configuration.Endpoint = localEndpoint;
            Configuration.ApiKey = "local";
            Configuration.ModelId = Configuration.LocalModelId;
            logger.LogInformation("LFM 本地推理服务已就绪：{Endpoint}", localEndpoint);
        }

        IKernelBuilder kernelBuilder = Kernel.CreateBuilder();
        RegisterChatCompletion(kernelBuilder);
        Kernel kernelService = kernelBuilder.Build();

        chatCompletionAgent = new() {
            Kernel = kernelService,
            Arguments = new KernelArguments(ProvidePromptExecutionSettings(true)),
        };
        chatCompletionAgentNotThinking = new() {
            Kernel = kernelService,
            Arguments = new KernelArguments(ProvidePromptExecutionSettings(false)),
        };
    }

    protected override async Task OnDestroy()
    {
        if (llamaCppRuntime != null)
        {
            await llamaCppRuntime.DisposeAsync();
            llamaCppRuntime = null;
        }
        if (pythonPipe != null)
        {
            await pythonPipe.DisposeAsync();
            pythonPipe = null;
        }
    }

    async Task WaitLocalServerReadyAsync(Uri endpoint)
    {
        using HttpClient client = new();
        Exception? last = null;
        DateTime deadline = DateTime.Now + TimeSpan.FromSeconds(120);
        while (DateTime.Now < deadline)
        {
            try
            {
                var response = await client.GetAsync(new Uri(endpoint, "/models"));
                if (response.IsSuccessStatusCode)
                    return;
                last = new Exception($"本地服务尚未就绪，HTTP {(int)response.StatusCode}");
            }
            catch (Exception e)
            {
                last = e;
            }
            await Task.Delay(1000);
        }
        throw last ?? new Exception("等待本地推理服务就绪超时（120秒）。");
    }

    void RegisterChatCompletion(IKernelBuilder kernelBuilder)
    {
        if (string.IsNullOrWhiteSpace(Configuration.ApiKey))
            throw new Exception("语言模型的key为空，请检查你的“LFM语言模型”插件配置是否正确。");

        // 强制使用 HTTP 1.1 以解决部分服务商在流式传输时可能出现的 HttpIOException
        SocketsHttpHandler handler = new() {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions {
                RemoteCertificateValidationCallback = delegate {
                    return true;
                }
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        // 使用通用处理器拦截并破解所有 OpenAI 兼容协议的思考过程字段
        LFMCompatibleHandler reasoningHandler = new(handler);

        HttpClient httpClient = new(reasoningHandler) {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        if (!string.IsNullOrWhiteSpace(Configuration.ExtraHeaders))
        {
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(Configuration.ExtraHeaders);
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "解析自定义请求头失败");
            }
        }

        kernelBuilder.AddOpenAIChatCompletion(
            endpoint: new Uri(Configuration.Endpoint),
            modelId: Configuration.ModelId,
            apiKey: Configuration.ApiKey,
            httpClient: httpClient
        );
    }

    [Experimental("SKEXP0010")]
    PromptExecutionSettings ProvidePromptExecutionSettings(bool thinking)
    {
        OpenAIPromptExecutionSettings settings = new();

        if (thinking && string.IsNullOrEmpty(Configuration.ReasoningEffort) == false)
            settings.ReasoningEffort = Configuration.ReasoningEffort;
        else
            settings.ReasoningEffort = null;

        settings.ExtraBody = new Dictionary<string, object?>();
        string body = thinking ? Configuration.ExtraBody : Configuration.ExtraBodyNotThinking;
        if (string.IsNullOrWhiteSpace(body) == false)
        {
            try
            {
                var bodyDict = JsonSerializer.Deserialize<Dictionary<string, object>>(body);
                if (bodyDict != null)
                {
                    foreach (var kvp in bodyDict)
                        settings.ExtraBody[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "解析自定义请求体失败");
            }
        }

        return settings;
    }

    const string PythonServerCode =
        """
        import sys, json, threading
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        from pathlib import Path
        import torch
        from transformers import AutoTokenizer, AutoModelForCausalLM, TextIteratorStreamer

        model = None
        tokenizer = None
        gen_kwargs = {}
        model_name = "LFM"

        def _load_model(model_path, device):
            global model, tokenizer, model_name
            model_name = Path(str(model_path)).name
            tokenizer = AutoTokenizer.from_pretrained(model_path, trust_remote_code=True)
            if device == "cuda":
                model = AutoModelForCausalLM.from_pretrained(model_path, device_map=None, torch_dtype="auto", trust_remote_code=True).to("cuda")
            elif device == "cpu":
                model = AutoModelForCausalLM.from_pretrained(model_path, device_map=None, torch_dtype="float32", trust_remote_code=True).to("cpu")
            else:
                model = AutoModelForCausalLM.from_pretrained(model_path, device_map="auto", torch_dtype="auto", trust_remote_code=True)
            return "ready"

        def _start_server(bind_port):
            class Handler(BaseHTTPRequestHandler):
                def log_message(self, *args):
                    pass
                def _send_json(self, obj):
                    raw = json.dumps(obj, ensure_ascii=False).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(raw)))
                    self.end_headers()
                    self.wfile.write(raw)
                def _send_sse(self, obj):
                    try:
                        self.wfile.write(("data: " + json.dumps(obj, ensure_ascii=False) + "\n\n").encode("utf-8"))
                        self.wfile.flush()
                    except Exception:
                        pass
                def do_GET(self):
                    p = self.path.rstrip("/")
                    if p.endswith("/models") or p.endswith("/v1/models"):
                        self._send_json({"object": "list", "data": [{"id": model_name}]})
                    else:
                        self.send_response(404)
                        self.end_headers()
                def do_POST(self):
                    p = self.path.rstrip("/")
                    if p.endswith("/chat/completions"):
                        length = int(self.headers.get("Content-Length", 0) or 0)
                        body = {}
                        if length > 0:
                            try:
                                body = json.loads(self.rfile.read(length).decode("utf-8"))
                            except Exception:
                                self.send_response(400)
                                self.end_headers()
                                return
                        messages = body.get("messages") or []
                        stream = body.get("stream", False)
                        params = {}
                        for key in ("temperature", "top_p", "top_k", "max_new_tokens", "repetition_penalty"):
                            v = body.get(key)
                            if v is not None:
                                params[key] = v
                        merged = dict(gen_kwargs)
                        merged.update(params)
                        self.send_response(200)
                        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
                        self.send_header("Cache-Control", "no-cache")
                        self.end_headers()
                        self.wfile.write(b'data: {"choices":[{"delta":{"role":"assistant"},"finish_reason":null}]}\n\n')
                        try:
                            chunk_id = "chatcmpl-local"
                            for text in _generate(messages, merged):
                                if text:
                                    self._send_sse({"id": chunk_id, "object": "chat.completion.chunk", "model": model_name, "choices": [{"index": 0, "delta": {"content": text}, "finish_reason": None}]})
                            self._send_sse({"id": chunk_id, "object": "chat.completion.chunk", "model": model_name, "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]})
                        except Exception as e:
                            self._send_sse({"error": f"{type(e).__name__}: {e}"})
                        self.wfile.write(b'data: [DONE]\n\n')
                    else:
                        self.send_response(404)
                        self.end_headers()

            class Server(ThreadingHTTPServer):
                daemon_threads = True

            attempt = 0
            while attempt < 50:
                port = bind_port + attempt
                try:
                    server = Server(("127.0.0.1", port), Handler)
                    break
                except OSError:
                    attempt += 1
            threading.Thread(target=server.serve_forever, daemon=True).start()
            return port

        def _generate(messages, params):
            inputs = tokenizer.apply_chat_template(messages, add_generation_prompt=True, return_tensors="pt", tokenize=True, return_dict=True)
            inputs = inputs.to(model.device)
            streamer = TextIteratorStreamer(tokenizer, skip_prompt=True, skip_special_tokens=True)

            def run():
                with torch.no_grad():
                    model.generate(**inputs, streamer=streamer, pad_token_id=tokenizer.eos_token_id, **params)

            threading.Thread(target=run, daemon=True).start()
            for text in streamer:
                yield text

        def init(model_path, port, device, max_new_tokens, temperature, top_p, top_k, repetition_penalty):
            _load_model(model_path, device)
            gen_kwargs.clear()
            gen_kwargs.update({
                "max_new_tokens": int(max_new_tokens),
                "do_sample": True,
                "temperature": float(temperature),
                "top_p": float(top_p),
                "top_k": int(top_k),
                "repetition_penalty": float(repetition_penalty),
            })
            actual_port = _start_server(int(port))
            return f"http://127.0.0.1:{actual_port}/v1"
        """;
}


