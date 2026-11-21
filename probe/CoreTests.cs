using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DSPModManager;

public static class CoreTests
{
    static int fails = 0;
    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + "  | " + detail);
        if (!ok) fails++;
    }

    // 造一个假的 DSP 目录：DSPGAME.exe(占位) + BepInEx/plugins + core + config
    static string MakeFakeGame(string tag)
    {
        string root = Path.Combine(Path.GetTempPath(), "dspmm_test_" + tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "DSPGAME.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "core"));
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "config"));
        return root;
    }

    static void DropDll(string pluginsDir, string name)
    {
        // 写一个最小的、非有效 .NET 文件：用来验证「读不出元数据也不崩」
        File.WriteAllBytes(Path.Combine(pluginsDir, name), new byte[] { 0x4D, 0x5A, 0, 0, 0 });
    }

    public static int Main()
    {
        try
        {
            Console.WriteLine("=== 1. 目录校验 ===");
            string bad = Path.Combine(Path.GetTempPath(), "dspmm_not_a_game_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(bad);
            var mm1 = new ModManager(bad);
            Check("1.1 非游戏目录被拒绝", !mm1.IsValidGameDir(), "dir=" + bad);

            string g = MakeFakeGame("main");
            var mm = new ModManager(g);
            Check("1.2 有效游戏目录被接受", mm.IsValidGameDir(), g);
            Check("1.3 HasBepInEx 为 true", mm.HasBepInEx(), "");

            Console.WriteLine();
            Console.WriteLine("=== 2. 扫描 ===");
            DropDll(mm.PluginsDir, "ModA.dll");
            DropDll(mm.PluginsDir, "ModB.dll");
            Check("2.1 空目录先有 2 个", mm.Scan().Count == 2, "count=" + mm.Scan().Count);
            Check("2.2 都标记为启用", mm.Scan().All(x => x.Enabled), "");
            Check("2.3 元数据读不出时 MetadataOk=false 但不抛异常",
                  mm.Scan().All(x => !x.MetadataOk), "error sample=" + mm.Scan()[0].MetadataError);

            Console.WriteLine();
            Console.WriteLine("=== 3. 停用 / 启用（核心：只改名不删文件）===");
            var all = mm.Scan();
            var a = all.First(x => x.FileName == "ModA.dll");
            long sizeBefore = a.FileSize;
            string srcPath = a.FilePath;

            mm.Disable(a);
            Check("3.1 停用后 Enabled=false", !a.Enabled, "");
            Check("3.2 原位置文件已不存在", !File.Exists(srcPath), srcPath);
            Check("3.3 出现在 disabled 子目录", File.Exists(a.FilePath) && a.FilePath.Contains("disabled"), a.FilePath);
            Check("3.4 FileName 已同步为磁盘真实名（含 .disabled）", a.FileName.EndsWith(".disabled"), a.FileName);
            Check("3.4b FileName 与磁盘文件名一致", a.FileName == Path.GetFileName(a.FilePath), a.FileName + " vs " + Path.GetFileName(a.FilePath));
            Check("3.5 文件大小未变（没被改写）", new FileInfo(a.FilePath).Length == sizeBefore,
                  sizeBefore + " -> " + new FileInfo(a.FilePath).Length);

            var rescanned = mm.Scan();
            Check("3.6 重新扫描仍能看到停用的模组", rescanned.Count == 2 && rescanned.Any(x => !x.Enabled),
                  "total=" + rescanned.Count + " disabled=" + rescanned.Count(x => !x.Enabled));

            var a2 = mm.Scan().First(x => x.FileName.Contains("ModA"));
            mm.Enable(a2);
            Check("3.7 启用后 Enabled=true", a2.Enabled, "");
            Check("3.8 回到 plugins 根目录", Path.GetDirectoryName(a2.FilePath) == mm.PluginsDir, a2.FilePath);
            Check("3.9 文件名恢复无 .disabled", a2.FileName == "ModA.dll", a2.FileName);

            Console.WriteLine();
            Console.WriteLine("=== 4. 幂等 / 边界 ===");
            var a3 = mm.Scan().First(x => x.FileName == "ModA.dll");
            mm.Enable(a3);  // 已启用再启用
            Check("4.1 重复启用不报错", a3.Enabled && File.Exists(a3.FilePath), "");
            var a4 = mm.Scan().First(x => x.FileName == "ModA.dll");
            mm.Disable(a4);
            mm.Disable(a4); // 已停用再停用
            Check("4.2 重复停用不报错", !a4.Enabled && File.Exists(a4.FilePath), "");

            Console.WriteLine();
            Console.WriteLine("=== 5. 同名冲突保护（绝不覆盖）===");
            // 在 disabled 里放一个 ModC，同时在 plugins 根放一个同名 ModC
            DropDll(mm.PluginsDir, "ModC.dll");
            Directory.CreateDirectory(mm.DisabledDir);
            DropDll(mm.DisabledDir, "ModC.dll.disabled");
            int before = Directory.GetFiles(mm.PluginsDir, "ModC.dll").Length;
            var cDisabled = mm.Scan().First(x => !x.Enabled && x.FileName.Contains("ModC"));
            bool threw = false;
            try { mm.Enable(cDisabled); }
            catch (InvalidOperationException) { threw = true; }
            Check("5.1 同名冲突时拒绝启用（不覆盖）", threw, "threw=" + threw);
            Check("5.2 冲突时启用侧文件仍在", Directory.GetFiles(mm.PluginsDir, "ModC.dll").Length == before, "");

            Console.WriteLine();
            Console.WriteLine("=== 6. 依赖分析 ===");
            // 用真实 DLL 测（可选）：通过环境变量 DSP_TEST_DLL / DSP_CECIL 指定，缺省跳过
            string realDll = Environment.GetEnvironmentVariable("DSP_TEST_DLL");
            string realCecil = Environment.GetEnvironmentVariable("DSP_CECIL");
            if (File.Exists(realDll) && File.Exists(realCecil))
            {
                PluginMetadataReader.ConfigureCecil(realCecil);
                var md = PluginMetadataReader.Read(realDll);
                Check("6.1 真实插件元数据解析成功", md.Ok, "err=" + md.Error);
                Check("6.2 GUID 正确", md.Guid == "com.lead.dsp.mechaboost", "guid=" + md.Guid);
                Check("6.3 Name 正确", md.Name == "DSP Mecha Boost", "name=" + md.Name);
                Check("6.4 Version 正确", md.Version == "1.0.0", "ver=" + md.Version);

                // 依赖缺失检测：把这个 mod 放进假目录，它不应报缺失
                DropDll(mm.PluginsDir, "RealOne.dll");
                File.Copy(realDll, Path.Combine(mm.PluginsDir, "RealOne.dll"), true);
                var missing = mm.FindMissingDependencies();
                Check("6.5 无依赖的模组不报缺失",
                      !missing.Any(kv => kv.Key.FileName == "RealOne.dll"),
                      "missing count=" + missing.Count);
            }
            else
            {
                Console.WriteLine("SKIP  6.x 真实 DLL 不存在，跳过元数据测试");
            }

            Console.WriteLine();
            Console.WriteLine("=== 7. 坏文件不崩 ===");
            DropDll(mm.PluginsDir, "Broken.dll");
            try
            {
                var s = mm.Scan();
                Check("7.1 扫描含坏文件仍成功", s.Count > 0, "count=" + s.Count);
                var b = s.First(x => x.FileName == "Broken.dll");
                Check("7.2 坏文件标记 MetadataOk=false", !b.MetadataOk, "err=" + b.MetadataError);
                mm.Disable(b);
                Check("7.3 坏文件仍能正常停用", !b.Enabled && File.Exists(b.FilePath), "");
            }
            catch (Exception e)
            {
                Check("7.x 坏文件不应让程序崩溃", false, e.Message);
            }

            Console.WriteLine();
            Console.WriteLine("=== 8. 状态导出 ===");
            string state = mm.ExportState();
            Check("8.1 导出包含表头", state.Contains("DSP Mod Manager state snapshot"), "");
            Check("8.2 导出包含模组行", state.Split('\n').Count(l => l.StartsWith("0\t") || l.StartsWith("1\t")) >= 2, "");

            Console.WriteLine();
            Console.WriteLine("=== 9. 清理 ===");
            try
            {
                Directory.Delete(g, true);
                Directory.Delete(bad, true);
                Check("9.1 临时目录已清理", true, "");
            }
            catch (Exception e) { Check("9.1 清理失败（无害）", true, e.Message); }

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "== ALL CORE CHECKS PASS ==" : ("== FAILURES: " + fails + " =="));
            return fails == 0 ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.WriteLine("FATAL " + e.GetType().Name + ": " + e.Message);
            Console.WriteLine(e.StackTrace);
            return 2;
        }
    }
}
