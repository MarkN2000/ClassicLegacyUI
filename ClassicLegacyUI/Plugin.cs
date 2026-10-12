using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisResoniteWrapper;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace ClassicLegacyUI;

[ResonitePlugin("markn2000.ClassicLegacyUI", "ClassicLegacyUI", "0.2.0", "markn2000", "")]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(BepisResoniteWrapper.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    private static Plugin? instance;
    private volatile Dictionary<Type, (colorX? Color, float? Slant, colorX? Container)> styles = new();
    internal static readonly colorX CommonColor = new(1f, 0.73f, 0.09f, 0.8f, ColorProfile.sRGB);
    internal static readonly colorX PanelColor = new(1f, 0.88f, 0.6f, 0.8f, ColorProfile.sRGB);

    public override void Load()
    {
        Config.SaveOnConfigSet = false;
        instance = this;
        ResoniteHooks.OnEngineReady += OnEngineReady;
    }

    private void OnEngineReady()
    {
        ResoniteHooks.OnEngineReady -= OnEngineReady;
        try
        {
            if (!TomlTypeConverter.CanConvert(typeof(colorX)))
                throw new InvalidOperationException("Shimの色変換が登録されていません。");
            bool newFile = !File.Exists(Config.ConfigFilePath);
            BackupExistingConfig(Config.ConfigFilePath);
            styles = ReadStyles(Config, message => Log.LogWarning(message));
            if (newFile) Config.Save();
            MethodInfo target = AccessTools.DeclaredMethod(typeof(DevCreateNewForm), "RunAction",
                [typeof(IButton), typeof(ButtonEventData), typeof(string)])
                ?? throw new MissingMethodException("DevCreateNewForm.RunActionが見つかりません。");
            HarmonyInstance.Patch(target, transpiler: new HarmonyMethod(typeof(Plugin), nameof(Transpile)));
            Config.SettingChanged += OnSettingChanged;
            Log.LogInfo("ClassicLegacyUIを読み込みました。設定画面の変更は次に作るUIから反映します。");
        }
        catch (Exception error)
        {
            HarmonyInstance.UnpatchSelf();
            styles.Clear();
            Log.LogError($"初期化できないため外観変更を無効にしました: {error}");
        }
    }

    public override bool Unload()
    {
        ResoniteHooks.OnEngineReady -= OnEngineReady;
        Config.SettingChanged -= OnSettingChanged;
        HarmonyInstance.UnpatchSelf();
        instance = null;
        return true;
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs args) =>
        styles = ReadStyles(Config, message => Log.LogWarning(message));

    internal static void BackupExistingConfig(string path)
    {
        string backup = path + ".before-0.2.0.bak";
        if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
    }

    internal static Dictionary<Type, (colorX? Color, float? Slant, colorX? Container)> ReadStyles(
        ConfigFile config, Action<string> warn)
    {
        config.SaveOnConfigSet = false;
        var result = new Dictionary<Type, (colorX? Color, float? Slant, colorX? Container)>();
        bool enabled = Read<bool>(config, "General", "Enabled", true, "新規作成するUIの外観変更を有効にする。", warn) == true;
        Add(typeof(LegacyButton), "Button", CommonColor, 22.5f);
        Add(typeof(LegacyCheckbox), "Checkbox", CommonColor);
        Add(typeof(LegacyHorizontalChoiceBar), "HorizontalChoiceBar", CommonColor, 22.5f);
        Add(typeof(LegacyNumericUpDown), "NumericUpDown", CommonColor, 22.5f);
        Add(typeof(LegacyCanvasPanel), "Panel", PanelColor);
        Add(typeof(LegacyProgressBar), "ProgressBar", new colorX(1f, 0.73f, 0.09f, 1f, ColorProfile.sRGB), 22.5f);
        Add(typeof(LegacyRadio), "Radio", CommonColor);
        Add(typeof(LegacySlider), "Slider", CommonColor, 22.5f);
        Add(typeof(LegacyTextField), "TextField", CommonColor, 22.5f);
        return result;

        void Add(Type type, string section, colorX defaultColor, float? defaultSlant = null)
        {
            bool active = Read<bool>(config, section, "Enabled", true, "この種類の外観変更を有効にする。", warn) == true;
            bool progress = type == typeof(LegacyProgressBar);
            const string colorHelp = "色を標準の色編集で指定。各成分0〜1、sRGB/Linear対応。Aは標準色との混合率。次に作るUIから反映。";
            colorX? color = Read(config, section, progress ? "BarColor" : "Color", defaultColor, colorHelp, warn, ValidColor);
            colorX? container = progress ? Read(config, section, "ContainerColor", PanelColor, colorHelp, warn, ValidColor) : null;
            float? slant = defaultSlant.HasValue
                ? Read(config, section, "Slant", defaultSlant.Value, "形状の傾き（度）。-30〜30。次に作るUIから反映。", warn, ValidSlant,
                    new AcceptableValueRange<float>(-30f, 30f))
                : null;
            if (enabled && active) result.Add(type, (color, slant, container));
        }
    }

    internal static T? Read<T>(ConfigFile config, string section, string key, T fallback,
        string description, Action<string> warn, Func<T, bool>? validate = null, AcceptableValueBase? range = null) where T : struct
    {
        T value = config.Bind(section, key, fallback, new ConfigDescription(description, range)).Value;
        if (validate != null && !validate(value))
        {
            warn($"[{section}] {key} を適用しません: 対応する値の範囲外です。");
            return null;
        }
        return value;
    }

    internal static bool ValidColor(colorX value) => (value.Profile == ColorProfile.sRGB || value.Profile == ColorProfile.Linear)
        && Unit(value.r) && Unit(value.g) && Unit(value.b) && Unit(value.a);
    private static bool Unit(float value) => float.IsFinite(value) && value >= 0f && value <= 1f;

    // ponytail: 初版は標準寸法の-30〜30度に限定。範囲拡張時は各部品の寸法と操作領域を検証する。
    internal static bool ValidSlant(float value) => float.IsFinite(value) && value >= -30f && value <= 30f;

    // RunActionは呼び出し前にpath引数をカテゴリのパスへ更新する。
    internal static bool IsTargetCategory(string? category) =>
        string.Equals(category?.Replace('\\', '/').Trim('/'), "Object/Legacy UI", StringComparison.Ordinal);

    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        MethodInfo invoke = AccessTools.Method(typeof(Action<Slot>), nameof(Action<Slot>.Invoke));
        var matches = code.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.Calls(invoke)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"生成アクション呼び出しが1か所ではありません（{matches.Length}か所）。");
        int index = matches[0].index;
        CodeInstruction original = code[index];
        if (original.blocks.Count != 0)
            throw new InvalidOperationException("生成アクションの例外ブロック構造が想定と異なります。");
        var source = new CodeInstruction(OpCodes.Ldarg_2);
        source.labels.AddRange(original.labels);
        code.RemoveAt(index);
        code.InsertRange(index, [source, new CodeInstruction(OpCodes.Ldarg_3),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(CreateAndStyle)))]);
        return code;
    }

    internal static void CreateAndStyle(Action<Slot> create, Slot slot, ButtonEventData eventData, string category)
    {
        create(slot);
        try
        {
            if (instance == null || instance.styles.Count == 0 || !IsTargetCategory(category) || eventData.source?.Slot?.IsUnderLocalUser != true)
                return;
            foreach (Component component in slot.GetComponents<Component>())
            {
                if (!instance.styles.TryGetValue(component.GetType(), out var style)) continue;
                Apply(component, style.Color, style.Slant, style.Container);
            }
        }
        catch (Exception error)
        {
            // 外観処理の失敗で、元の生成処理のUndo終了や後続通知を妨げない。
            instance?.Log.LogError($"生成済みUIへの外観適用に失敗しました: {error}");
        }
    }

    private static void Apply(Component component, colorX? color, float? slant, colorX? container)
    {
        switch (component)
        {
            case LegacyButton button:
                if (color.HasValue) button.Color = color.Value;
                if (slant.HasValue) button.Slant = slant.Value;
                break;
            case LegacyCheckbox checkbox:
                if (color.HasValue) checkbox.OverrideColor = color.Value;
                break;
            case LegacyHorizontalChoiceBar choice:
                if (slant.HasValue) choice.Slant.Value = slant.Value;
                if (color.HasValue)
                    for (int i = 0; i < choice.ItemCount; i++) choice.GetItem(i).OverrideColor.Value = color.Value;
                break;
            case LegacyFieldBase field:
                if (color.HasValue) field.OverrideColor = color.Value;
                if (slant.HasValue) field.Slant = slant.Value;
                break;
            case LegacyCanvasPanel panel:
                if (color.HasValue) panel.Panel.Color = color.Value;
                break;
            case LegacyProgressBar progress:
                if (color.HasValue) progress.BarColor = color.Value;
                if (container.HasValue) progress.ContainerColor = container.Value;
                if (slant.HasValue) progress.Slant = slant.Value;
                break;
            case LegacyRadio radio:
                if (color.HasValue) radio.OverrideColor = color.Value;
                break;
            case LegacySlider slider:
                if (color.HasValue) slider.OverrideColor = color.Value;
                if (slant.HasValue) slider.Slant = slant.Value;
                break;
        }
    }
}
