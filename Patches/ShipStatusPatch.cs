using HarmonyLib;
using UnityEngine;
using Hazel;
using System;
using System.Linq;
using System.Collections.Generic;
using static TOHE.Translator;
using TOHE.Roles.Crewmate;

namespace TOHE;

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.FixedUpdate))]
class ShipFixedUpdatePatch
{
    public static void Postfix(ShipStatus __instance)
    {
        //Above here, all of us will execute
        if (!AmongUsClient.Instance.AmHost) return;

        //Below here, only the host performs
        if (Main.IsFixedCooldown && Main.RefixCooldownDelay >= 0)
        {
            Main.RefixCooldownDelay -= Time.fixedDeltaTime;
        }
        else if (!float.IsNaN(Main.RefixCooldownDelay))
        {
            Utils.MarkEveryoneDirtySettings();
            Main.RefixCooldownDelay = float.NaN;
            Logger.Info("Refix Cooldown", "CoolDown");
        }
    }
}
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.UpdateSystem), typeof(SystemTypes), typeof(PlayerControl), typeof(MessageReader))]
public static class MessageReaderUpdateSystemPatch
{
    public static void Prefix(ShipStatus __instance, [HarmonyArgument(0)] SystemTypes systemType, [HarmonyArgument(1)] PlayerControl player, [HarmonyArgument(2)] MessageReader reader)
    {
        RepairSystemPatch.Prefix(__instance, systemType, player, MessageReader.Get(reader).ReadByte());
    }
    public static void Postfix(ShipStatus __instance, [HarmonyArgument(0)] SystemTypes systemType, [HarmonyArgument(1)] PlayerControl player, [HarmonyArgument(2)] MessageReader reader)
    {
        RepairSystemPatch.Postfix(__instance, systemType, player, MessageReader.Get(reader).ReadByte());
    }
}
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.UpdateSystem), typeof(SystemTypes), typeof(PlayerControl), typeof(byte))]
class RepairSystemPatch
{
    public static bool Prefix(ShipStatus __instance,
        [HarmonyArgument(0)] SystemTypes systemType,
        [HarmonyArgument(1)] PlayerControl player,
        [HarmonyArgument(2)] byte amount)
    {
        Logger.Msg("SystemType: " + systemType.ToString() + ", PlayerName: " + player.GetNameWithRole().RemoveHtmlTags() + ", amount: " + amount, "RepairSystem");

        if (RepairSender.enabled && AmongUsClient.Instance.NetworkMode != NetworkModes.OnlineGame)
        {
            Logger.SendInGame("SystemType: " + systemType.ToString() + ", PlayerName: " + player.GetNameWithRole().RemoveHtmlTags() + ", amount: " + amount);
        }

        if (!AmongUsClient.Instance.AmHost) return true;

        if (Options.DisableSabotage.GetBool() && systemType == SystemTypes.Sabotage)
        {
            return false;
        }

        if (player.Is(CustomRoles.Fool) && !Main.MeetingIsStarted && 
            systemType != SystemTypes.Sabotage &&
            (systemType is
                SystemTypes.Reactor or
                SystemTypes.Laboratory or
                SystemTypes.HeliSabotage or
                SystemTypes.LifeSupp or
                SystemTypes.Comms or
                SystemTypes.Electrical))
        {
            return false;
        }

        // Fast fix critical saboatge
        switch (player.GetCustomRole())
        {
            case CustomRoles.SabotageMaster:
                SabotageMaster.RepairSystem(__instance, systemType, amount, player.PlayerId);
                break;
            case CustomRoles.Alchemist when Alchemist.FixNextSabo:
                Alchemist.RepairSystem(systemType, amount);
                break;
        }

        if (systemType == SystemTypes.Electrical && 0 <= amount && amount <= 4 && Main.NormalOptions.MapId == 4)
        {
            if (Options.DisableAirshipViewingDeckLightsPanel.GetBool() && Vector2.Distance(player.transform.position, new(-12.93f, -11.28f)) <= 2f) return false;
            if (Options.DisableAirshipGapRoomLightsPanel.GetBool() && Vector2.Distance(player.transform.position, new(13.92f, 6.43f)) <= 2f) return false;
            if (Options.DisableAirshipCargoLightsPanel.GetBool() && Vector2.Distance(player.transform.position, new(30.56f, 2.12f)) <= 2f) return false;
        }

        if (player.Is(CustomRoles.Unlucky) && player.IsAlive()
            && (systemType is SystemTypes.Doors))
        {
            var Ue = IRandom.Instance;
            if (Ue.Next(1, 100) < Options.UnluckySabotageSuicideChance.GetInt())
            {
                player.RpcMurderPlayerV3(player);
                Main.PlayerStates[player.PlayerId].deathReason = PlayerState.DeathReason.Suicide;
                return false;
            }
        }

        return true;
    }

    // Fast fix lights
    public static void Postfix(ShipStatus __instance,
        [HarmonyArgument(0)] SystemTypes systemType,
        [HarmonyArgument(1)] PlayerControl player,
        [HarmonyArgument(2)] byte amount)
    {
        Camouflage.CheckCamouflage();

        if (systemType == SystemTypes.Electrical && 0 <= amount && amount <= 4)
        {
            var SwitchSystem = ShipStatus.Instance.Systems[SystemTypes.Electrical].Cast<SwitchSystem>();
            if (SwitchSystem != null && SwitchSystem.IsActive)
            {
                switch (player.GetCustomRole())
                {
                    case CustomRoles.SabotageMaster:
                        Logger.Info($"{player.GetNameWithRole().RemoveHtmlTags()} instant-fix-lights", "SwitchSystem");
                        SabotageMaster.SwitchSystemRepair(SwitchSystem, amount, player.PlayerId);
                        break;
                    case CustomRoles.Alchemist when Alchemist.FixNextSabo:
                        Logger.Info($"{player.GetNameWithRole().RemoveHtmlTags()} instant-fix-lights", "SwitchSystem");
                        SwitchSystem.ActualSwitches = 0;
                        SwitchSystem.ExpectedSwitches = 0;
                        Alchemist.FixNextSabo = false;
                        break;
                }
            }
        }
    }
    public static void CheckAndOpenDoorsRange(ShipStatus __instance, int amount, int min, int max)
    {
        var Ids = new List<int>();
        for (var i = min; i <= max; i++)
        {
            Ids.Add(i);
        }
        CheckAndOpenDoors(__instance, amount, Ids.ToArray());
    }
    private static void CheckAndOpenDoors(ShipStatus __instance, int amount, params int[] DoorIds)
    {
        if (!DoorIds.Contains(amount)) return;
        foreach (var id in DoorIds)
        {
            __instance.RpcUpdateSystem(SystemTypes.Doors, (byte)id);
        }
    }
}
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CloseDoorsOfType))]
class CloseDoorsPatch
{
    public static bool Prefix(ShipStatus __instance)
    {
        return !(Options.DisableCloseDoor.GetBool());
    }
}
/*[HarmonyPatch(typeof(SwitchSystem), nameof(SwitchSystem.UpdateSystem))]
class SwitchSystemRepairPatch
{
    public static void Postfix(SwitchSystem __instance, [HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader reader)
    {
        var amount = reader.FastByte();

        if (player.Is(CustomRoles.SabotageMaster))
            SabotageMaster.SwitchSystemRepair(__instance, amount, player.PlayerId);

        if (player.Is(CustomRoles.Repairman))
            Repairman.SwitchSystemRepair(__instance, amount);

        if (player.Is(CustomRoles.Alchemist) && Alchemist.FixNextSabo == true)
        {
            if (amount is >= 0 and <= 4)
            {
                __instance.ActualSwitches = 0;
                __instance.ExpectedSwitches = 0;
            }
            Alchemist.FixNextSabo = false;
        }
    }
}*/
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
class StartPatch
{
    public static void Postfix()
    {
        Logger.CurrentMethod();
        Logger.Info("-----------Start of game-----------", "Phase");

        Utils.CountAlivePlayers(true);

        if (Options.AllowConsole.GetBool())
        {
            if (!BepInEx.ConsoleManager.ConsoleActive && BepInEx.ConsoleManager.ConsoleEnabled)
                BepInEx.ConsoleManager.CreateConsole();
        }
        else
        {
            if (BepInEx.ConsoleManager.ConsoleActive && !DebugModeManager.AmDebugger)
            {
                BepInEx.ConsoleManager.DetachConsole();
                Logger.SendInGame(GetString("Warning.CanNotUseBepInExConsole"));
            }
        }
    }
}
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.StartMeeting))]
class StartMeetingPatch
{
    public static void Prefix(ShipStatus __instance, PlayerControl reporter, GameData.PlayerInfo target)
    {
        MeetingStates.ReportTarget = target;
        MeetingStates.DeadBodies = UnityEngine.Object.FindObjectsOfType<DeadBody>();
    }
}
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Begin))]
class BeginPatch
{
    public static void Postfix()
    {
        Logger.CurrentMethod();

        //Should the initial setup of the host's position be done here?
    }
}
[HarmonyPatch(typeof(GameManager), nameof(GameManager.CheckTaskCompletion))]
class CheckTaskCompletionPatch
{
    public static bool Prefix(ref bool __result)
    {
        if (Options.DisableTaskWin.GetBool() || Options.NoGameEnd.GetBool() || TaskState.InitialTotalTasks == 0)
        {
            __result = false;
            return false;
        }
        return true;
    }
}