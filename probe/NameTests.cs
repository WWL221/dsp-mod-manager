using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DSPModManager;

public static class NameTests
{
    static int fails = 0;
    static void Check(string n, bool ok, string d)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + n + "  | " + d);
        if (!ok) fails++;
    }

    public static int Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "dspmm_names_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        string tbl = Path.Combine(tmp, "mod-names.txt");

        try
        {
            Console.WriteLine("=== 1. 内置中文名 ===");
            ModNameLocalizer.Configure(Path.Combine(tmp, "nonexistent.txt"));
            Check("1.1 伊卡洛斯增强", ModNameLocalizer.GetChineseName("com.lead.dsp.mechaboost", "DSPMechaBoost.dll") == "伊卡洛斯增强",
                  ModNameLocalizer.GetChineseName("com.lead.dsp.mechaboost", "DSPMechaBoost.dll"));
            Check("1.2 无视地形建造", ModNameLocalizer.GetChineseName("com.dsp.noslopebuild", "DSPNoSlopeBuild.dll") == "无视地形建造", "");
            Check("1.3 风力发电机200倍", ModNameLocalizer.GetChineseName("com.lead.dsp.windturbine200x", "DSPWindTurbine200x.dll") == "风力发电机200倍", "");
            Check("1.4 未知模组返回 null", ModNameLocalizer.GetChineseName("com.unknown.mod", "Unknown.dll") == null, "");

            Console.WriteLine();
            Console.WriteLine("=== 2. 外部对照表覆盖 ===");
            File.WriteAllText(tbl,
                "# 测试\n" +
                "com.lead.dsp.mechaboost\t我的伊卡洛斯\n" +
                "com.unknown.mod\t某个模组\n", new System.Text.UTF8Encoding(false));
            ModNameLocalizer.Configure(tbl);
            Check("2.1 外部表覆盖内置名", ModNameLocalizer.GetChineseName("com.lead.dsp.mechaboost", "x.dll") == "我的伊卡洛斯", "");
            Check("2.2 外部表补未知模组", ModNameLocalizer.GetChineseName("com.unknown.mod", "x.dll") == "某个模组", "");
            Check("2.3 外部表没写的仍回落内置", ModNameLocalizer.GetChineseName("com.dsp.noslopebuild", "x.dll") == "无视地形建造", "");

            Console.WriteLine();
            Console.WriteLine("=== 3. ★ 行尾注释不能被当成中文名（真实 bug 回归）===");
            File.WriteAllText(tbl,
                "com.lead.dsp.mechaboost\t伊卡洛斯增强        # DSP Mecha Boost\n", new System.Text.UTF8Encoding(false));
            ModNameLocalizer.Configure(tbl);
            string got = ModNameLocalizer.GetChineseName("com.lead.dsp.mechaboost", "x.dll");
            Check("3.1 中文名不含 # 注释", got == "伊卡洛斯增强", "got=[" + (got ?? "null") + "]");
            Check("3.2 中文名不含英文尾巴", got != null && !got.Contains("DSP Mecha"), "got=[" + (got ?? "null") + "]");

            Console.WriteLine();
            Console.WriteLine("=== 4. 按文件名匹配（无 GUID 时）===");
            File.WriteAllText(tbl, "SomeMod\t按文件名匹配的名字\n", new System.Text.UTF8Encoding(false));
            ModNameLocalizer.Configure(tbl);
            Check("4.1 文件名主体匹配", ModNameLocalizer.GetChineseName(null, "SomeMod.dll") == "按文件名匹配的名字", "");
            Check("4.2 .disabled 后缀也能匹配", ModNameLocalizer.GetChineseName(null, "SomeMod.dll.disabled") == "按文件名匹配的名字",
                  ModNameLocalizer.GetChineseName(null, "SomeMod.dll.disabled"));

            Console.WriteLine();
            Console.WriteLine("=== 5. 模板生成 ===");
            string tbl2 = Path.Combine(tmp, "gen.txt");
            var mods = new List<ModInfo>();
            var m1 = new ModInfo(); m1.Guid = "com.lead.dsp.mechaboost"; m1.Name = "DSP Mecha Boost"; m1.FileName = "DSPMechaBoost.dll";
            var m2 = new ModInfo(); m2.Guid = "com.unknown.mod"; m2.Name = "Unknown Mod"; m2.FileName = "Unknown.dll";
            mods.Add(m1); mods.Add(m2);

            ModNameLocalizer.Configure(Path.Combine(tmp, "none2.txt"));
            ModNameLocalizer.WriteTemplate(mods, tbl2);
            string text = File.ReadAllText(tbl2);
            Check("5.1 模板含 GUID", text.Contains("com.lead.dsp.mechaboost"), "");
            Check("5.2 模板自动填内置中文名", text.Contains("伊卡洛斯增强"), "");
            Check("5.3 未知模组留空待填", text.Contains("com.unknown.mod\t") || text.Contains("com.unknown.mod "), "");

            // 用户填了未知模组后，重新生成模板不应覆盖
            File.WriteAllText(tbl2,
                "com.unknown.mod\t我填的名字\n", new System.Text.UTF8Encoding(false));
            ModNameLocalizer.Configure(Path.Combine(tmp, "none3.txt"));
            ModNameLocalizer.WriteTemplate(mods, tbl2);
            string text2 = File.ReadAllText(tbl2);
            Check("5.4 重新生成模板保留用户已填内容", text2.Contains("我填的名字"),
                  text2.Split('\n').FirstOrDefault(l => l.StartsWith("com.unknown.mod")) ?? "(缺)");

            Console.WriteLine();
            Console.WriteLine("=== 6. 显示格式：中文名（英文名）===");
            ModNameLocalizer.Configure(Path.Combine(tmp, "none4.txt"));
            var mm = new ModInfo();
            mm.Guid = "com.lead.dsp.mechaboost";
            mm.Name = "DSP Mecha Boost";
            mm.MetadataOk = true;
            mm.FileName = "DSPMechaBoost.dll";
            Check("6.1 DisplayName = 中文名（英文名）",
                  mm.DisplayName == "伊卡洛斯增强（DSP Mecha Boost）", mm.DisplayName);
            Check("6.2 EnglishName 保持英文", mm.EnglishName == "DSP Mecha Boost", mm.EnglishName);
            Check("6.3 ChineseName 单独可取", mm.ChineseName == "伊卡洛斯增强", mm.ChineseName);

            var mm2 = new ModInfo();
            mm2.Guid = "com.unknown.mod"; mm2.Name = "Unknown Mod"; mm2.MetadataOk = true; mm2.FileName = "Unknown.dll";
            Check("6.4 无中文名时只显示英文（不加空括号）", mm2.DisplayName == "Unknown Mod", mm2.DisplayName);

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "== ALL NAME CHECKS PASS ==" : ("== FAILURES: " + fails + " =="));
            return fails == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}