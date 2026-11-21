using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DSPModManager
{
    /// <summary>
    /// 给模组名补中文。显示格式：English Name（中文名）
    ///
    /// 数据来源分两层：
    ///  1) BuiltIn —— 本项目自己产出的模组，中文名确定，内置。
    ///  2) 外部对照表 mod-names.txt —— 用户可编辑，用来给别人的模组补中文名。
    ///     格式：GUID 或 DLL 文件名 &lt;Tab&gt; 中文名；# 开头为注释。
    ///
    /// 为什么不在程序里硬编码一大堆社区模组的中文名：
    /// 猜错名字比不显示中文更糟（用户会以为装错了模组）。所以只内置我们确定知道的，
    /// 其余留空白，由用户在对照表里自己填，填完刷新即可生效。
    /// </summary>
    public static class ModNameLocalizer
    {
        /// <summary>本项目的模组（GUID -> 中文名），确定无疑的才写进来。</summary>
        private static readonly Dictionary<string, string> BuiltIn =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "com.lead.dsp.mechaboost",     "伊卡洛斯增强" },
            { "com.dsp.noslopebuild",        "无视地形建造" },
            { "com.lead.dsp.windturbine200x","风力发电机200倍" },
        };

        private static Dictionary<string, string> _overrides;
        private static string _filePath;
        private static bool _loaded;

        /// <summary>对照表文件路径（放在 exe 同级目录）。</summary>
        public static string FilePath { get { return _filePath; } }

        public static void Configure(string path)
        {
            _filePath = path;
            _overrides = null;
            _loaded = false;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;
                foreach (string raw in File.ReadAllLines(_filePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;

                    string key = line.Substring(0, tab).Trim();
                    string val = line.Substring(tab + 1);

                    // 剥掉行尾注释（模板会自动加 "# 英文名" 在末尾作为提示，
                    // 不剥掉的话中文名会变成 "# DSP Mecha Boost" 这种垃圾值）
                    int hash = val.IndexOf('#');
                    if (hash >= 0) val = val.Substring(0, hash);
                    val = val.Trim();

                    if (key.Length == 0 || val.Length == 0) continue;

                    _overrides[key] = val;
                }
            }
            catch
            {
                // 对照表读不了不能影响主功能，静默降级为「只有内置中文名」
                _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 取中文名。查不到返回 null（调用方据此决定是否加括号）。
        /// 匹配顺序：外部对照表(GUID) -> 外部对照表(文件名) -> 内置表(GUID)
        /// 外部优先，这样用户能覆盖内置名。
        /// </summary>
        public static string GetChineseName(string guid, string fileName)
        {
            EnsureLoaded();

            if (!string.IsNullOrEmpty(guid) && _overrides.ContainsKey(guid))
                return _overrides[guid];

            string stem = Stem(fileName);
            if (!string.IsNullOrEmpty(stem))
            {
                if (_overrides.ContainsKey(stem)) return _overrides[stem];
                if (_overrides.ContainsKey(stem + ".dll")) return _overrides[stem + ".dll"];
            }

            if (!string.IsNullOrEmpty(guid) && BuiltIn.ContainsKey(guid))
                return BuiltIn[guid];

            return null;
        }

        /// <summary>去掉 .dll / .disabled 后缀，得到用于匹配的名字主体。</summary>
        private static string Stem(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            string s = fileName;
            if (s.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - ".disabled".Length);
            if (s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - ".dll".Length);
            return s;
        }

        /// <summary>
        /// 生成/更新对照表模板：把还没有中文名的模组列进去，用户填完保存、点刷新即可。
        /// 已存在且已有中文名的条目原样保留，不会覆盖用户已填的内容。
        /// </summary>
        public static string WriteTemplate(IEnumerable<ModInfo> mods, string path)
        {
            var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                try
                {
                    foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        existing[line.Substring(0, tab).Trim()] = line.Substring(tab + 1).Trim();
                    }
                }
                catch { }
            }

            var sb = new StringBuilder();
            sb.AppendLine("# DSP 模组管理器 —— 模组中文名对照表");
            sb.AppendLine("#");
            sb.AppendLine("# 格式：<GUID 或 DLL文件名> <Tab> <中文名>");
            sb.AppendLine("# 以 # 开头的行是注释。改完保存，回到管理器点「刷新」即可生效。");
            sb.AppendLine("# 留空不填的模组就只显示英文名。");
            sb.AppendLine();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods)
            {
                if (string.IsNullOrEmpty(m.Guid)) continue;
                if (seen.Contains(m.Guid)) continue;
                seen.Add(m.Guid);

                string cn;
                existing.TryGetValue(m.Guid, out cn);
                if (string.IsNullOrEmpty(cn))
                {
                    string builtin = GetChineseName(m.Guid, m.FileName);
                    cn = builtin ?? "";
                }
                sb.Append(m.Guid).Append('\t').Append(cn);
                sb.Append("        # ").AppendLine(m.Name ?? m.FileName);
            }

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            return path;
        }
    }
}