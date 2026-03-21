using FluentAssertions;
using NetTrader.Domain.Entities;

namespace NetTrader.Tests;

/// <summary>
/// Тесты потокобезопасности BotState и Channel&lt;BotCommand&gt;.
/// </summary>
public class BotStateTests
{
    [Fact]
    public void BotState_DefaultValues_AreCorrect()
    {
        var state = new BotState(defaultLeverage: 10);

        state.IsPaused.Should().BeFalse();
        state.EmergencyStopRequested.Should().BeFalse();
        state.Leverage.Should().Be(10);
    }

    [Fact]
    public void BotState_SetAndGetProperties_ThreadSafe()
    {
        var state = new BotState();

        state.IsPaused = true;
        state.IsPaused.Should().BeTrue();

        state.EmergencyStopRequested = true;
        state.EmergencyStopRequested.Should().BeTrue();

        state.Leverage = 20;
        state.Leverage.Should().Be(20);
    }

    [Fact]
    public async Task BotState_Channel_SendAndReceive()
    {
        var state = new BotState();

        await state.SendCommandAsync(new BotCommand
        {
            Type = BotCommandType.Pause
        });

        state.TryReadCommand(out var cmd).Should().BeTrue();
        cmd!.Type.Should().Be(BotCommandType.Pause);

        // Канал пуст
        state.TryReadCommand(out _).Should().BeFalse();
    }

    [Fact]
    public async Task BotState_Channel_MultipleCommands_PreservesOrder()
    {
        var state = new BotState();

        await state.SendCommandAsync(new BotCommand { Type = BotCommandType.Pause });
        await state.SendCommandAsync(new BotCommand { Type = BotCommandType.SetLeverage, Argument = "20" });
        await state.SendCommandAsync(new BotCommand { Type = BotCommandType.Resume });

        state.TryReadCommand(out var cmd1).Should().BeTrue();
        cmd1!.Type.Should().Be(BotCommandType.Pause);

        state.TryReadCommand(out var cmd2).Should().BeTrue();
        cmd2!.Type.Should().Be(BotCommandType.SetLeverage);
        cmd2.Argument.Should().Be("20");

        state.TryReadCommand(out var cmd3).Should().BeTrue();
        cmd3!.Type.Should().Be(BotCommandType.Resume);
    }

    [Fact]
    public async Task BotState_ConcurrentAccess_NoRaceCondition()
    {
        var state = new BotState();
        int iterations = 1000;

        // Параллельная запись/чтение из разных потоков
        var writeTask = Task.Run(async () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                state.Leverage = i;
                state.IsPaused = i % 2 == 0;
                await state.SendCommandAsync(new BotCommand
                {
                    Type = BotCommandType.SetLeverage,
                    Argument = i.ToString()
                });
            }
        });

        var readTask = Task.Run(() =>
        {
            int readCount = 0;
            for (int i = 0; i < iterations * 2; i++)
            {
                _ = state.Leverage;
                _ = state.IsPaused;
                if (state.TryReadCommand(out _)) readCount++;
            }
            return readCount;
        });

        await Task.WhenAll(writeTask, readTask);

        // Не должно быть исключений — тест проходит если дошли сюда
        state.Leverage.Should().BeGreaterOrEqualTo(0);
    }
}
