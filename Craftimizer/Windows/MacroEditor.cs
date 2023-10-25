using Craftimizer.Plugin;
using Craftimizer.Plugin.Utils;
using Craftimizer.Simulator;
using Craftimizer.Simulator.Actions;
using Craftimizer.Utils;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sim = Craftimizer.Simulator.SimulatorNoRandom;

namespace Craftimizer.Windows;

public sealed class MacroEditor : Window, IDisposable
{
    private const ImGuiWindowFlags WindowFlags = ImGuiWindowFlags.None;

    private CharacterStats characterStats = null!;
    public CharacterStats CharacterStats
    {
        get => characterStats;
        private set
        {
            characterStats = value with
            {
                Craftsmanship = Math.Clamp(value.Craftsmanship, 0, 9000),
                Control = Math.Clamp(value.Control, 0, 9000),
                CP = Math.Clamp(value.CP, 180, 1000),
                Level = Math.Clamp(value.Level, 1, 90),
                CLvl = Gearsets.CalculateCLvl(value.Level),
            };
        }
    }
    public RecipeData RecipeData { get; private set; }

    public record CrafterBuffs
    {
        public (int Craftsmanship, int Control) FC { get; init; }
        public (uint ItemId, bool IsHQ) Food { get; init; }
        public (uint ItemId, bool IsHQ) Medicine { get; init; }

        public CrafterBuffs(StatusList? statuses)
        {
            if (statuses == null)
                return;

            foreach (var status in statuses)
            {
                if (status.StatusId == 48)
                    Food = FoodStatus.ResolveFoodParam(status.Param) ?? default;
                else if (status.StatusId == 49)
                    Medicine = FoodStatus.ResolveFoodParam(status.Param) ?? default;
                else if (status.StatusId == 356)
                    FC = FC with { Craftsmanship = status.Param / 5 };
                else if (status.StatusId == 357)
                    FC = FC with { Control = status.Param / 5 };
            }
        }
    }
    public CrafterBuffs Buffs { get; set; }

    private List<int> HQIngredientCounts { get; set; }
    private int StartingQuality => RecipeData.CalculateStartingQuality(HQIngredientCounts);

    private sealed record SimulatedActionStep
    {
        public ActionType Action { get; init; }
        // State *after* executing the action
        public ActionResponse Response { get; set; }
        public SimulationState State { get; set; }
    };
    private List<SimulatedActionStep> Macro { get; set; } = new();
    private SimulationState InitialState { get; set; }
    private SimulationState State => Macro.Count > 0 ? Macro[^1].State : InitialState;
    private ActionType[] DefaultActions { get; }
    private Action<IEnumerable<ActionType>>? MacroSetter { get; set; }

    private CancellationTokenSource? SolverTokenSource { get; set; }
    private Exception? SolverException { get; set; }
    private int? SolverStartStepCount { get; set; }
    private bool SolverRunning => SolverTokenSource != null;

    private IDalamudTextureWrap ExpertBadge { get; }
    private IDalamudTextureWrap CollectibleBadge { get; }
    private IDalamudTextureWrap SplendorousBadge { get; }
    private IDalamudTextureWrap SpecialistBadge { get; }
    private IDalamudTextureWrap NoManipulationBadge { get; }
    private IDalamudTextureWrap ManipulationBadge { get; }
    private IDalamudTextureWrap WellFedBadge { get; }
    private IDalamudTextureWrap MedicatedBadge { get; }
    private IDalamudTextureWrap InControlBadge { get; }
    private IDalamudTextureWrap EatFromTheHandBadge { get; }
    private GameFontHandle AxisFont { get; }

    private string popupSaveAsMacroName = string.Empty;

    private string popupImportText = string.Empty;
    private string popupImportUrl = string.Empty;
    private string popupImportError = string.Empty;
    private CancellationTokenSource? popupImportUrlTokenSource;
    private MacroImport.RetrievedMacro? popupImportUrlMacro;

    public MacroEditor(CharacterStats characterStats, RecipeData recipeData, CrafterBuffs buffs, IEnumerable<ActionType> actions, Action<IEnumerable<ActionType>>? setter) : base("Craftimizer Macro Editor", WindowFlags, false)
    {
        CharacterStats = characterStats;
        RecipeData = recipeData;
        Buffs = buffs;
        MacroSetter = setter;
        DefaultActions = actions.ToArray();

        HQIngredientCounts = new();
        HQIngredientCounts.AddRange(Enumerable.Repeat(0, RecipeData.Ingredients.Count));

        RecalculateState();
        foreach (var action in DefaultActions)
            AddStep(action);

        ExpertBadge = Service.IconManager.GetAssemblyTexture("Graphics.expert_badge.png");
        CollectibleBadge = Service.IconManager.GetAssemblyTexture("Graphics.collectible_badge.png");
        SplendorousBadge = Service.IconManager.GetAssemblyTexture("Graphics.splendorous.png");
        SpecialistBadge = Service.IconManager.GetAssemblyTexture("Graphics.specialist.png");
        NoManipulationBadge = Service.IconManager.GetAssemblyTexture("Graphics.no_manip.png");
        ManipulationBadge = ActionType.Manipulation.GetIcon(RecipeData.ClassJob);
        WellFedBadge = Service.IconManager.GetIcon(LuminaSheets.StatusSheet.GetRow(48)!.Icon);
        MedicatedBadge = Service.IconManager.GetIcon(LuminaSheets.StatusSheet.GetRow(49)!.Icon);
        InControlBadge = Service.IconManager.GetIcon(LuminaSheets.StatusSheet.GetRow(356)!.Icon);
        EatFromTheHandBadge = Service.IconManager.GetIcon(LuminaSheets.StatusSheet.GetRow(357)!.Icon);
        AxisFont = Service.PluginInterface.UiBuilder.GetGameFontHandle(new(GameFontFamilyAndSize.Axis14));

        IsOpen = true;

        CollapsedCondition = ImGuiCond.Appearing;
        Collapsed = false;

        SizeConstraints = new() { MinimumSize = new(821, 750), MaximumSize = new(float.PositiveInfinity) };

        Service.WindowSystem.AddWindow(this);
    }

    public override void OnClose()
    {
        SolverTokenSource?.Cancel();
    }

    public override void Draw()
    {
        var modifiedInput = false;

        using (var table = ImRaii.Table("params", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("col2", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextColumn();
                modifiedInput = DrawCharacterParams();
                ImGui.TableNextColumn();
                modifiedInput |= DrawRecipeParams();
            }
        }

        if (modifiedInput)
            RecalculateState();

        ImGui.Separator();

        using (var table = ImRaii.Table("macroInfo", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthStretch, 2);
                ImGui.TableSetupColumn("col2", ImGuiTableColumnFlags.WidthStretch, 3);
                ImGui.TableNextColumn();
                DrawActionHotbars();
                ImGui.TableNextColumn();
                DrawMacroInfo();
                DrawMacro();
            }
        }
    }

    private bool DrawCharacterParams()
    {
        var oldStats = CharacterStats;

        ImGuiUtils.TextCentered("Crafter");

        var textClassName = RecipeData.ClassJob.GetAbbreviation();
        Vector2 textClassSize;
        {
            var layout = AxisFont.LayoutBuilder(textClassName).Build();
            textClassSize = new(layout.Width, layout.Height);
        }

        var imageSize = ImGui.GetFrameHeight();
        ImGuiUtils.AlignCentered(
                imageSize + 5 +
                textClassSize.X);
        ImGui.AlignTextToFramePadding();

        var uv0 = new Vector2(6, 3);
        var uv1 = uv0 + new Vector2(44);
        uv0 /= new Vector2(56);
        uv1 /= new Vector2(56);

        ImGui.Image(Service.IconManager.GetIcon(RecipeData.ClassJob.GetIconId()).ImGuiHandle, new Vector2(imageSize), uv0, uv1);
        ImGui.SameLine(0, 5);
        AxisFont.Text(textClassName);

        using (var statsTable = ImRaii.Table("stats", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            if (statsTable)
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthStretch, 4.5f);
                ImGui.TableSetupColumn("col2", ImGuiTableColumnFlags.WidthStretch, 3);
                ImGui.TableSetupColumn("col3", ImGuiTableColumnFlags.WidthStretch, 2);

                var inputWidth = ImGui.CalcTextSize(SqText.ToLevelString(9999)).X + ImGui.GetStyle().FramePadding.X * 2 + 5;

                void DrawStat(string name, int value, Action<int> setter)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(name);
                    ImGui.SameLine(0, 5);
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

                    var text = value.ToString();
                    if (ImGui.InputText($"##{name}", ref text, 8, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CharsDecimal))
                    {
                        setter(
                            int.TryParse(text, out var newLevel)
                            ? Math.Clamp(newLevel, 0, 9999)
                            : 0);
                    }
                }

                ImGui.TableNextColumn();
                DrawStat("Craftsmanship", CharacterStats.Craftsmanship, v => CharacterStats = CharacterStats with { Craftsmanship = v });

                ImGui.TableNextColumn();
                DrawStat("Control", CharacterStats.Control, v => CharacterStats = CharacterStats with { Control = v });

                ImGui.TableNextColumn();
                DrawStat("CP", CharacterStats.CP, v => CharacterStats = CharacterStats with { CP = v });
            }
        }

        using (var paramTable = ImRaii.Table("params", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            if (paramTable)
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("col2", ImGuiTableColumnFlags.WidthStretch, 3);
                ImGui.TableSetupColumn("col3", ImGuiTableColumnFlags.WidthStretch, 2);

                ImGui.TableNextColumn();
                var levelTextWidth = ImGui.CalcTextSize(SqText.ToLevelString(99)).X + ImGui.GetStyle().FramePadding.X * 2 + 5;
                ImGuiUtils.AlignCentered(
                    ImGui.CalcTextSize(SqText.LevelPrefix.ToIconString()).X + 5 +
                    levelTextWidth);

                ImGui.AlignTextToFramePadding();
                ImGui.Text(SqText.LevelPrefix.ToIconString());
                ImGui.SameLine(0, 3);
                ImGui.SetNextItemWidth(levelTextWidth);
                var levelText = SqText.ToLevelString(CharacterStats.Level);
                bool textChanged;
                unsafe
                {
                    textChanged = ImGui.InputText("##levelText", ref levelText, 8, ImGuiInputTextFlags.CallbackCharFilter | ImGuiInputTextFlags.AutoSelectAll, LevelInputCallback);
                }
                if (textChanged)
                    CharacterStats = CharacterStats with
                    {
                        Level =
                            SqText.TryParseLevelString(levelText, out var newLevel)
                            ? Math.Clamp(newLevel, 1, 90)
                            : 1
                    };
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"CLvl {Gearsets.CalculateCLvl(CharacterStats.Level)}");

                var disabledTint = new Vector4(0.5f, 0.5f, 0.5f, 0.75f);
                var imageButtonPadding = (int)(ImGui.GetStyle().FramePadding.Y / 2f);
                var imageButtonSize = imageSize - imageButtonPadding * 2;
                {
                    var v = CharacterStats.HasSplendorousBuff;
                    var tint = v ? Vector4.One : disabledTint;
                    if (ImGui.ImageButton(SplendorousBadge.ImGuiHandle, new Vector2(imageButtonSize), default, Vector2.One, imageButtonPadding, default, tint))
                        CharacterStats = CharacterStats with { HasSplendorousBuff = !v };
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(CharacterStats.HasSplendorousBuff ? $"Splendorous Tool" : "No Splendorous Tool");
                }
                ImGui.SameLine(0, 5);
                bool? newIsSpecialist = null;
                {
                    var v = CharacterStats.IsSpecialist;
                    var tint = new Vector4(0.99f, 0.97f, 0.62f, 1f) * (v ? Vector4.One : disabledTint);
                    if (ImGui.ImageButton(SpecialistBadge.ImGuiHandle, new Vector2(imageButtonSize), default, Vector2.One, imageButtonPadding, default, tint))
                    {
                        v = !v;
                        newIsSpecialist = v;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(v ? $"Specialist" : "Not a Specialist");
                }
                ImGui.SameLine(0, 5);
                {
                    var manipLevel = ActionType.Manipulation.GetActionRow(RecipeData.ClassJob).Action!.ClassJobLevel;
                    using (var d = ImRaii.Disabled(manipLevel > CharacterStats.Level))
                    {
                        var v = CharacterStats.CanUseManipulation && manipLevel <= CharacterStats.Level;
                        var tint = (v || manipLevel > CharacterStats.Level) ? disabledTint : Vector4.One;
                        if (ImGui.ImageButton(v ? ManipulationBadge.ImGuiHandle : NoManipulationBadge.ImGuiHandle, new Vector2(imageButtonSize), default, Vector2.One, imageButtonPadding, default, tint))
                            CharacterStats = CharacterStats with { CanUseManipulation = !v };
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(CharacterStats.CanUseManipulation && manipLevel <= CharacterStats.Level ? $"Can Use Manipulation" : "Cannot Use Manipulation");
                }

                ImGui.TableNextColumn();

                (uint ItemId, bool HQ)? newFoodBuff = null;
                var buffImageSize = new Vector2(imageSize * WellFedBadge.Width / WellFedBadge.Height, imageSize);
                ImGui.Image(WellFedBadge.ImGuiHandle, buffImageSize);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Food");
                ImGui.SameLine(0, 5);
                {
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    using var combo = ImRaii.Combo("##food", FormatItemBuff(Buffs.Food));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(FormatItemBuffDescription(Buffs.Food));
                    if (combo)
                    {
                        if (ImGui.Selectable("None", Buffs.Food.ItemId == 0))
                            newFoodBuff = (0, false);

                        foreach (var food in FoodStatus.OrderedFoods)
                        {
                            var row = (food.Item.RowId, false);
                            if (ImGui.Selectable(FormatItemBuff(row), Buffs.Food == row))
                                newFoodBuff = row;
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(FormatItemBuffDescription(row));

                            if (food.Item.CanBeHq)
                            {
                                row = (food.Item.RowId, true);
                                if (ImGui.Selectable(FormatItemBuff(row), Buffs.Food == row))
                                    newFoodBuff = row;
                                if (ImGui.IsItemHovered())
                                    ImGui.SetTooltip(FormatItemBuffDescription(row));
                            }
                        }
                    }
                }

                (uint ItemId, bool HQ)? newMedicineBuff = null;
                buffImageSize = new Vector2(imageSize * MedicatedBadge.Width / MedicatedBadge.Height, imageSize);
                ImGui.Image(MedicatedBadge.ImGuiHandle, buffImageSize);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Medicine");
                ImGui.SameLine(0, 5);
                {
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    using var combo = ImRaii.Combo("##medicine", FormatItemBuff(Buffs.Medicine));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(FormatItemBuffDescription(Buffs.Medicine));
                    if (combo)
                    {
                        if (ImGui.Selectable("None", Buffs.Medicine.ItemId == 0))
                            newMedicineBuff = (0, false);

                        foreach (var medicine in FoodStatus.OrderedMedicines)
                        {
                            var row = (medicine.Item.RowId, false);
                            if (ImGui.Selectable(FormatItemBuff(row), Buffs.Medicine == row))
                                newMedicineBuff = row;
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(FormatItemBuffDescription(row));

                            if (medicine.Item.CanBeHq)
                            {
                                row = (medicine.Item.RowId, true);
                                if (ImGui.Selectable(FormatItemBuff(row), Buffs.Medicine == row))
                                    newMedicineBuff = row;
                                if (ImGui.IsItemHovered())
                                    ImGui.SetTooltip(FormatItemBuffDescription(row));
                            }
                        }
                    }
                }

                ImGui.TableNextColumn();

                int? newFCCraftsmanshipBuff = null;
                buffImageSize = new Vector2(imageSize * MedicatedBadge.Width / MedicatedBadge.Height, imageSize);
                ImGui.Image(EatFromTheHandBadge.ImGuiHandle, buffImageSize);
                var fcBuffName = "Eat from the Hand";
                var fcStatName = "Craftsmanship";
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(fcBuffName);
                ImGui.SameLine(0, 5);
                {
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    using var combo = ImRaii.Combo("##fcCraftsmanship", FormatFCBuff(fcBuffName, Buffs.FC.Craftsmanship));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(FormatFCBuffDescription(fcBuffName, fcStatName, Buffs.FC.Craftsmanship));
                    if (combo)
                    {
                        if (ImGui.Selectable("None", Buffs.FC.Craftsmanship == 0))
                            newFCCraftsmanshipBuff = 0;

                        for (var i = 1; i <= 3; ++i)
                        {
                            if (ImGui.Selectable(FormatFCBuff(fcBuffName, i), Buffs.FC.Craftsmanship == i))
                                newFCCraftsmanshipBuff = i;
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(FormatFCBuffDescription(fcBuffName, fcStatName, i));
                        }
                    }
                }

                int? newFCControlBuff = null;
                buffImageSize = new Vector2(imageSize * MedicatedBadge.Width / MedicatedBadge.Height, imageSize);
                ImGui.Image(InControlBadge.ImGuiHandle, buffImageSize);
                fcBuffName = "In Control";
                fcStatName = "Control";
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(fcBuffName);
                ImGui.SameLine(0, 5);
                {
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    using var combo = ImRaii.Combo("##fcControl", FormatFCBuff(fcBuffName, Buffs.FC.Control));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(FormatFCBuffDescription(fcBuffName, fcStatName, Buffs.FC.Control));
                    if (combo)
                    {
                        if (ImGui.Selectable("None", Buffs.FC.Control == 0))
                            newFCControlBuff = 0;

                        for (var i = 1; i <= 3; ++i)
                        {
                            if (ImGui.Selectable(FormatFCBuff(fcBuffName, i), Buffs.FC.Control == i))
                                newFCControlBuff = i;
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(FormatFCBuffDescription(fcBuffName, fcStatName, i));
                        }
                    }
                }

                if (newIsSpecialist.HasValue || newFoodBuff.HasValue || newMedicineBuff.HasValue || newFCCraftsmanshipBuff.HasValue || newFCControlBuff.HasValue)
                {
                    var baseStat = GetBaseStats(CharacterStats);

                    Buffs = Buffs with
                    {
                        Food = newFoodBuff ?? Buffs.Food,
                        Medicine = newMedicineBuff ?? Buffs.Medicine,
                        FC = (newFCCraftsmanshipBuff ?? Buffs.FC.Craftsmanship, newFCControlBuff ?? Buffs.FC.Control)
                    };

                    var newStats = CharacterStats with { Craftsmanship = baseStat.Craftsmanship, Control = baseStat.Control, CP = baseStat.CP };
                    if (newIsSpecialist is { } isSpecialist)
                    {
                        if (isSpecialist != CharacterStats.IsSpecialist)
                        {
                            var craftsmanship = 20;
                            var control = 20;
                            var cp = 15;
                            if (!isSpecialist)
                            {
                                craftsmanship *= -1;
                                control *= -1;
                                cp *= -1;
                            }

                            newStats = newStats with
                            {
                                IsSpecialist = isSpecialist,
                                Craftsmanship = newStats.Craftsmanship + craftsmanship,
                                Control = newStats.Control + control,
                                CP = newStats.CP + cp
                            };
                        }
                    }

                    var bonus = CalculateConsumableBonus(newStats);
                    CharacterStats = newStats with
                    {
                        Craftsmanship = newStats.Craftsmanship + bonus.Craftsmanship,
                        Control = newStats.Control + bonus.Control,
                        CP = newStats.CP + bonus.CP
                    };
                }
            }
        }

        return oldStats != CharacterStats;
    }

    private static unsafe int LevelInputCallback(ImGuiInputTextCallbackData* data)
    {
        if (data->EventFlag == ImGuiInputTextFlags.CallbackCharFilter)
        {
            if (SqText.LevelNumReplacements.TryGetValue((char)data->EventChar, out var seChar))
                data->EventChar = seChar.ToIconChar();
            else
                return 1;
        }

        return 0;
    }

    private static string FormatItemBuff((uint ItemId, bool IsHQ) input)
    {
        if (input.ItemId == 0)
            return "None";

        var name = LuminaSheets.ItemSheet.GetRow(input.ItemId)?.Name.ToDalamudString().ToString() ?? $"Unknown ({input.ItemId})";
        return input.IsHQ ? $"{name} (HQ)" : name;
    }

    private static string FormatItemBuffDescription((uint ItemId, bool IsHQ) input)
    {
        var s = new StringBuilder(FormatItemBuff(input) + "\n");

        void AddStat(string name, FoodStatus.FoodStat? statNullable)
        {
            if (statNullable is not { } stat)
                return;

            var (value, max) = input.IsHQ ? (stat.ValueHQ, stat.MaxHQ) : (stat.Value, stat.Max);

            if (!stat.IsRelative)
                s.AppendLine($"{name} +{value}");
            else
                s.AppendLine($"{name} +{value}%% (Max {max})");
        }

        if (FoodStatus.TryGetFood(input.ItemId) is { } food)
        {
            AddStat("Craftsmanship", food.Craftsmanship);
            AddStat("Control", food.Control);
            AddStat("CP", food.CP);
        }
        return s.ToString();
    }

    private static string FormatFCBuff(string name, int level)
    {
        if (level == 0)
            return "None";

        return $"{name} {new string('I', level)}";
    }

    private static string FormatFCBuffDescription(string name, string statName, int level)
    {
        if (level == 0)
            return FormatFCBuff(name, level);

        return $"{FormatFCBuff(name, level)}\n{statName} +{level * 5}";
    }

    private (int Craftsmanship, int Control, int CP) GetBaseStats(CharacterStats stats)
    {
        var (craftsmanship, control, cp) = (stats.Craftsmanship, stats.Control, stats.CP);

        craftsmanship -= Buffs.FC.Craftsmanship * 5;
        control -= Buffs.FC.Control * 5;

        var food = FoodStatus.TryGetFood(Buffs.Food.ItemId);
        var medicine = FoodStatus.TryGetFood(Buffs.Medicine.ItemId);

        static void GetBaseStat(ref int val, bool isHq, FoodStatus.FoodStat? food, out float a, out int b)
        {
            a = 1;
            b = 0;
            if (food is { } stat)
            {
                if (stat.IsRelative)
                {
                    a = (isHq ? stat.ValueHQ : stat.Value) / 100f;
                    b = isHq ? stat.MaxHQ : stat.Max;
                }
                else
                    val -= isHq ? stat.ValueHQ : stat.Value;
            }
        }

        static int GetBaseStat2(int val, bool foodHq, FoodStatus.FoodStat? food, bool medicineHq, FoodStatus.FoodStat? medicine)
        {
            GetBaseStat(ref val, foodHq, food, out var a, out var b);
            GetBaseStat(ref val, medicineHq, medicine, out var c, out var d);
            return CalculateBaseStat(val, a, b, c, d);
        }

        craftsmanship = GetBaseStat2(craftsmanship, Buffs.Food.IsHQ, food?.Craftsmanship, Buffs.Medicine.IsHQ, medicine?.Craftsmanship);
        control = GetBaseStat2(control, Buffs.Food.IsHQ, food?.Control, Buffs.Medicine.IsHQ, medicine?.Control);
        cp = GetBaseStat2(cp, Buffs.Food.IsHQ, food?.CP, Buffs.Medicine.IsHQ, medicine?.CP);

        return (craftsmanship, control, cp);
    }

    private (int Craftsmanship, int Control, int CP) CalculateConsumableBonus(CharacterStats stats)
    {
        int craftsmanship = 0, control = 0, cp = 0;
        static int CalculateStatBonus(int val, bool isHq, FoodStatus.FoodStat? food)
        {
            if (food is { } stat)
            {
                if (stat.IsRelative)
                    return (int)Math.Min((isHq ? stat.ValueHQ : stat.Value) / 100f * val, isHq ? stat.MaxHQ : stat.Max);
                else
                    return isHq ? stat.ValueHQ : stat.Value;
            }
            return 0;
        }
        var food = FoodStatus.TryGetFood(Buffs.Food.ItemId);

        craftsmanship += CalculateStatBonus(stats.Craftsmanship, Buffs.Food.IsHQ, food?.Craftsmanship);
        control += CalculateStatBonus(stats.Control, Buffs.Food.IsHQ, food?.Control);
        cp += CalculateStatBonus(stats.CP, Buffs.Food.IsHQ, food?.CP);

        var medicine = FoodStatus.TryGetFood(Buffs.Medicine.ItemId);
        craftsmanship += CalculateStatBonus(stats.Craftsmanship, Buffs.Medicine.IsHQ, medicine?.Craftsmanship);
        control += CalculateStatBonus(stats.Control, Buffs.Medicine.IsHQ, medicine?.Control);
        cp += CalculateStatBonus(stats.CP, Buffs.Medicine.IsHQ, medicine?.CP);

        craftsmanship += Buffs.FC.Craftsmanship * 5;
        control += Buffs.FC.Control * 5;

        return (craftsmanship, control, cp);
    }

    // y: output stat
    // a: coefficient
    // b: max value for a product
    // c: coefficient
    // d: max value for c product
    // Implementation of https://www.desmos.com/calculator/qlj9f9qjqy for calculating x from y
    private static int CalculateBaseStat(int y, float a, int b, float c, int d)
    {
        if (y <= 0)
            return 0;

        if (d / c < b / a)
            (a, b, c, d) = (c, d, a, b);

        var dc = d / c;
        var ba = b / a;
        if (dc + b + d <= y)
            return y - b - d;
        else if (y <= (1 + a + c) * ba)
            return (int)Math.Ceiling(y / (a + c + 1));
        else
            return (int)Math.Ceiling((y - b) / (c + 1));
    }

    private bool DrawRecipeParams()
    {
        var oldStartingQuality = StartingQuality;

        ImGuiUtils.TextCentered("Recipe");

        var textStars = new string('★', RecipeData!.Table.Stars);
        var textStarsSize = Vector2.Zero;
        if (!string.IsNullOrEmpty(textStars))
        {
            var layout = AxisFont.LayoutBuilder(textStars).Build();
            textStarsSize = new(layout.Width, layout.Height);
        }
        var textLevel = SqText.LevelPrefix.ToIconChar() + SqText.ToLevelString(RecipeData.RecipeInfo.ClassJobLevel);
        var isExpert = RecipeData.RecipeInfo.IsExpert;
        var isCollectable = RecipeData.Recipe.ItemResult.Value!.IsCollectable;
        var imageSize = ImGui.GetFrameHeight();
        var textSize = ImGui.GetFontSize();
        var badgeSize = new Vector2(textSize * ExpertBadge.Width / ExpertBadge.Height, textSize);
        var badgeOffset = (imageSize - badgeSize.Y) / 2;

        var rightSideWidth =
            5 + ImGui.CalcTextSize(textLevel).X +
            (textStarsSize != Vector2.Zero ? textStarsSize.X + 3 : 0) +
            (isCollectable ? badgeSize.X + 3 : 0) +
            (isExpert ? badgeSize.X + 3 : 0);
        ImGui.AlignTextToFramePadding();

        ImGui.Image(Service.IconManager.GetIcon(RecipeData.Recipe.ItemResult.Value!.Icon).ImGuiHandle, new Vector2(imageSize));

        ImGui.SameLine(0, 5);

        ushort? newRecipe = null;
        {
            var recipe = RecipeData.Recipe;
            if (ImGuiUtils.SearchableCombo(
                "combo",
                ref recipe,
                LuminaSheets.RecipeSheet.Where(r => r.RecipeLevelTable.Row != 0 && r.ItemResult.Row != 0),
                AxisFont.ImFont,
                ImGui.GetContentRegionAvail().X - rightSideWidth,
                r => r.ItemResult.Value!.Name.ToDalamudString().ToString(),
                r => r.RowId.ToString(),
                r =>
                {
                    ImGui.TextUnformatted($"{r.ItemResult.Value!.Name.ToDalamudString()}");

                    var classJob = (ClassJob)r.CraftType.Row;
                    var textLevel = SqText.LevelPrefix.ToIconChar() + SqText.ToLevelString(r.RecipeLevelTable.Value!.ClassJobLevel);
                    var textLevelSize = ImGui.CalcTextSize(textLevel);
                    ImGui.SameLine();

                    var imageSize = AxisFont.ImFont.FontSize;
                    ImGuiUtils.AlignRight(
                        imageSize + 5 +
                        textLevelSize.X,
                        ImGui.GetContentRegionAvail().X);

                    var uv0 = new Vector2(6, 3);
                    var uv1 = uv0 + new Vector2(44);
                    uv0 /= new Vector2(56);
                    uv1 /= new Vector2(56);

                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ImGui.GetStyle().FramePadding.Y / 2);
                    ImGui.Image(Service.IconManager.GetIcon(classJob.GetIconId()).ImGuiHandle, new Vector2(imageSize), uv0, uv1);
                    ImGui.SameLine(0, 5);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (AxisFont.ImFont.FontSize - textLevelSize.Y) / 2);
                    ImGui.Text(textLevel);
                }))
            {
                newRecipe = (ushort)recipe.RowId;
            }
        }

        ImGui.SameLine(0, 5);
        ImGui.Text(textLevel);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"RLvl {RecipeData.RecipeInfo.RLvl}");

        if (textStarsSize != Vector2.Zero)
        {
            ImGui.SameLine(0, 3);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (imageSize - textStarsSize.Y) / 2);
            AxisFont.Text(textStars);
        }

        if (isCollectable)
        {
            ImGui.SameLine(0, 3);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + badgeOffset);
            ImGui.Image(CollectibleBadge.ImGuiHandle, badgeSize);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Collectible");
        }

        if (isExpert)
        {
            ImGui.SameLine(0, 3);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + badgeOffset);
            ImGui.Image(ExpertBadge.ImGuiHandle, badgeSize);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Expert Recipe");
        }

        using (var statsTable = ImRaii.Table("stats", 3, ImGuiTableFlags.BordersInnerV))
        {
            if (statsTable)
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("col2", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("col3", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Progress");
                ImGui.SameLine();
                ImGuiUtils.TextRight($"{RecipeData.RecipeInfo.MaxProgress}");

                ImGui.TableNextColumn();
                ImGui.Text("Quality");
                ImGui.SameLine();
                ImGuiUtils.TextRight($"{RecipeData.RecipeInfo.MaxQuality}");

                ImGui.TableNextColumn();
                ImGui.Text("Durability");
                ImGui.SameLine();
                ImGuiUtils.TextRight($"{RecipeData.RecipeInfo.MaxDurability}");
            }
        }

        using (var table = ImRaii.Table("ingredientTable", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            if (table)
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthStretch, 2);
                ImGui.TableSetupColumn("col2", ImGuiTableColumnFlags.WidthStretch, 2);
                ImGui.TableSetupColumn("col3", ImGuiTableColumnFlags.WidthStretch, 2);
                ImGui.TableSetupColumn("col4", ImGuiTableColumnFlags.WidthStretch, 2);

                var ingredients = RecipeData.Ingredients.GetEnumerator();
                var hqCount = HQIngredientCounts.GetEnumerator();

                ImGui.TableNextColumn();
                DrawIngredientHQEntry(0);
                DrawIngredientHQEntry(1);

                ImGui.TableNextColumn();
                DrawIngredientHQEntry(2);
                DrawIngredientHQEntry(3);

                ImGui.TableNextColumn();
                DrawIngredientHQEntry(4);
                DrawIngredientHQEntry(5);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ImGui.GetStyle().FramePadding.Y);
                ImGuiUtils.TextCentered($"Starting Quality");
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().FramePadding.Y);
                ImGuiUtils.TextCentered($"{StartingQuality}");
            }
        }

        if (newRecipe is { } recipeId)
        {
            RecipeData = new(recipeId);
            HQIngredientCounts.Clear();
            HQIngredientCounts.AddRange(Enumerable.Repeat(0, RecipeData.Ingredients.Count));
            return true;
        }

        return oldStartingQuality != StartingQuality;
    }

    private void DrawIngredientHQEntry(int idx)
    {
        if (idx >= RecipeData.Ingredients.Count)
        {
            ImGui.Dummy(new(0, ImGui.GetFrameHeight()));
            return;
        }

        var ingredient = RecipeData.Ingredients[idx];
        var hqCount = HQIngredientCounts[idx];

        var canHq = ingredient.Item.CanBeHq;
        var icon = Service.IconManager.GetHqIcon(ingredient.Item.Icon, canHq);
        var imageSize = ImGui.GetFrameHeight();

        using (var d = ImRaii.Disabled(!canHq))
            ImGui.Image(icon.ImGuiHandle, new Vector2(imageSize));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (canHq)
            {
                var perItem = RecipeData.CalculateItemStartingQuality(idx, 1);
                var total = RecipeData.CalculateItemStartingQuality(idx, hqCount);
                ImGui.SetTooltip($"{ingredient.Item.Name.ToDalamudString()} {SeIconChar.HighQuality.ToIconString()}\n+{perItem} Quality/Item{(total > 0 ? $"\n+{total} Quality" : "")}");
            }
            else
                ImGui.SetTooltip($"{ingredient.Item.Name.ToDalamudString()}");
        }
        ImGui.SameLine(0, 5);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (5 + ImGui.CalcTextSize("/").X + 5 + ImGui.CalcTextSize($"99").X));
        using var d2 = ImRaii.Disabled(!canHq);
        if (canHq)
        {
            var text = hqCount.ToString();
            if (ImGui.InputText($"##ingredient{idx}", ref text, 8, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CharsDecimal))
            {
                HQIngredientCounts[idx] =
                    int.TryParse(text, out var newCount)
                    ? Math.Clamp(newCount, 0, ingredient.Amount)
                    : 0;
            }
        }
        else
        {
            var text = ingredient.Amount.ToString();
            ImGui.InputText($"##ingredient{idx}", ref text, 8, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CharsDecimal);
        }
        ImGui.SameLine(0, 5);
        ImGui.AlignTextToFramePadding();
        ImGui.Text("/");
        ImGui.SameLine(0, 5);
        ImGui.AlignTextToFramePadding();
        ImGuiUtils.TextCentered($"{ingredient.Amount}");
    }

    private void DrawActionHotbars()
    {
        var sim = new Sim(State);

        var imageSize = ImGui.GetFrameHeight() * 2;
        var spacing = ImGui.GetStyle().ItemSpacing.Y;

        using var _color = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero);
        using var _color3 = ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4.Zero);
        using var _color2 = ImRaii.PushColor(ImGuiCol.ButtonActive, Vector4.Zero);
        using var _alpha = ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, ImGui.GetStyle().DisabledAlpha * .5f);
        foreach (var category in Enum.GetValues<ActionCategory>())
        {
            if (category == ActionCategory.Combo)
                continue;

            var actions = category.GetActions();
            using var panel = ImGuiUtils.GroupPanel(category.GetDisplayName(), -1, out var availSpace);
            var itemsPerRow = (int)MathF.Floor((availSpace + spacing) / (imageSize + spacing));
            var itemCount = actions.Count;
            var iterCount = (int)(Math.Ceiling((float)itemCount / itemsPerRow) * itemsPerRow);
            for (var i = 0; i < iterCount; i++)
            {
                if (i % itemsPerRow != 0)
                    ImGui.SameLine(0, spacing);
                if (i < itemCount)
                {
                    var actionBase = actions[i].Base();
                    var canUse = actionBase.CanUse(sim);
                    if (ImGui.ImageButton(actions[i].GetIcon(RecipeData!.ClassJob).ImGuiHandle, new(imageSize), default, Vector2.One, 0, default, !canUse ? new(1, 1, 1, ImGui.GetStyle().DisabledAlpha) : Vector4.One))
                        AddStep(actions[i]);
                    if (!canUse &&
                        (CharacterStats.Level < actionBase.Level ||
                            (actions[i] == ActionType.Manipulation && !CharacterStats.CanUseManipulation) ||
                            (actions[i] is ActionType.HeartAndSoul or ActionType.CarefulObservation && !CharacterStats.IsSpecialist)
                        )
                       )
                    {
                        Vector2 v1 = ImGui.GetItemRectMin(), v2 = ImGui.GetItemRectMax();
                        ImGui.PushClipRect(v1, v2, true);
                        (v1.X, v2.X) = (v2.X, v1.X);
                        ImGui.GetWindowDrawList().AddLine(v1, v2, ImGui.GetColorU32(new Vector4(1, 0, 0, ImGui.GetStyle().DisabledAlpha / 2)), 5);
                        ImGui.PopClipRect();
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip($"{actions[i].GetName(RecipeData!.ClassJob)}\n{actionBase.GetTooltip(sim, true)}");
                }
                else
                    ImGui.Dummy(new(imageSize));
            }
        }

        var minY = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y - ImGui.GetStyle().CellPadding.Y;
        if (SizeConstraints!.Value.MinimumSize.Y != minY)
            SizeConstraints = SizeConstraints.Value with { MinimumSize = SizeConstraints.Value.MinimumSize with { Y = minY } };
    }

    private void DrawMacroInfo()
    {
        using (var barsTable = ImRaii.Table("simBars", 2, ImGuiTableFlags.SizingStretchSame))
        {
            if (barsTable)
            {
                ImGui.TableSetupColumn("col1", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("col2", ImGuiTableColumnFlags.WidthStretch, 2);

                ImGui.TableNextColumn();
                var datas = new List<BarData>(3)
                {
                    new("Durability", Colors.Durability, State.Durability, RecipeData.RecipeInfo.MaxDurability, null, null),
                    new("Condition", default, 0, 0, null, State.Condition)
                };
                if (RecipeData.Recipe.ItemResult.Value!.IsCollectable)
                    datas.Add(new("Collectability", Colors.HQ, State.Collectability, State.MaxCollectability, $"{State.Collectability}", null));
                else if (RecipeData.Recipe.RequiredQuality > 0)
                    datas.Add(new("Quality %", Colors.HQ, State.Quality, RecipeData.Recipe.RequiredQuality, $"{(float)State.Quality / RecipeData.Recipe.RequiredQuality * 100:0}%", null));
                else if (RecipeData.RecipeInfo.MaxQuality > 0)
                    datas.Add(new("HQ %", Colors.HQ, State.HQPercent, 100, $"{State.HQPercent}%", null));
                DrawBars(datas);

                ImGui.TableNextColumn();
                datas = new List<BarData>(3)
                {
                    new("Progress", Colors.Progress, State.Progress, RecipeData.RecipeInfo.MaxProgress, null, null),
                    new("Quality", Colors.Quality, State.Quality, RecipeData.RecipeInfo.MaxQuality, null, null),
                    new("CP", Colors.CP, State.CP, CharacterStats.CP, null, null)
                };
                if (RecipeData.RecipeInfo.MaxQuality <= 0)
                    datas.RemoveAt(1);
                DrawBars(datas);
            }
        }

        using (var panel = ImGuiUtils.GroupPanel("Buffs", -1, out _))
        {
            using var _font = ImRaii.PushFont(AxisFont.ImFont);

            var iconHeight = ImGui.GetFrameHeight() * 1.75f;
            var durationShift = iconHeight * .2f;

            ImGui.Dummy(new(0, iconHeight + ImGui.GetStyle().ItemSpacing.Y + ImGui.GetTextLineHeight() - durationShift));
            ImGui.SameLine(0, 0);

            var effects = State.ActiveEffects;
            foreach (var effect in Enum.GetValues<EffectType>())
            {
                if (!effects.HasEffect(effect))
                    continue;

                using (var group = ImRaii.Group())
                {
                    var icon = effect.GetIcon(effects.GetStrength(effect));
                    var size = new Vector2(iconHeight * icon.Width / icon.Height, iconHeight);

                    ImGui.Image(icon.ImGuiHandle, size);
                    if (!effect.IsIndefinite())
                    {
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - durationShift);
                        ImGuiUtils.TextCentered($"{effects.GetDuration(effect)}", size.X);
                    }
                }
                if (ImGui.IsItemHovered())
                {
                    var status = effect.Status();
                    using var _reset = ImRaii.DefaultFont();
                    ImGui.SetTooltip($"{status.Name.ToDalamudString()}\n{status.Description.ToDalamudString()}");
                }
                ImGui.SameLine();
            }
        }
    }

    private readonly record struct BarData(string Name, Vector4 Color, float Value, float Max, string? Caption, Condition? Condition);
    private void DrawBars(IEnumerable<BarData> bars)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var totalSize = ImGui.GetContentRegionAvail().X;
        totalSize -= 2 * spacing;
        var textSize = bars.Max(b =>
        {
            if (b.Caption is { } caption)
                return ImGui.CalcTextSize(caption).X;
            // max (sp/2) "/" (sp/2) max
            return Math.Max(ImGui.CalcTextSize($"{b.Value:0}").X, ImGui.CalcTextSize($"{b.Max:0}").X) * 2
                + spacing
                + ImGui.CalcTextSize("/").X;
        });
        var maxSize = (textSize - 2 * spacing - ImGui.CalcTextSize("/").X) / 2;
        var barSize = totalSize - textSize - spacing;
        foreach (var bar in bars)
        {
            using var panel = ImGuiUtils.GroupPanel(bar.Name, totalSize, out _);
            if (bar.Condition is { } condition)
            {
                using (var g = ImRaii.Group())
                {
                    var size = ImGui.GetFrameHeight() + spacing + ImGui.CalcTextSize(condition.Name()).X;
                    ImGuiUtils.AlignCentered(size, totalSize);
                    ImGui.GetWindowDrawList().AddCircleFilled(
                        ImGui.GetCursorScreenPos() + new Vector2(ImGui.GetFrameHeight() / 2),
                        ImGui.GetFrameHeight() / 2,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(.35f, .35f, .35f, 0) + condition.GetColor(DateTime.UtcNow.TimeOfDay)));
                    ImGui.Dummy(new(ImGui.GetFrameHeight()));
                    ImGui.SameLine(0, spacing);
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(condition.Name());
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(condition.Description(CharacterStats.HasSplendorousBuff));
            }
            else
            {
                using (var color = ImRaii.PushColor(ImGuiCol.PlotHistogram, bar.Color))
                    ImGui.ProgressBar(Math.Clamp(bar.Value / bar.Max, 0, 1), new(barSize, ImGui.GetFrameHeight()), string.Empty);
                ImGui.SameLine(0, spacing);
                ImGui.AlignTextToFramePadding();
                if (bar.Caption is { } caption)
                    ImGuiUtils.TextRight(caption, textSize);
                else
                {
                    ImGuiUtils.TextRight($"{bar.Value:0}", maxSize);
                    ImGui.SameLine(0, spacing / 2);
                    ImGui.Text("/");
                    ImGui.SameLine(0, spacing / 2);
                    ImGuiUtils.TextRight($"{bar.Max:0}", maxSize);
                }
            }
        }
    }

    private void DrawMacro()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var imageSize = ImGui.GetFrameHeight() * 2;
        var lastState = InitialState;

        using var panel = ImGuiUtils.GroupPanel("Macro", -1, out var availSpace);
        ImGui.Dummy(new(0, imageSize));
        ImGui.SameLine(0, 0);

        var macroActionsHeight = ImGui.GetFrameHeightWithSpacing();
        var childHeight = ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y * 2 - ImGui.GetStyle().CellPadding.Y - macroActionsHeight - ImGui.GetStyle().ItemSpacing.Y * 2;

        using (var child = ImRaii.Child("##macroActions", new(availSpace, childHeight)))
        {
            var itemsPerRow = (int)Math.Max(1, MathF.Floor((ImGui.GetContentRegionAvail().X + spacing) / (imageSize + spacing)));
            using var _color = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero);
            using var _color3 = ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4.Zero);
            using var _color2 = ImRaii.PushColor(ImGuiCol.ButtonActive, Vector4.Zero);
            for (var i = 0; i < Macro.Count; i++)
            {
                if (i % itemsPerRow != 0)
                    ImGui.SameLine(0, spacing);
                var (action, response, state) = (Macro[i].Action, Macro[i].Response, Macro[i].State);
                var actionBase = action.Base();
                var failedAction = response != ActionResponse.UsedAction;
                using var id = ImRaii.PushId(i);
                if (ImGui.ImageButton(action.GetIcon(RecipeData!.ClassJob).ImGuiHandle, new(imageSize), default, Vector2.One, 0, default, failedAction ? new(1, 1, 1, ImGui.GetStyle().DisabledAlpha) : Vector4.One))
                    RemoveStep(i);
                if (response is ActionResponse.ActionNotUnlocked)
                {
                    Vector2 v1 = ImGui.GetItemRectMin(), v2 = ImGui.GetItemRectMax();
                    ImGui.PushClipRect(v1, v2, true);
                    (v1.X, v2.X) = (v2.X, v1.X);
                    ImGui.GetWindowDrawList().AddLine(v1, v2, ImGui.GetColorU32(new Vector4(1, 0, 0, ImGui.GetStyle().DisabledAlpha / 2)), 5);
                    ImGui.PopClipRect();
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    var sim = new Sim(lastState);
                    ImGui.SetTooltip($"{action.GetName(RecipeData!.ClassJob)}\n{actionBase.GetTooltip(sim, true)}");
                }
                lastState = state;
            }
        }

        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(default);
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(availSpace, 0), ImGui.GetColorU32(ImGuiCol.Border));
        ImGui.Dummy(default);
        DrawMacroActions(availSpace);
    }

    private void DrawMacroActions(float availWidth)
    {
        var height = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = availWidth - ((spacing + height) * (3 + (DefaultActions.Length > 0 ? 1 : 0))); // small buttons at the end
        var halfWidth = (width - spacing) / 2f;
        var quarterWidth = (halfWidth - spacing) / 2f;

        using (var _disabled = ImRaii.Disabled(SolverRunning))
        {
            if (MacroSetter != null)
            {
                if (ImGui.Button("Save", new(quarterWidth, height)))
                    SaveMacro();
                ImGui.SameLine();
                if (ImGui.Button("Save As", new(quarterWidth, height)))
                    ShowSaveAsPopup();
            }
            else
            {
                if (ImGui.Button("Save", new(halfWidth, height)))
                    ShowSaveAsPopup();
            }
        }
        DrawSaveAsPopup();
        ImGui.SameLine();
        if (SolverRunning)
        {
            if (SolverTokenSource?.IsCancellationRequested ?? false)
            {
                using var _disabled = ImRaii.Disabled();
                ImGui.Button("Stopping", new(halfWidth, height));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This might could a while, sorry! Please report\n" +
                                     "if this takes longer than a second.");
            }
            else
            {
                if (ImGui.Button("Stop", new(halfWidth, height)))
                    SolverTokenSource?.Cancel();
            }
        }
        else
        {
            if (ImGui.Button(SolverStartStepCount.HasValue ? "Regenerate" : "Generate", new(halfWidth, height)))
                CalculateBestMacro();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Suggest a way to finish the crafting recipe.\n" +
                                 "Results aren't perfect, and levels of success\n" +
                                 "can vary wildly depending on the solver's settings.");
        }
        ImGui.SameLine();
        if (ImGuiUtils.IconButtonSized(FontAwesomeIcon.Paste, new(height)))
            Service.Plugin.CopyMacro(Macro.Select(s => s.Action).ToArray());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy to Clipboard");
        ImGui.SameLine();
        using (var _disabled = ImRaii.Disabled(SolverRunning))
        {
            if (ImGuiUtils.IconButtonSized(FontAwesomeIcon.FileImport, new(height)))
                ShowImportPopup();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Import Macro");
        DrawImportPopup();
        ImGui.SameLine();
        if (DefaultActions.Length > 0)
        {
            using (var _disabled = ImRaii.Disabled(SolverRunning))
            {
                if (ImGuiUtils.IconButtonSized(FontAwesomeIcon.Undo, new(height)))
                {
                    SolverStartStepCount = null;
                    Macro.Clear();
                    foreach (var action in DefaultActions)
                        AddStep(action);
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Reset");
        }
        ImGui.SameLine();
        using (var _disabled = ImRaii.Disabled(SolverRunning))
        {
            if (ImGuiUtils.IconButtonSized(FontAwesomeIcon.Trash, new(height)))
            {
                SolverStartStepCount = null;
                Macro.Clear();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Clear");
    }

    private void ShowSaveAsPopup()
    {
        ImGui.OpenPopup($"##saveAsPopup");
        popupSaveAsMacroName = string.Empty;
        ImGui.SetNextWindowPos(ImGui.GetMousePos() - new Vector2(ImGui.CalcItemWidth() * .25f, ImGui.GetFrameHeight() + ImGui.GetStyle().WindowPadding.Y * 2));
    }

    private void DrawSaveAsPopup()
    {
        using var popup = ImRaii.Popup($"##saveAsPopup");
        if (popup)
        {
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(ImGui.CalcItemWidth());
            if (ImGui.InputTextWithHint($"##setName", "Name", ref popupSaveAsMacroName, 100, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (!string.IsNullOrWhiteSpace(popupSaveAsMacroName))
                {
                    var newMacro = new Macro() { Name = popupSaveAsMacroName, Actions = Macro.Select(s => s.Action).ToArray() };
                    Service.Configuration.AddMacro(newMacro);
                    MacroSetter = actions =>
                    {
                        newMacro.ActionEnumerable = actions;
                        Service.Configuration.Save();
                    };
                    ImGui.CloseCurrentPopup();
                }
            }
        }
    }

    private void ShowImportPopup()
    {
        ImGui.OpenPopup($"##importPopup");
        popupImportText = string.Empty;
        popupImportUrl = string.Empty;
        popupImportError = string.Empty;
        popupImportUrlMacro = null;
        popupImportUrlTokenSource = null;
    }

    private void DrawImportPopup()
    {
        const string ExampleMacro = "/mlock\n/ac \"Muscle Memory\" <wait.3>\n/ac Manipulation <wait.2>\n/ac Veneration <wait.2>\n/ac \"Waste Not II\" <wait.2>\n/ac Groundwork <wait.3>\n/ac Innovation <wait.2>\n/ac \"Preparatory Touch\" <wait.3>\n/ac \"Preparatory Touch\" <wait.3>\n/ac \"Preparatory Touch\" <wait.3>\n/ac \"Preparatory Touch\" <wait.3>\n/ac \"Great Strides\" <wait.2>\n/ac \"Byregot's Blessing\" <wait.3>\n/ac \"Careful Synthesis\" <wait.3>";
        const string ExampleUrl = "https://ffxivteamcraft.com/simulator/39630/35499/9XOZDZKhbVXJUIPXjM63";

        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSizeConstraints(new(400, 0), new(float.PositiveInfinity));
        using var popup = ImRaii.Popup($"##importPopup", ImGuiWindowFlags.Modal | ImGuiWindowFlags.NoMove);
        if (popup)
        {
            bool submittedText, submittedUrl;

            using (var panel = ImGuiUtils.GroupPanel("##text", -1, out var availWidth))
            {
                ImGui.AlignTextToFramePadding();
                ImGuiUtils.TextCentered("Paste your macro here");
                {
                    using var font = ImRaii.PushFont(UiBuilder.MonoFont);
                    ImGuiUtils.InputTextMultilineWithHint("", ExampleMacro, ref popupImportText, 2048, new(availWidth, ImGui.GetTextLineHeight() * 15 + ImGui.GetStyle().FramePadding.Y), ImGuiInputTextFlags.AutoSelectAll);
                }
                using (var _disabled = ImRaii.Disabled(popupImportUrlTokenSource != null))
                    submittedText = ImGui.Button("Import", new(availWidth, 0));
            }

            using (var panel = ImGuiUtils.GroupPanel("##url", -1, out var availWidth))
            {
                var availOffset = ImGui.GetContentRegionAvail().X - availWidth;

                ImGui.AlignTextToFramePadding();
                ImGuiUtils.TextCentered("or provide a url to it");
                ImGui.SameLine();
                using (var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                {
                    using var font = ImRaii.PushFont(UiBuilder.IconFont);
                    ImGuiUtils.TextRight(FontAwesomeIcon.InfoCircle.ToIconString(), ImGui.GetContentRegionAvail().X - availOffset);
                }
                if (ImGui.IsItemHovered())
                {
                    using var t = ImRaii.Tooltip();
                    ImGui.Text("Supported sites:");
                    ImGui.BulletText("ffxivteamcraft.com");
                    ImGui.BulletText("craftingway.app");
                    ImGui.Text("More suggestions are appreciated!");
                }
                ImGui.SetNextItemWidth(availWidth);
                submittedUrl = ImGui.InputTextWithHint("", ExampleUrl, ref popupImportUrl, 2048, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);
                using (var _disabled = ImRaii.Disabled(popupImportUrlTokenSource != null))
                    submittedUrl = ImGui.Button("Import", new(availWidth, 0)) || submittedUrl;
            }

            ImGui.Dummy(default);

            if (!string.IsNullOrWhiteSpace(popupImportError))
            {
                using (var c = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                    ImGui.TextWrapped(popupImportError);
                ImGui.Dummy(default);
            }

            if (ImGuiUtils.ButtonCentered("Nevermind", new(ImGui.GetContentRegionAvail().X / 2f, 0)))
            {
                popupImportUrlTokenSource?.Cancel();
                ImGui.CloseCurrentPopup();
            }

            if (popupImportUrlTokenSource == null)
            {
                if (submittedText)
                {
                    if (MacroImport.TryParseMacro(popupImportText) is { } parsedActions)
                    {
                        popupImportUrlTokenSource?.Cancel();
                        Macro.Clear();
                        foreach (var action in parsedActions)
                            AddStep(action);

                        Service.PluginInterface.UiBuilder.AddNotification($"Imported macro with {parsedActions.Count} step{(parsedActions.Count != 1 ? "s" : "")}", "Craftimizer Macro Imported", NotificationType.Success);
                        popupImportUrlTokenSource?.Cancel();
                        ImGui.CloseCurrentPopup();
                    }
                    else
                        popupImportError = "Could not find any actions to import. Is it a valid macro?";
                }
                if (submittedUrl)
                {
                    if (MacroImport.TryParseUrl(popupImportUrl, out _))
                    {
                        popupImportUrlTokenSource = new();
                        popupImportUrlMacro = null;
                        var token = popupImportUrlTokenSource.Token;
                        var url = popupImportUrl;

                        var task = Task.Run(() => MacroImport.RetrieveUrl(url, token), token);
                        _ = task.ContinueWith(t =>
                        {
                            if (token == popupImportUrlTokenSource.Token)
                                popupImportUrlTokenSource = null;
                        });
                        _ = task.ContinueWith(t =>
                        {
                            if (token.IsCancellationRequested)
                                return;

                            try
                            {
                                t.Exception!.Flatten().Handle(ex => ex is TaskCanceledException or OperationCanceledException);
                            }
                            catch (AggregateException e)
                            {
                                popupImportError = e.Message;
                                Log.Error(e, "Retrieving macro failed");
                            }
                        }, TaskContinuationOptions.OnlyOnFaulted);
                        _ = task.ContinueWith(t => popupImportUrlMacro = t.Result, TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                    else
                        popupImportError = "The url is not in the right format for any supported sites.";
                }
                if (popupImportUrlMacro is { Name: var name, Actions: var actions })
                {
                    Macro.Clear();
                    foreach(var action in actions)
                        AddStep(action);
                    Service.PluginInterface.UiBuilder.AddNotification($"Imported macro \"{name}\"", "Craftimizer Macro Imported", NotificationType.Success);

                    popupImportUrlTokenSource?.Cancel();
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        else
        {
            popupImportUrlTokenSource?.Cancel();
            popupImportUrlTokenSource = null;
        }
    }
    private void CalculateBestMacro()
    {
        SolverTokenSource?.Cancel();
        SolverTokenSource = new();
        SolverException = null;

        RevertPreviousMacro();
        SolverStartStepCount = Macro.Count;

        var token = SolverTokenSource.Token;
        var state = State;
        var task = Task.Run(() => CalculateBestMacroTask(state, token), token);
        _ = task.ContinueWith(t =>
        {
            if (token == SolverTokenSource.Token)
                SolverTokenSource = null;
        });
        _ = task.ContinueWith(t =>
        {
            if (token.IsCancellationRequested)
                return;

            try
            {
                t.Exception!.Flatten().Handle(ex => ex is TaskCanceledException or OperationCanceledException);
            }
            catch (AggregateException e)
            {
                SolverException = e;
                Log.Error(e, "Calculating macro failed");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void CalculateBestMacroTask(SimulationState state, CancellationToken token)
    {
        var config = Service.Configuration.SimulatorSolverConfig;

        token.ThrowIfCancellationRequested();

        var solver = new Solver.Solver(config, state) { Token = token };
        solver.OnLog += Log.Debug;
        solver.OnNewAction += a => AddStep(a, isSolver: true);
        solver.Start();
        _ = solver.GetTask().GetAwaiter().GetResult();

        token.ThrowIfCancellationRequested();
    }

    private void RevertPreviousMacro()
    {
        if (SolverStartStepCount is { } stepCount && stepCount < Macro.Count)
            Macro.RemoveRange(stepCount, Macro.Count - stepCount);
    }

    private void SaveMacro()
    {
        MacroSetter?.Invoke(Macro.Select(s => s.Action));
    }

    private void RecalculateState()
    {
        InitialState = new SimulationState(new(CharacterStats, RecipeData.RecipeInfo, StartingQuality));
        var sim = new Sim(InitialState);
        var lastState = InitialState;
        foreach (var step in Macro)
            lastState = ((step.Response, step.State) = sim.Execute(lastState, step.Action)).State;
    }

    private void AddStep(ActionType action, int index = -1, bool isSolver = false)
    {
        if (index < -1 || index >= Macro.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (!isSolver && SolverRunning)
            throw new InvalidOperationException("Cannot add steps while solver is running");
        if (!SolverRunning)
            SolverStartStepCount = null;

        if (index == -1)
        {
            var sim = new Sim(State);
            var resp = sim.Execute(State, action);
            Macro.Add(new() { Action = action, Response = resp.Response, State = resp.NewState });
        }
        else
        {
            var state = index == 0 ? InitialState : Macro[index - 1].State;
            var sim = new Sim(state);
            var resp = sim.Execute(state, action);
            Macro.Insert(index, new() { Action = action, Response = resp.Response, State = resp.NewState });
            state = resp.NewState;
            for (var i = index + 1; i < Macro.Count; i++)
                state = ((Macro[i].Response, Macro[i].State) = sim.Execute(state, Macro[i].Action)).State;
        }
    }

    private void RemoveStep(int index)
    {
        if (index < 0 || index >= Macro.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (SolverRunning)
            throw new InvalidOperationException("Cannot remove steps while solver is running");
        SolverStartStepCount = null;

        Macro.RemoveAt(index);

        var state = index == 0 ? InitialState : Macro[index - 1].State;
        var sim = new Sim(state);
        for (var i = index; i < Macro.Count; i++)
            state = ((Macro[i].Response, Macro[i].State) = sim.Execute(state, Macro[i].Action)).State;
    }

    public void Dispose()
    {
        Service.WindowSystem.RemoveWindow(this);

        AxisFont.Dispose();
    }
}
