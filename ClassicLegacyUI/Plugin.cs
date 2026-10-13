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

[ResonitePlugin("markn2000.ClassicLegacyUI", "ClassicLegacyUI", "0.2.4", "markn2000", "https://github.com/MarkN2000/ClassicLegacyUI")]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(BepisResoniteWrapper.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    private static Plugin? instance;
    private Settings? settings;
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
                throw new InvalidOperationException("The Shim color converter is not registered.");
            bool newFile = !File.Exists(Config.ConfigFilePath);
            BackupExistingConfig(Config.ConfigFilePath);
            settings = RegisterSettings(Config);
            styles = ReadStyles(settings, message => Log.LogWarning(message));
            if (newFile) Config.Save();
            MethodInfo target = AccessTools.DeclaredMethod(typeof(DevCreateNewForm), "RunAction",
                [typeof(IButton), typeof(ButtonEventData), typeof(string)])
                ?? throw new MissingMethodException("DevCreateNewForm.RunAction was not found.");
            HarmonyInstance.Patch(target, transpiler: new HarmonyMethod(typeof(Plugin), nameof(Transpile)));
            Config.SettingChanged += OnSettingChanged;
            Log.LogInfo("ClassicLegacyUI loaded. Settings changes apply to newly created UI.");
        }
        catch (Exception error)
        {
            HarmonyInstance.UnpatchSelf();
            styles = new();
            Log.LogError($"Initialization failed; styling has been disabled: {error}");
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
        styles = ReadStyles(settings!, message => Log.LogWarning(message));

    internal static void BackupExistingConfig(string path)
    {
        string backup = path + ".before-0.2.0.bak";
        if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
    }

    internal sealed record Settings(ConfigEntry<bool> Enabled,
        Dictionary<Type, (ConfigEntry<bool> Enabled, ConfigEntry<colorX> Color,
            ConfigEntry<float>? Slant, ConfigEntry<colorX>? Container)> Styles);

    internal static Settings RegisterSettings(ConfigFile config)
    {
        config.SaveOnConfigSet = false;
        var settings = new Settings(config.Bind("General", "Enabled", true, "Enable styling for newly created UI."), new());
        Add(typeof(LegacyButton), "Button", CommonColor, 22.5f);
        Add(typeof(LegacyCheckbox), "Checkbox", CommonColor);
        Add(typeof(LegacyHorizontalChoiceBar), "HorizontalChoiceBar", CommonColor, 22.5f);
        Add(typeof(LegacyNumericUpDown), "NumericUpDown", CommonColor, 22.5f);
        Add(typeof(LegacyCanvasPanel), "Panel", PanelColor);
        Add(typeof(LegacyProgressBar), "ProgressBar", new colorX(1f, 0.73f, 0.09f, 1f, ColorProfile.sRGB), 22.5f);
        Add(typeof(LegacyRadio), "Radio", CommonColor);
        Add(typeof(LegacySlider), "Slider", CommonColor, 22.5f);
        Add(typeof(LegacyTextField), "TextField", CommonColor, 22.5f);
        return settings;

        void Add(Type type, string section, colorX defaultColor, float? defaultSlant = null)
        {
            var active = config.Bind(section, "Enabled", true, "Enable styling for this UI type.");
            bool progress = type == typeof(LegacyProgressBar);
            const string colorHelp = "Color in sRGB or Linear, with each channel from 0 to 1. Alpha blends with the default color. Applies to newly created UI.";
            var color = config.Bind(section, progress ? "BarColor" : "Color", defaultColor, colorHelp);
            var container = progress ? config.Bind(section, "ContainerColor", PanelColor, colorHelp) : null;
            var slant = defaultSlant.HasValue
                ? config.Bind(section, "Slant", defaultSlant.Value, new ConfigDescription(
                    "Slant in degrees, from -90 to 90. Applies to newly created UI.", new AcceptableValueRange<float>(-90f, 90f)))
                : null;
            settings.Styles.Add(type, (active, color, slant, container));
        }
    }

    internal static Dictionary<Type, (colorX? Color, float? Slant, colorX? Container)> ReadStyles(
        Settings settings, Action<string> warn)
    {
        var result = new Dictionary<Type, (colorX? Color, float? Slant, colorX? Container)>(settings.Styles.Count);
        bool enabled = settings.Enabled.Value;
        foreach (var (type, entries) in settings.Styles)
        {
            var style = (Read(entries.Color, warn, ValidColor), Read(entries.Slant, warn, ValidSlant),
                Read(entries.Container, warn, ValidColor));
            if (enabled && entries.Enabled.Value) result.Add(type, style);
        }
        return result;
    }

    private static T? Read<T>(ConfigEntry<T>? entry, Action<string> warn, Func<T, bool> validate) where T : struct
    {
        if (entry == null) return null;
        T value = entry.Value;
        if (!validate(value))
        {
            warn($"[{entry.Definition.Section}] {entry.Definition.Key} was not applied: value is outside the supported range.");
            return null;
        }
        return value;
    }

    internal static bool ValidColor(colorX value) => (value.Profile == ColorProfile.sRGB || value.Profile == ColorProfile.Linear)
        && Unit(value.r) && Unit(value.g) && Unit(value.b) && Unit(value.a);
    private static bool Unit(float value) => float.IsFinite(value) && value >= 0f && value <= 1f;

    internal static bool ValidSlant(float value) => float.IsFinite(value) && value >= -90f && value <= 90f;

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
            throw new InvalidOperationException($"Expected exactly one creation action call; found {matches.Length}.");
        int index = matches[0].index;
        CodeInstruction original = code[index];
        if (original.blocks.Count != 0)
            throw new InvalidOperationException("The creation action has an unexpected exception block structure.");
        var category = new CodeInstruction(OpCodes.Ldarg_3);
        category.labels.AddRange(original.labels);
        code.RemoveAt(index);
        code.InsertRange(index, [category,
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(CreateAndStyle)))]);
        return code;
    }

    internal static void CreateAndStyle(Action<Slot> create, Slot slot, string category)
    {
        create(slot);
        try
        {
            var styles = instance?.styles;
            if (styles == null || styles.Count == 0 || !IsTargetCategory(category))
                return;
            foreach (Component component in slot.GetComponents<Component>())
            {
                if (!styles.TryGetValue(component.GetType(), out var style)) continue;
                Apply(component, style.Color, style.Slant, style.Container);
            }
        }
        catch (Exception error)
        {
            // 外観処理の失敗で、元の生成処理のUndo終了や後続通知を妨げない。
            instance?.Log.LogError($"Failed to apply styling to the newly created UI: {error}");
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
