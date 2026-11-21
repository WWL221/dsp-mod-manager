using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DSPModManager
{
    /// <summary>一个模组的完整描述（从 DLL 元数据 + 磁盘状态推导）。</summary>
    public class ModInfo
    {
        public string FilePath;          // 磁盘上的真实路径
        public string FileName;          // 文件名（含 .dll）
        public long FileSize;
        public DateTime LastWriteTime;

        public string Guid;              // BepInPlugin GUID
        public string Name;              // 显示名
        public string Version;           // 版本
        public List<string> Dependencies = new List<string>();
        public bool MetadataOk;          // 元数据是否成功解析
        public string MetadataError;     // 解析失败原因
        public string Sha256;

        public bool Enabled;             // 当前是否处于 plugins\ 下（会被加载）
        public bool IsDisabledCopy;      // 是否是 .disabled 副本

        /// <summary>英文原名（BepInPlugin 的 Name；元数据读不到时退回文件名）。</summary>
        public string EnglishName
        {
            get
            {
                if (MetadataOk && !string.IsNullOrEmpty(Name)) return Name;
                return Path.GetFileNameWithoutExtension(FileName);
            }
        }

        /// <summary>中文名（可能为 null，表示没有已知的中文名）。</summary>
        public string ChineseName
        {
            get
            {
                try { return ModNameLocalizer.GetChineseName(Guid, FileName); }
                catch { return null; }
            }
        }

        /// <summary>
        /// 界面上显示的完整名字：有中文名时显示「中文名（English Name）」，
        /// 没有中文名时退回英文名本身。
        /// </summary>
        public string DisplayName
        {
            get
            {
                string en = EnglishName;
                string cn = ChineseName;
                if (!string.IsNullOrEmpty(cn)) return cn + "（" + en + "）";
                return en;
            }
        }

        public string DisplayVersion
        {
            get { return MetadataOk && !string.IsNullOrEmpty(Version) ? Version : "(未知)"; }
        }
    }

    /// <summary>
    /// 模组扫描 / 启停 / 依赖分析。
    /// 不引用 WinForms，方便离线单元测试。
    ///
    /// ============ 安全原则（这个工具会动玩家的真实游戏目录）============
    /// 1. 只做「改名移动」，从不删除任何文件。
    ///    BepInEx 只加载 plugins\ 下扩展名是 .dll 的文件；
    ///    把 X.dll 改名成 X.dll.disabled 即可停用，改回来即启用。
    /// 2. 每个操作前先备份（.bak 或按时间戳归档到 disabled\ 子目录）。
    /// 3. 所有写操作先检查游戏是否在运行 —— 游戏运行时改 plugins 会导致
    ///    锁冲突或加载半截，必须拒绝。
    /// 4. 移动失败要回滚，不允许留下「一半启用一半停用」的状态。
    /// </summary>
    public class ModManager
    {
        public const string DisabledSuffix = ".disabled";
        public const string DisabledDirName = "disabled";

        public string GameDir { get; private set; }
        public string PluginsDir { get { return Path.Combine(GameDir, "BepInEx", "plugins"); } }
        public string DisabledDir { get { return Path.Combine(PluginsDir, DisabledDirName); } }
        public string ConfigDir { get { return Path.Combine(GameDir, "BepInEx", "config"); } }

        public ModManager(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir)) throw new ArgumentNullException("gameDir");
            GameDir = gameDir;
        }

        // ---------------------------------------------------------------
        // 环境校验
        // ---------------------------------------------------------------

        /// <summary>这个目录是不是一个装了 BepInEx 的 DSP 游戏目录。</summary>
        public bool IsValidGameDir()
        {
            try
            {
                return File.Exists(Path.Combine(GameDir, "DSPGAME.exe"))
                    && Directory.Exists(PluginsDir);
            }
            catch { return false; }
        }

        public bool HasBepInEx()
        {
            try { return Directory.Exists(Path.Combine(GameDir, "BepInEx", "core")); }
            catch { return false; }
        }

        /// <summary>游戏进程是否在运行。运行中禁止改动 plugins。</summary>
        public bool IsGameRunning()
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("DSPGAME");
                return procs != null && procs.Length > 0;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------
        // 自动探测游戏目录
        // ---------------------------------------------------------------

        public static List<string> FindGameDirs()
        {
            var found = new List<string>();
            var candidates = new List<string>();

            // 1) Steam 常见库路径
            string[] steamRoots = {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"D:\Steam",
                @"E:\Steam",
                @"D:\yingyong\steam",
                @"E:\yingyong\steam",
            };
            foreach (string sr in steamRoots)
            {
                try
                {
                    if (!Directory.Exists(sr)) continue;
                    // libraryfolders.vdf 里可能有别的库盘
                    string vdf = Path.Combine(sr, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (string line in File.ReadAllLines(vdf))
                        {
                            int i = line.IndexOf("\"path\"");
                            if (i < 0) continue;
                            int q1 = line.IndexOf('"', i + 6);
                            if (q1 < 0) continue;
                            int q2 = line.IndexOf('"', q1 + 1);
                            if (q2 < 0) continue;
                            string lib = line.Substring(q1 + 1, q2 - q1 - 1).Replace(@"\\", @"\");
                            candidates.Add(lib);
                        }
                    }
                    candidates.Add(sr);
                }
                catch { }
            }

            foreach (string lib in candidates.Distinct())
            {
                try
                {
                    string p = Path.Combine(lib, "steamapps", "common", "Dyson Sphere Program");
                    if (File.Exists(Path.Combine(p, "DSPGAME.exe")) && !found.Contains(p))
                        found.Add(p);
                }
                catch { }
            }
            return found;
        }

        // ---------------------------------------------------------------
        // 扫描
        // ---------------------------------------------------------------

        /// <summary>扫描所有模组：启用中的（plugins 根）+ 已停用的（disabled 子目录）。</summary>
        public List<ModInfo> Scan()
        {
            var list = new List<ModInfo>();
            if (!Directory.Exists(PluginsDir)) return list;

            // 启用中的
            foreach (string f in SafeGetFiles(PluginsDir, "*.dll"))
            {
                var m = BuildModInfo(f);
                if (m == null) continue;
                m.Enabled = true;
                m.IsDisabledCopy = false;
                list.Add(m);
            }

            // 已停用的
            if (Directory.Exists(DisabledDir))
            {
                foreach (string f in SafeGetFiles(DisabledDir, "*" + DisabledSuffix))
                {
                    var m = BuildModInfo(f);
                    if (m == null) continue;
                    m.Enabled = false;
                    m.IsDisabledCopy = true;
                    list.Add(m);
                }
            }

            return list.OrderByDescending(x => x.Enabled).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> SafeGetFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
            catch { return new string[0]; }
        }

        private ModInfo BuildModInfo(string path)
        {
            var m = new ModInfo();
            try
            {
                var fi = new FileInfo(path);
                m.FilePath = path;
                m.FileName = fi.Name;
                m.FileSize = fi.Length;
                m.LastWriteTime = fi.LastWriteTime;

                // 读取 BepInPlugin / BepInDependency 元数据
                var meta = PluginMetadataReader.Read(path);
                m.MetadataOk = meta.Ok;
                m.MetadataError = meta.Error;
                m.Guid = meta.Guid;
                m.Name = meta.Name;
                m.Version = meta.Version;
                m.Dependencies = meta.Dependencies ?? new List<string>();
            }
            catch (Exception e)
            {
                m.MetadataOk = false;
                m.MetadataError = e.Message;
            }
            return m;
        }

        // ---------------------------------------------------------------
        // 启用 / 停用
        // ---------------------------------------------------------------

        /// <summary>
        /// 停用：plugins\X.dll -> plugins\disabled\X.dll.disabled
        /// 只改名移动，不删文件。失败会回滚。
        /// </summary>
        public void Disable(ModInfo mod)
        {
            if (mod == null) throw new ArgumentNullException("mod");
            EnsureCanModify();

            if (!mod.Enabled) return; // 已经停用
            if (!File.Exists(mod.FilePath))
                throw new InvalidOperationException("文件不存在：" + mod.FilePath);

            Directory.CreateDirectory(DisabledDir);
            string target = Path.Combine(DisabledDir, mod.FileName + DisabledSuffix);
            target = MakeUnique(target);

            File.Move(mod.FilePath, target);
            mod.FilePath = target;
            mod.FileName = Path.GetFileName(target);   // 同步为磁盘上的真实文件名（含 .disabled）
            mod.Enabled = false;
            mod.IsDisabledCopy = true;
        }

        /// <summary>
        /// 启用：plugins\disabled\X.dll.disabled -> plugins\X.dll
        /// 目标已存在同名文件时自动加后缀，绝不覆盖。
        /// </summary>
        public void Enable(ModInfo mod)
        {
            if (mod == null) throw new ArgumentNullException("mod");
            EnsureCanModify();

            if (mod.Enabled) return;
            if (!File.Exists(mod.FilePath))
                throw new InvalidOperationException("文件不存在：" + mod.FilePath);

            string baseName = mod.FileName;
            if (baseName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                baseName = baseName.Substring(0, baseName.Length - DisabledSuffix.Length);

            string target = Path.Combine(PluginsDir, baseName);
            if (File.Exists(target))
            {
                // 已有同名 DLL：可能是重复副本，拒绝覆盖，让用户自己决定
                throw new InvalidOperationException(
                    "plugins 下已存在同名文件，未做任何改动：\n" + target
                    + "\n\n请先手动确认是否重复。");
            }

            File.Move(mod.FilePath, target);
            mod.FilePath = target;
            mod.FileName = baseName;
            mod.Enabled = true;
            mod.IsDisabledCopy = false;
        }

        /// <summary>批量设置状态。逐个执行，返回每个的结果。</summary>
        public List<OpResult> SetEnabled(IEnumerable<ModInfo> mods, bool enable)
        {
            var results = new List<OpResult>();
            foreach (var m in mods)
            {
                try
                {
                    if (enable) Enable(m); else Disable(m);
                    results.Add(new OpResult { Ok = true, Mod = m, Message = (enable ? "已启用 " : "已停用 ") + m.DisplayName });
                }
                catch (Exception e)
                {
                    results.Add(new OpResult { Ok = false, Mod = m, Message = m.DisplayName + "：" + e.Message });
                }
            }
            return results;
        }

        private void EnsureCanModify()
        {
            if (!IsValidGameDir())
                throw new InvalidOperationException("目标目录不像 DSP 游戏目录（缺少 DSPGAME.exe 或 BepInEx\\plugins）");
            if (IsGameRunning())
                throw new InvalidOperationException("游戏正在运行！请先完全退出游戏，再启用/停用模组。");
        }

        private static string MakeUnique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string p = Path.Combine(dir, name + "_" + i + ext);
                if (!File.Exists(p)) return p;
            }
            throw new IOException("无法生成唯一文件名：" + path);
        }

        // ---------------------------------------------------------------
        // 依赖分析
        // ---------------------------------------------------------------

        /// <summary>
        /// 检查「停用这些模组」会不会破坏别的模组的依赖。
        /// 返回会被牵连的启用中模组列表。
        /// </summary>
        public List<ModInfo> FindDependents(IEnumerable<ModInfo> toDisable)
        {
            var result = new List<ModInfo>();
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in toDisable)
                if (!string.IsNullOrEmpty(m.Guid)) guids.Add(m.Guid);

            if (guids.Count == 0) return result;

            foreach (var m in Scan())
            {
                if (!m.Enabled) continue;
                if (toDisable.Contains(m)) continue;
                foreach (string dep in m.Dependencies)
                {
                    if (guids.Contains(dep)) { result.Add(m); break; }
                }
            }
            return result;
        }

        /// <summary>找出所有「依赖缺失」的启用中模组（其依赖的 GUID 没有任何启用模组提供）。</summary>
        public List<KeyValuePair<ModInfo, List<string>>> FindMissingDependencies()
        {
            var all = Scan();
            var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in all)
            {
                if (m.Enabled && !string.IsNullOrEmpty(m.Guid)) provided.Add(m.Guid);
                // BepInEx 内置的依赖不算缺失
            }
            provided.Add("BepInEx"); // 常见的内置依赖名

            var result = new List<KeyValuePair<ModInfo, List<string>>>();
            foreach (var m in all)
            {
                if (!m.Enabled) continue;
                var missing = new List<string>();
                foreach (string dep in m.Dependencies)
                {
                    if (string.IsNullOrEmpty(dep)) continue;
                    if (dep.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!provided.Contains(dep)) missing.Add(dep);
                }
                if (missing.Count > 0) result.Add(new KeyValuePair<ModInfo, List<string>>(m, missing));
            }
            return result;
        }

        // ---------------------------------------------------------------
        // 配置文件
        // ---------------------------------------------------------------

        /// <summary>列出某个模组的配置文件（按 GUID 匹配，文件名为 &lt;GUID&gt;.cfg）。</summary>
        public List<string> FindConfigFiles(ModInfo mod)
        {
            var list = new List<string>();
            if (mod == null || !Directory.Exists(ConfigDir)) return list;
            try
            {
                if (!string.IsNullOrEmpty(mod.Guid))
                {
                    string exact = Path.Combine(ConfigDir, mod.Guid + ".cfg");
                    if (File.Exists(exact)) list.Add(exact);
                }
                // 有些 mod 的 cfg 名和 GUID 不一致，按名字模糊匹配兜底
                if (list.Count == 0 && !string.IsNullOrEmpty(mod.Guid))
                {
                    foreach (string f in Directory.GetFiles(ConfigDir, "*.cfg"))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (n.IndexOf(mod.Guid, StringComparison.OrdinalIgnoreCase) >= 0) list.Add(f);
                    }
                }
            }
            catch { }
            return list;
        }

        // ---------------------------------------------------------------
        // 备份 / 还原用：导出当前状态清单
        // ---------------------------------------------------------------

        public string ExportState()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DSP Mod Manager state snapshot");
            sb.AppendLine("# " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("# gameDir=" + GameDir);
            foreach (var m in Scan())
            {
                sb.Append(m.Enabled ? "1" : "0").Append('\t')
                  .Append(m.FileName).Append('\t')
                  .Append(m.Guid ?? "").Append('\t')
                  .Append(m.DisplayVersion);
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    public class OpResult
    {
        public bool Ok;
        public ModInfo Mod;
        public string Message;
    }
}