using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace DSPModManager
{
    public class ModMetadata
    {
        public bool Ok;
        public string Error;
        public string Guid;
        public string Name;
        public string Version;
        public List<string> Dependencies = new List<string>();
    }

    /// <summary>
    /// 读取 BepInEx 插件的 [BepInPlugin] / [BepInDependency] 元数据。
    ///
    /// ============ 为什么不用 Assembly.LoadFrom ============
    /// LoadFrom 会把目标 DLL 真正加载进当前 AppDomain 并解析它的所有依赖。
    /// 模组 DLL 依赖 Assembly-CSharp / UnityEngine 等游戏程序集，
    /// 在管理器进程里解析不到会抛 ReflectionTypeLoadException，甚至可能
    /// 因为静态构造器副作用把管理器弄崩。管理器只是要「读几个字符串」，不能这么干。
    ///
    /// 所以优先用 Mono.Cecil —— 纯元数据读取，不执行任何代码。
    /// 找不到 Cecil 时降级到 AssemblyName.GetAssemblyName（只读程序集标识，也不执行代码），
    /// 那种情况下拿不到 GUID/依赖，但至少不会崩。
    /// </summary>
    public static class PluginMetadataReader
    {
        private static Type _cecilAssemblyDef;
        private static MethodInfo _cecilRead;
        private static bool _cecilInit;
        private static string _cecilErr;

        /// <summary>让调用方指定 Mono.Cecil.dll 的位置（通常在 BepInEx\core\ 下）。</summary>
        public static void ConfigureCecil(string cecilPath)
        {
            _cecilInit = false;
            _cecilAssemblyDef = null;
            _cecilRead = null;
            _cecilErr = null;

            try
            {
                if (string.IsNullOrEmpty(cecilPath) || !File.Exists(cecilPath))
                {
                    _cecilErr = "未找到 Mono.Cecil.dll：" + cecilPath;
                    _cecilInit = true;
                    return;
                }
                Assembly asm = Assembly.LoadFrom(cecilPath);
                Type t = asm.GetType("Mono.Cecil.AssemblyDefinition");
                if (t == null) { _cecilErr = "Mono.Cecil 中找不到 AssemblyDefinition 类型"; _cecilInit = true; return; }
                MethodInfo read = t.GetMethod("ReadAssembly", new Type[] { typeof(string) });
                if (read == null) { _cecilErr = "Mono.Cecil 中找不到 ReadAssembly(string)"; _cecilInit = true; return; }
                _cecilAssemblyDef = t;
                _cecilRead = read;
            }
            catch (Exception e)
            {
                _cecilErr = "加载 Mono.Cecil 失败：" + e.Message;
            }
            finally
            {
                _cecilInit = true;
            }
        }

        public static ModMetadata Read(string dllPath)
        {
            var md = new ModMetadata();
            if (!_cecilInit) ConfigureCecil(null);

            if (_cecilRead != null)
            {
                try { return ReadWithCecil(dllPath); }
                catch (Exception e)
                {
                    md.Ok = false;
                    md.Error = "Cecil 读取失败：" + e.Message;
                    return md;
                }
            }

            // 降级：只拿程序集名，至少能显示个版本
            try
            {
                AssemblyName an = AssemblyName.GetAssemblyName(dllPath);
                md.Ok = false;
                md.Name = an.Name;
                md.Version = an.Version != null ? an.Version.ToString() : null;
                md.Error = _cecilErr ?? "未启用 Mono.Cecil，无法读取 BepInPlugin 特性";
                return md;
            }
            catch (Exception e)
            {
                md.Ok = false;
                md.Error = "不是有效的 .NET 程序集：" + e.Message;
                return md;
            }
        }

        private static ModMetadata ReadWithCecil(string dllPath)
        {
            var md = new ModMetadata();
            md.Ok = false;

            object asm = _cecilRead.Invoke(null, new object[] { dllPath });
            if (asm == null) { md.Error = "Cecil 返回 null"; return md; }

            try
            {
                // asm.MainModule.Types -> 遍历找带 BepInPlugin 的类型
                object module = GetProp(asm, "MainModule");
                object types = GetProp(module, "Types");
                var typeList = (System.Collections.IEnumerable)types;

                foreach (object t in typeList)
                {
                    object attrs = GetProp(t, "CustomAttributes");
                    if (attrs == null) continue;
                    foreach (object ca in (System.Collections.IEnumerable)attrs)
                    {
                        object at = GetProp(ca, "AttributeType");
                        string atName = at != null ? (string)GetProp(at, "Name") : null;
                        if (atName == "BepInPlugin")
                        {
                            object args = GetProp(ca, "ConstructorArguments");
                            var vals = new List<string>();
                            if (args != null)
                                foreach (object a in (System.Collections.IEnumerable)args)
                                {
                                    object av = GetProp(a, "Value");
                                    vals.Add(av == null ? "" : av.ToString());
                                }
                            if (vals.Count >= 3)
                            {
                                md.Guid = vals[0];
                                md.Name = vals[1];
                                md.Version = vals[2];
                                md.Ok = true;
                            }
                            else
                            {
                                md.Error = "BepInPlugin 参数不足（应为 GUID/Name/Version 三个）";
                            }
                        }
                        else if (atName == "BepInDependency")
                        {
                            object args = GetProp(ca, "ConstructorArguments");
                            if (args != null)
                                foreach (object a in (System.Collections.IEnumerable)args)
                                {
                                    object av = GetProp(a, "Value");
                                    if (av != null)
                                    {
                                        string s = av.ToString();
                                        if (!string.IsNullOrEmpty(s) && !md.Dependencies.Contains(s))
                                            md.Dependencies.Add(s);
                                    }
                                }
                        }
                    }
                    if (md.Ok) break; // 找到主插件类就够了
                }

                if (!md.Ok && string.IsNullOrEmpty(md.Error))
                    md.Error = "未找到 [BepInPlugin] 特性（可能不是 BepInEx 插件）";
                return md;
            }
            finally
            {
                // Cecil 的 AssemblyDefinition 实现了 IDisposable，读完整理掉
                try
                {
                    var d = asm as IDisposable;
                    if (d != null) d.Dispose();
                }
                catch { }
            }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                Type t = obj.GetType();
                PropertyInfo p = t.GetProperty(name);
                if (p != null) return p.GetValue(obj, null);
                FieldInfo f = t.GetField(name);
                if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }
    }
}