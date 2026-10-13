using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx.Configuration;
using ClassicLegacyUI;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

internal static class Program
{
    private static void Main(string[] args)
    {
        string game = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";
        string profile = args.Length > 1 ? args[1] : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"com.kesomannen.gale\resonite\profiles\Mod開発");
        // DLLを元の場所から読む。Engineの生成、ローダーの起動、ゲームへのパッチは行わない。
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string? file = new[] { Path.Combine(profile, "BepInEx", "core"), game }
                .Select(dir => Path.Combine(dir, name.Name + ".dll")).FirstOrDefault(File.Exists)
                ?? Directory.EnumerateFiles(Path.Combine(profile, "BepInEx", "plugins"), name.Name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
            return file == null ? null : context.LoadFromAssemblyPath(file);
        };
        Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        // Shimと同じ公開Coder APIを使い、ゲームを初期化せず色変換を登録する。
        TomlTypeConverter.AddConverter(typeof(colorX), new TypeConverter
        {
            ConvertToString = (value, _) => Coder<colorX>.EncodeToString((colorX)value),
            ConvertToObject = (value, _) => Coder<colorX>.DecodeFromString(value)
        });
        string file = Path.Combine(Path.GetTempPath(), "ClassicLegacyUI-check-" + Guid.NewGuid() + ".cfg");
        var warnings = new List<string>();
        try
        {
            var config = new ConfigFile(file, false) { SaveOnConfigSet = false };
            var settings = Plugin.RegisterSettings(config);
            var styles = Plugin.ReadStyles(settings, warnings.Add);
            Check(styles.Count == 9 && warnings.Count == 0, "Defaults for all nine UI types");
            Check(config.Values.Count(entry => entry.SettingType == typeof(bool)) == 10
                && config.Values.Count(entry => entry.SettingType == typeof(colorX)) == 10
                && config.Values.Count(entry => entry.SettingType == typeof(float)) == 6
                && config.Values.All(entry => entry.SettingType != typeof(string)), "Native setting types for the settings UI");
            Check(Coder.IsEnginePrimitive(typeof(colorX)), "Color type supported by the native editor");
            Check(config.Values.Where(entry => entry.SettingType == typeof(float)).All(entry =>
                entry.Description.AcceptableValues is AcceptableValueRange<float> range
                && range.MinValue == -90f && range.MaxValue == 90f), "Slant slider ranges for all six UI types");
            foreach (var pair in styles)
            {
                colorX expectedColor = pair.Key == typeof(LegacyCanvasPanel) ? Plugin.PanelColor
                    : pair.Key == typeof(LegacyProgressBar) ? new colorX(1f, 0.73f, 0.09f, 1f, ColorProfile.sRGB) : Plugin.CommonColor;
                Check(pair.Value.Color == expectedColor, "Default color");
                float? expectedSlant = pair.Key == typeof(LegacyCheckbox) || pair.Key == typeof(LegacyCanvasPanel) || pair.Key == typeof(LegacyRadio)
                    ? null : 22.5f;
                Check(pair.Value.Slant == expectedSlant, "Default slant");
            }
            Check(styles[typeof(LegacyProgressBar)].Container == Plugin.PanelColor, "Progress Bar container color");
            config.Save();
            string saved = File.ReadAllText(file);
            Check(saved.Contains("[1; 0.73; 0.09; 0.8; sRGB]"), "Serialized color format");
            var roundTrip = Plugin.ReadStyles(Plugin.RegisterSettings(new ConfigFile(file, false)), warnings.Add);
            Check(styles.All(pair => roundTrip[pair.Key].Equals(pair.Value)), "Configuration round trip");

            config["General", "Enabled"].BoxedValue = false;
            config["Button", "Color"].BoxedValue = new colorX(float.NaN, 0f, 0f, 1f);
            Check(Plugin.ReadStyles(settings, warnings.Add).Count == 0 && warnings.Count == 0, "Skip disabled settings without validating their values");
            config["General", "Enabled"].BoxedValue = true;
            config["Button", "Enabled"].BoxedValue = false;
            Check(!Plugin.ReadStyles(settings, warnings.Add).ContainsKey(typeof(LegacyButton)) && warnings.Count == 0, "Skip disabled UI types without validating their values");
            config["Button", "Enabled"].BoxedValue = true;
            config["Button", "Color"].BoxedValue = new colorX(0.1f, 0.2f, 0.3f, 1f, ColorProfile.Linear);
            config["Button", "Slant"].BoxedValue = -22.5f;
            var changed = Plugin.ReadStyles(settings, warnings.Add);
            Check(changed[typeof(LegacyButton)].Color!.Value.Profile == ColorProfile.Linear
                && changed[typeof(LegacyButton)].Slant == -22.5f, "Change settings and re-enable styling");
            Check(styles[typeof(LegacyButton)].Slant == 22.5f, "Previously captured values remain unchanged");
            Check(File.ReadAllText(file) == saved, "No automatic save on each settings change");

            foreach (float angle in new[] { -90f, -75f, 75f, 90f })
            {
                foreach (var entry in settings.Styles.Values)
                    if (entry.Slant != null) entry.Slant.Value = angle;
                var expanded = Plugin.ReadStyles(settings, warnings.Add);
                Check(settings.Styles.Where(pair => pair.Value.Slant != null)
                    .All(pair => expanded[pair.Key].Slant == angle), "Preserve expanded slant values for all six UI types");
            }
            config["Button", "Slant"].BoxedValue = -22.5f;

            // 登録元から外しても保持したConfigEntryを読み、再登録しないことを確認する。
            config.Remove(new ConfigDefinition("Button", "Color"));
            var retained = Plugin.ReadStyles(settings, warnings.Add);
            Check(config.Count == 25 && retained[typeof(LegacyButton)] == changed[typeof(LegacyButton)],
                "Retain registered entries without registering them again");

            const string legacy = "[General]\n# Setting type: String\nEnabled = false\n[Button]\n# Setting type: String\nColor = [0.2; 0.4; 0.6; 0.5; sRGB]\nSlant = 12.5\n";
            File.WriteAllText(file, legacy);
            var migratedConfig = new ConfigFile(file, false);
            var migratedSettings = Plugin.RegisterSettings(migratedConfig);
            Check(Plugin.ReadStyles(migratedSettings, warnings.Add).Count == 0, "Preserve the disabled state from legacy string settings");
            migratedConfig["General", "Enabled"].BoxedValue = true;
            var migrated = Plugin.ReadStyles(migratedSettings, warnings.Add);
            Check(migrated[typeof(LegacyButton)].Color == new colorX(0.2f, 0.4f, 0.6f, 0.5f)
                && migrated[typeof(LegacyButton)].Slant == 12.5f, "Preserve custom values from legacy string settings");
            File.WriteAllText(file, "[Button]\nSlant = 5\n");
            migratedConfig.Reload();
            Check(Plugin.ReadStyles(migratedSettings, warnings.Add)[typeof(LegacyButton)].Slant == 5f, "Standard configuration reload");

            const string invalid = "[Button]\nColor = malformed\nSlant = NaN\n[Panel]\nColor = [1; 2; 0; 0.8; sRGB]\n[Slider]\nSlant = 100\n[Checkbox]\nEnabled = false\n[Radio]\nEnabled = bad\n[ProgressBar]\nContainerColor = [1; 1; 1; 1; Linear]\n";
            File.WriteAllText(file, invalid);
            styles = Plugin.ReadStyles(Plugin.RegisterSettings(new ConfigFile(file, false)), warnings.Add);
            Check(styles[typeof(LegacyButton)].Color == Plugin.CommonColor
                && styles[typeof(LegacyButton)].Slant == -90f, "Native fallback for malformed values and NaN clamping");
            Check(styles[typeof(LegacyCanvasPanel)].Color == null && styles[typeof(LegacySlider)].Slant == 90f, "Color validation and native slant clamping");
            Check(styles[typeof(LegacyProgressBar)].Container!.Value.Profile == ColorProfile.Linear, "Linear input from the native color editor");
            Check(!styles.ContainsKey(typeof(LegacyCheckbox)) && styles.ContainsKey(typeof(LegacyRadio)), "Disabled entries and fallback for malformed values");
            Check(warnings.Count == 1, "Log out-of-range colors");
            Check(File.ReadAllText(file) == invalid, "Preserve existing files with invalid values");
            File.WriteAllText(file, "[General]\nEnabled = false\n");
            Check(!File.Exists(file + ".before-0.2.0.bak"), "No automatic configuration backup");
            Check(Plugin.ReadStyles(Plugin.RegisterSettings(new ConfigFile(file, false)), warnings.Add).Count == 0, "Disable globally");
        }
        finally { File.Delete(file); }

        Check(Plugin.ValidSlant(-90) && Plugin.ValidSlant(90) && !Plugin.ValidSlant(-90.01f) && !Plugin.ValidSlant(90.01f)
            && !Plugin.ValidSlant(float.NaN) && !Plugin.ValidSlant(float.NegativeInfinity)
            && !Plugin.ValidSlant(float.PositiveInfinity), "Slant boundaries");
        Check(!Plugin.ValidColor(new colorX(float.NaN, 0, 0, 1)), "Non-finite color channels");
        Check(Plugin.IsTargetCategory(@"Object\Legacy UI") && Plugin.IsTargetCategory("/Object/Legacy UI/"), "Category separators");
        Check(!Plugin.IsTargetCategory("Object/Legacy UI/Color Dialog") && !Plugin.IsTargetCategory(null)
            && !Plugin.IsTargetCategory("Object/Legacy UI/0"), "Excluded categories");

        MethodInfo invoke = AccessTools.Method(typeof(Action<Slot>), "Invoke");
        MethodInfo wrapper = AccessTools.Method(typeof(Plugin), nameof(Plugin.CreateAndStyle));
        var method = new DynamicMethod("CheckCreation", typeof(void), [typeof(Action<Slot>), typeof(Slot), typeof(ButtonEventData), typeof(string)], typeof(Program).Module, true);
        ILGenerator il = method.GetILGenerator();
        Label label = il.DefineLabel();
        var call = new CodeInstruction(OpCodes.Callvirt, invoke);
        call.labels.Add(label);
        var input = new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Br, label), call, new CodeInstruction(OpCodes.Ret) };
        var patched = Plugin.Transpile(input).ToList();
        Check(patched[3].labels.Contains(label) && patched[3].opcode == OpCodes.Ldarg_3, "Preserve branch targets");
        Check(patched.All(code => code.opcode != OpCodes.Ldarg_2)
            && wrapper.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(Action<Slot>), typeof(Slot), typeof(string) }), "Styling does not receive the input source");
        foreach (CodeInstruction instruction in patched)
        {
            foreach (Label targetLabel in instruction.labels) il.MarkLabel(targetLabel);
            if (instruction.operand is MethodInfo info) il.Emit(instruction.opcode, info);
            else if (instruction.operand is Label targetLabel) il.Emit(instruction.opcode, targetLabel);
            else il.Emit(instruction.opcode);
        }
        var create = method.CreateDelegate<Action<Action<Slot>, Slot, ButtonEventData, string>>();
        int created = 0;
        create(_ => created++, null!, default, "Object/Legacy UI");
        Check(created == 1, "Patched IL invokes the original action exactly once");
        var expected = new InvalidOperationException("Original action failed");
        try { create(_ => throw expected, null!, default, "Object/Legacy UI"); throw new Exception("The original exception was swallowed"); }
        catch (InvalidOperationException error) { Check(ReferenceEquals(error, expected), "Preserve the original exception"); }
        Reject([]);
        Reject([new CodeInstruction(OpCodes.Callvirt, invoke), new CodeInstruction(OpCodes.Callvirt, invoke)]);

        MethodInfo runAction = AccessTools.DeclaredMethod(typeof(DevCreateNewForm), "RunAction",
            [typeof(IButton), typeof(ButtonEventData), typeof(string)]);
        var original = PatchProcessor.GetOriginalInstructions(runAction);
        var actual = Plugin.Transpile(original).ToList();
        Check(actual.Count == original.Count + 1 && actual.Count(code => code.Calls(wrapper)) == 1, "IL transformation compatibility with current DLLs (without execution)");
        Console.WriteLine("PASS: Defaults, configuration round trips, invalid values, file preservation, category filtering, IL transformation, and current APIs. In-game behavior has not been tested.");
    }

    private static void Reject(IEnumerable<CodeInstruction> code)
    {
        try { Plugin.Transpile(code).ToList(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Incompatible IL was not rejected.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
    }
}
