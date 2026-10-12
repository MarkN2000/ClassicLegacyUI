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
            var styles = Plugin.ReadStyles(config, warnings.Add);
            Check(styles.Count == 9 && warnings.Count == 0, "9種類の初期設定");
            Check(config.Values.Count(entry => entry.SettingType == typeof(bool)) == 10
                && config.Values.Count(entry => entry.SettingType == typeof(colorX)) == 10
                && config.Values.Count(entry => entry.SettingType == typeof(float)) == 6
                && config.Values.All(entry => entry.SettingType != typeof(string)), "設定画面に渡す実際の型");
            Check(Coder.IsEnginePrimitive(typeof(colorX)), "Resonite標準の色エディター対象型");
            Check(config["Button", "Slant"].Description.AcceptableValues is AcceptableValueRange<float> range
                && range.MinValue == -30f && range.MaxValue == 30f, "角度スライダーの範囲");
            foreach (var pair in styles)
            {
                colorX expectedColor = pair.Key == typeof(LegacyCanvasPanel) ? Plugin.PanelColor
                    : pair.Key == typeof(LegacyProgressBar) ? new colorX(1f, 0.73f, 0.09f, 1f, ColorProfile.sRGB) : Plugin.CommonColor;
                Check(pair.Value.Color == expectedColor, "初期色");
                float? expectedSlant = pair.Key == typeof(LegacyCheckbox) || pair.Key == typeof(LegacyCanvasPanel) || pair.Key == typeof(LegacyRadio)
                    ? null : 22.5f;
                Check(pair.Value.Slant == expectedSlant, "初期角度");
            }
            Check(styles[typeof(LegacyProgressBar)].Container == Plugin.PanelColor, "Progress Bar外枠");
            config.Save();
            string saved = File.ReadAllText(file);
            Check(saved.Contains("[1; 0.73; 0.09; 0.8; sRGB]"), "色の保存形式");
            var roundTrip = Plugin.ReadStyles(new ConfigFile(file, false), warnings.Add);
            Check(styles.All(pair => roundTrip[pair.Key].Equals(pair.Value)), "設定ファイルの往復");

            config["General", "Enabled"].BoxedValue = false;
            Check(Plugin.ReadStyles(config, warnings.Add).Count == 0, "設定画面相当の全体無効化");
            config["General", "Enabled"].BoxedValue = true;
            config["Button", "Color"].BoxedValue = new colorX(0.1f, 0.2f, 0.3f, 1f, ColorProfile.Linear);
            config["Button", "Slant"].BoxedValue = -22.5f;
            var changed = Plugin.ReadStyles(config, warnings.Add);
            Check(changed[typeof(LegacyButton)].Color!.Value.Profile == ColorProfile.Linear
                && changed[typeof(LegacyButton)].Slant == -22.5f, "設定画面相当の変更と再有効化");
            Check(styles[typeof(LegacyButton)].Slant == 22.5f, "取得済みの値に変更を遡及しない");
            Check(File.ReadAllText(file) == saved, "UI変更中に独自の毎回保存をしない");

            const string legacy = "[General]\n# Setting type: String\nEnabled = false\n[Button]\n# Setting type: String\nColor = [0.2; 0.4; 0.6; 0.5; sRGB]\nSlant = 12.5\n";
            File.WriteAllText(file, legacy);
            var migratedConfig = new ConfigFile(file, false);
            Check(Plugin.ReadStyles(migratedConfig, warnings.Add).Count == 0, "旧文字列設定の無効状態を引き継ぐ");
            migratedConfig["General", "Enabled"].BoxedValue = true;
            var migrated = Plugin.ReadStyles(migratedConfig, warnings.Add);
            Check(migrated[typeof(LegacyButton)].Color == new colorX(0.2f, 0.4f, 0.6f, 0.5f)
                && migrated[typeof(LegacyButton)].Slant == 12.5f, "旧文字列設定のカスタム値を引き継ぐ");
            File.WriteAllText(file, "[Button]\nSlant = 5\n");
            migratedConfig.Reload();
            Check(Plugin.ReadStyles(migratedConfig, warnings.Add)[typeof(LegacyButton)].Slant == 5f, "標準の設定再読み込み");

            const string invalid = "[Button]\nColor = malformed\nSlant = NaN\n[Panel]\nColor = [1; 2; 0; 0.8; sRGB]\n[Slider]\nSlant = 90\n[Checkbox]\nEnabled = false\n[Radio]\nEnabled = bad\n[ProgressBar]\nContainerColor = [1; 1; 1; 1; Linear]\n";
            File.WriteAllText(file, invalid);
            Plugin.BackupExistingConfig(file);
            styles = Plugin.ReadStyles(new ConfigFile(file, false), warnings.Add);
            Check(styles[typeof(LegacyButton)].Color == Plugin.CommonColor
                && styles[typeof(LegacyButton)].Slant == -30f, "標準の構文不正時の既定値とNaN補正");
            Check(styles[typeof(LegacyCanvasPanel)].Color == null && styles[typeof(LegacySlider)].Slant == 30f, "色の検証と標準の角度補正");
            Check(styles[typeof(LegacyProgressBar)].Container!.Value.Profile == ColorProfile.Linear, "標準色エディターのLinear入力");
            Check(!styles.ContainsKey(typeof(LegacyCheckbox)) && styles.ContainsKey(typeof(LegacyRadio)), "無効化と構文不正時の既定値");
            Check(warnings.Count == 1, "範囲外の色をログへ記録");
            Check(File.ReadAllText(file) == invalid, "不正な既存ファイルの保護");
            File.WriteAllText(file, "[General]\nEnabled = false\n");
            Plugin.BackupExistingConfig(file);
            Check(File.ReadAllText(file + ".before-0.2.0.bak") == invalid, "移行前バックアップの作成と維持");
            Check(Plugin.ReadStyles(new ConfigFile(file, false), warnings.Add).Count == 0, "全体の無効化");
        }
        finally { File.Delete(file); File.Delete(file + ".before-0.2.0.bak"); }

        Check(Plugin.ValidSlant(-30) && Plugin.ValidSlant(30) && !Plugin.ValidSlant(30.01f)
            && !Plugin.ValidSlant(float.PositiveInfinity), "角度の境界");
        Check(!Plugin.ValidColor(new colorX(float.NaN, 0, 0, 1)), "非有限の色");
        Check(Plugin.IsTargetCategory(@"Object\Legacy UI") && Plugin.IsTargetCategory("/Object/Legacy UI/"), "カテゴリの区切り");
        Check(!Plugin.IsTargetCategory("Object/Legacy UI/Color Dialog") && !Plugin.IsTargetCategory(null)
            && !Plugin.IsTargetCategory("Object/Legacy UI/0"), "対象外カテゴリ");

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
        Check(patched[3].labels.Contains(label) && patched[3].opcode == OpCodes.Ldarg_2, "分岐先の維持");
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
        Check(created == 1, "変換後ILによる元アクションの1回実行");
        var expected = new InvalidOperationException("元アクションの失敗");
        try { create(_ => throw expected, null!, default, "Object/Legacy UI"); throw new Exception("例外が失われました"); }
        catch (InvalidOperationException error) { Check(ReferenceEquals(error, expected), "元アクションの例外を維持"); }
        Reject([]);
        Reject([new CodeInstruction(OpCodes.Callvirt, invoke), new CodeInstruction(OpCodes.Callvirt, invoke)]);

        MethodInfo runAction = AccessTools.DeclaredMethod(typeof(DevCreateNewForm), "RunAction",
            [typeof(IButton), typeof(ButtonEventData), typeof(string)]);
        var original = PatchProcessor.GetOriginalInstructions(runAction);
        var actual = Plugin.Transpile(original).ToList();
        Check(actual.Count == original.Count + 2 && actual.Count(code => code.Calls(wrapper)) == 1, "現行DLLへのIL変換適合（実行なし）");
        Console.WriteLine("PASS: 初期値、設定往復、不正値・ファイル保護、対象カテゴリ、IL変換と現行API。実機動作は未検証。");
    }

    private static void Reject(IEnumerable<CodeInstruction> code)
    {
        try { Plugin.Transpile(code).ToList(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("不適合ILを拒否しませんでした。");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
    }
}
