using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Zhenyao.Dice;

public class DiceModuleConfig
{
    [DisplayName("默认面数")]
    [Description("骰子的默认面数")]
    public int DefaultSides { get; set; } = 6;
}

[Module("投骰子",
    "可以进行骰子投掷，支持自定义面数",
    defaultCategory: "真央插件",
    EditorUI = null
)]
public class DiceModule(
    XmlFunctionCaller functionCaller,
    ILogger<DiceModule> logger,
    Interactor<DiceModule> interactor
) : ChatBehaviour, IConfigurable<DiceModuleConfig>
{
    public DiceModuleConfig Configuration { get; set; } = null!;

    [XmlFunction(FunctionMode.OneShot)]
    [Description("投掷一个骰子")]
    public Task Roll([Description("骰子面数")] int? sides = null)
    {
        if (sides == null)
            sides = Configuration.DefaultSides;

        if (sides < 1)
            throw new Exception("骰子面数必须大于 0");

        int result = Random.Shared.Next(1, sides.Value + 1);
        interactor.Poke($"投出了 {result} 点（{sides}面骰子）");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("投掷多个骰子")]
    public Task RollMultiple([Description("骰子数量")] int count, [Description("每个骰子面数")] int? sides = null)
    {
        if (count <= 0)
            throw new Exception("骰子数量必须大于 0");

        if (sides == null)
            sides = Configuration.DefaultSides;

        if (sides < 1)
            throw new Exception("骰子面数必须大于 0");

        int total = 0;
        var results = new int[count];
        for (int i = 0; i < count; i++)
        {
            results[i] = Random.Shared.Next(1, sides.Value + 1);
            total += results[i];
        }

        interactor.Poke($"投了 {count} 个{sides}面骰子，结果：{string.Join("+", results)} = {total}");
        return Task.CompletedTask;
    }

    protected override Task OnAwake()
    {
        XmlHandler xmlHandler = new(this) {
            Description = "此服务提供投骰子功能，可以随机生成骰子点数",
            Explanation = "支持单颗和多颗骰子投掷，可自定义面数和数量"
        };
        functionCaller.RegisterHandler(xmlHandler,
            DocumentMode.Implicit,
            cancellationToken: DestroyCancellationToken
        );
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        return Task.CompletedTask;
    }
}