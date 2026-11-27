using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>treasure</c> on both hosts: the chest, the native gold burst, the relic holders for 0 to 6 relics, Skip and Proceed.
/// What a touch on a holder does is the host's: TouchSpireUI's native release takes the relic on one tap; the launcher takes
/// a chest whose every relic is claimable (a single player's one-relic chest, or the Blitz chest's two relics for two rounds)
/// with the game's own single tap, and selects on the first press and takes on a second tap only when the chest offers a
/// choice (more visible relics than picks left, or another player) (<see cref="TakeTreasureRelicAsync"/> and
/// <see cref="CheckTreasurePressAwardAsync"/>). The room's
/// portrait framing and touch-target sizes are partials implemented by the TouchSpire host (HostRewardChecks.cs) and dropped
/// on the launcher.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunTreasureAsync(RunState state)
    {
        SaveManager.Instance.MarkFtueAsComplete("obtain_relic_ftue");
        Player player = state.Players[0];
        bool portrait = IsPortraitHost();
        (int Count, bool Skip)[] cases = portrait
            ? [(1, false), (0, false), (2, false), (3, false), (4, false), (5, false), (6, false), (1, true)]
            : [(1, false), (1, true)];
        foreach (var (count, skip) in cases)
        {
            string name = skip ? "treasure_skip" : $"treasure_{count}";
            await RunManager.Instance.EnterRoomDebug(RoomType.Treasure, showTransition: false);
            var room = FindFirst<NTreasureRoom>(_tree.Root)!;
            var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            if (count != 1) SetTreasureRewardRelics(count);
            var chest = room.GetNode<NTreasureButton>("%Chest");
            var coins = room.GetNode<GpuParticles2D>("%GoldExplosion");
            var collection = room.GetNode<NTreasureRoomRelicCollection>("%RelicCollection");
            int gold = player.Gold;
            await WaitSecondsAsync(0.5);
            if (count == 1) CheckTreasureLayout(name, skip ? "closed_skip" : "closed", room, collection);
            else if (!portrait) CheckTreasureLayout(name, "landscape", room, collection);
            if (portrait && count == 1 && !skip) await CaptureAsync("treasure_closed");
            await _pointer.TapAsync(CenterOf(chest));
            await WaitUntilAsync(() => collection.IsVisibleInTree() && coins.Emitting, "open chest and native gold burst", ShortWait);
            _report.Check(name + "_awarded_gold", player.Gold > gold && coins.Amount == player.Gold - gold,
                $"gold={gold} -> {player.Gold} coins={coins.Amount}");
            if (count <= 1) await CaptureAsync(name + "_opening");
            if (skip)
            {
                var proceed = room.ProceedButton;
                await WaitUntilAsync(() => proceed.IsEnabled && proceed.IsSkip, "native delayed treasure Skip", ShortWait);
                await WaitSecondsAsync(0.6);
                CheckTreasureLayout(name, "skip", room, collection);
                await CaptureAsync("treasure_skip_available");
                int relicCount = player.Relics.Count;
                await _pointer.TapAsync(CenterOf(proceed));
                await WaitUntilAsync(() => NMapScreen.Instance is { IsOpen: true }, "Skip opens native map", ShortWait);
                _report.Check("treasure_skip_leaves_reward_unclaimed", player.Relics.Count == relicCount,
                    "native Skip opened the map without granting a relic");
                continue;
            }
            if (count == 0)
            {
                _report.Check("treasure_empty_notice", room.GetNode<Control>("%EmptyLabel").IsVisibleInTree(), "native empty label and smoke played");
            }
            else
            {
                await WaitSecondsAsync(1.1);
                var holders = collection.HoldersInUse.ToArray();
                _report.Check(name + "_reward_count", holders.Length == count, $"holders={holders.Length}");
                CheckTreasureLayout(name, "open", room, collection);
                await CaptureAsync(name + "_rewards");
                int relicsBefore = player.Relics.Count;
                if (count == 1)
                {
                    await CheckTreasurePressAwardAsync(name, holders[0], player);
                }
                else
                {
                    await TakeTreasureRelicAsync(holders[^1]);
                    try
                    {
                        await WaitUntilAsync(() => sync.CurrentRelics is null || player.Relics.Count > relicsBefore,
                            "native selection completes the fixture", ShortWait);
                    }
                    catch (TimeoutException)
                    {
                        _report.Fail(name + "_relic_pick_answered", "the native relic collection was not answered");
                    }
                }
                try
                {
                    await WaitUntilAsync(() => player.Relics.Count > relicsBefore, "the picked relic is awarded", ShortWait);
                }
                catch (TimeoutException)
                {
                    // Reported by the check below with the counts.
                }
                _report.Check(name + "_one_relic_awarded", player.Relics.Count == relicsBefore + 1,
                    $"relics={relicsBefore}->{player.Relics.Count}");
            }
            if (count <= 1)
            {
                try
                {
                    await WaitUntilAsync(() => room.ProceedButton.IsEnabled && !room.ProceedButton.IsSkip,
                        "native treasure Proceed", ShortWait);
                }
                catch (TimeoutException)
                {
                    _report.Fail(name + "_proceed_ready", "the treasure room's Proceed did not become ready");
                    continue;
                }
                await WaitSecondsAsync(0.6);
                CheckTreasureLayout(name, "proceed", room, collection);
                if (count == 1)
                {
                    await CaptureAsync("treasure_proceed");
                    await _pointer.TapAsync(CenterOf(room.ProceedButton));
                    await WaitUntilAsync(() => NMapScreen.Instance is { IsOpen: true }, "Proceed opens native map", ShortWait);
                    _report.Pass("treasure_proceed_opens_map");
                    // The next debug room enters with its own native visibility/navigation.
                    NMapScreen.Instance!.Close();
                }
            }
        }
    }

    /// <summary>
    /// The shared treasure fixture: give the native synchronizer's current rewards exactly <paramref name="count"/> relics
    /// (the counts the room can receive online) without networking or replacing its UI. A fresh room rolls one relic for one
    /// round, so the extra common relics come from the model database and a multi-relic single-player chest then offers more
    /// relics than that one pick.
    /// </summary>
    private static void SetTreasureRewardRelics(int count)
    {
        if (RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is not List<RelicModel> candidates) return;
        if (count <= candidates.Count)
        {
            candidates.RemoveRange(count, candidates.Count - count);
            return;
        }
        candidates.AddRange(ModelDb.AllRelics
            .Where(r => r.Rarity == RelicRarity.Common && !candidates.Any(c => c.Id == r.Id))
            .Take(count - candidates.Count).ToArray());
    }

    /// <summary>
    /// The contract the harness expects for an open chest, read from the game state and never from a host's own
    /// <c>PhoneSelectConfirm</c> adapter: only a single player with no more visible relics than picks left can take every
    /// relic, and only such a chest keeps the game's native single tap. More visible relics than picks left, another
    /// player, or an unreadable state all need the select-then-confirm pass. Both hosts read the same game state, so a
    /// scenario can branch on the chest it really has instead of hardcoding one shape (the Blitz chest is two relics for
    /// two rounds in both v0.113.1 and v0.114.0, but a fixture that rolled differently must not be tapped as if it were).
    /// </summary>
    private static TreasureChestContract ReadTreasureChestContract(NTreasureRoomRelicCollection collection)
    {
        int holders = collection.HoldersInUse.Count;
        int players = RunManager.Instance.DebugOnlyGetState() is { } state ? state.Players.Count : -1;
        (int? picksLeft, string source) = TreasurePicksLeft();
        bool everyRelicClaimable = players == 1 && picksLeft is int picks && holders > 0 && holders <= picks;
        return new TreasureChestContract(!everyRelicClaimable, holders, players, picksLeft, source);
    }

    /// <summary>
    /// The local player's relic picks left in the open chest, from the game's <c>TreasureRoomRelicSynchronizer</c>: v0.113
    /// keeps <c>_roundsLeft</c>; later builds keep <c>_rounds</c> and <c>_currentRound</c>. Null when neither form is readable.
    /// </summary>
    private static (int? PicksLeft, string Source) TreasurePicksLeft()
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            object? sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            if (sync is null) return (null, "no synchronizer");
            Type type = sync.GetType();
            if (type.GetField("_roundsLeft", All)?.GetValue(sync) is int roundsLeft)
                return (roundsLeft, "_roundsLeft");
            if (type.GetField("_rounds", All)?.GetValue(sync) is int rounds &&
                type.GetField("_currentRound", All)?.GetValue(sync) is int current)
                return (rounds - current, $"_rounds {rounds} - _currentRound {current}");
            return (null, "no round fields");
        }
        catch (Exception error)
        {
            return (null, error.GetType().Name);
        }
    }

    /// <summary>
    /// The expected chest contract and the state it was read from. <see cref="NeedsConfirm"/> is true exactly when the chest
    /// must go through the host's select-then-confirm pass.
    /// </summary>
    private readonly struct TreasureChestContract
    {
        internal TreasureChestContract(bool needsConfirm, int holders, int players, int? picksLeft, string picksSource)
        {
            NeedsConfirm = needsConfirm;
            Holders = holders;
            Players = players;
            PicksLeft = picksLeft;
            PicksSource = picksSource;
        }

        internal bool NeedsConfirm { get; }
        internal int Holders { get; }
        internal int Players { get; }
        internal int? PicksLeft { get; }
        internal string PicksSource { get; }

        internal string Detail => $"holders={Holders} players={Players} " +
            $"picksLeft={(PicksLeft?.ToString() ?? "unknown")} ({PicksSource}) → " +
            (NeedsConfirm ? "select-confirm" : "native single tap");
    }
}
