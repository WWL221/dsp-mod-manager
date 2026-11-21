using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace DSPModManager
{
    /// <summary>
    /// DSP 模组管理器主窗口。
    /// 独立程序，普通用户权限，不注入游戏、不改游戏文件内容，
    /// 只在 BepInEx\plugins 下做「改名移动」来启停模组。
    /// </summary>
    public class MainForm : Form
    {
        private ModManager _mgr;
        private List<ModInfo> _mods = new List<ModInfo>();

        private ComboBox _cmbGameDir;
        private Button _btnBrowse, _btnRefresh, _btnLaunch;
        private TextBox _txtSearch;
        private DataGridView _grid;
        private TextBox _txtDetail;
        private Button _btnEnable, _btnDisable, _btnOpenPlugins, _btnOpenConfig, _btnExport;
        private Label _lblStatus, _lblGameState;
        private Timer _timerProc;

        public MainForm()
        {
            // 中文名对照表放在 exe 同级目录，用户可自行编辑
            try
            {
                ModNameLocalizer.Configure(Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath), "mod-names.txt"));
            }
            catch { }

            BuildUi();
            TryInitGameDir();
        }

        // ===============================================================
        // UI
        // ===============================================================
        private void BuildUi()
        {
            Text = "DSP 模组管理器  —  戴森球计划 / BepInEx";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = SystemColors.Control;

            // 自适应屏幕，避免窗口超出可见区域（之前 1080 宽会被截断）
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min(1000, wa.Width - 80);
            int h = Math.Min(700, wa.Height - 80);
            Width = Math.Max(820, w);
            Height = Math.Max(560, h);
            MinimumSize = new Size(820, 560);

            // 用 TableLayoutPanel 做三行布局，顺序确定，不依赖 Dock 的 z-order
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8, 6, 8, 6)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84f));   // 顶部
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 中间
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));  // 底部
            Controls.Add(root);

            // ---------- 顶部 ----------
            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 2,
                Margin = new Padding(0)
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74f));   // 标签
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));   // 下拉
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82f));   // 浏览
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));   // 刷新
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104f));  // 启动
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));

            top.Controls.Add(MakeLabel("游戏目录："), 0, 0);

            _cmbGameDir = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 5, 6, 5)
            };
            _cmbGameDir.SelectedIndexChanged += delegate { OnGameDirChanged(); };
            top.Controls.Add(_cmbGameDir, 1, 0);

            _btnBrowse = MakeButton("浏览...");
            _btnBrowse.Click += delegate { BrowseGameDir(); };
            top.Controls.Add(_btnBrowse, 2, 0);

            _btnRefresh = MakeButton("刷新");
            _btnRefresh.Click += delegate { ReloadMods(); };
            top.Controls.Add(_btnRefresh, 3, 0);

            _btnLaunch = MakeButton("启动游戏");
            _btnLaunch.Click += delegate { LaunchGame(); };
            top.Controls.Add(_btnLaunch, 4, 0);

            // 第二行：筛选框（左）+ 游戏状态（右侧剩余全部）
            var filterPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 3, 0, 3)
            };
            var lblFilter = new Label { Text = "筛选：", AutoSize = true, Margin = new Padding(0, 7, 0, 0) };
            _txtSearch = new TextBox { Width = 200, Margin = new Padding(0, 3, 6, 3) };
            _txtSearch.TextChanged += delegate { BindGrid(); };
            filterPanel.Controls.Add(lblFilter);
            filterPanel.Controls.Add(_txtSearch);
            top.Controls.Add(filterPanel, 0, 1);
            top.SetColumnSpan(filterPanel, 2);

            _lblGameState = new Label
            {
                Dock = DockStyle.Fill,
                Text = "未检测",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                AutoEllipsis = true,
                Margin = new Padding(4, 0, 0, 0)
            };
            top.Controls.Add(_lblGameState, 2, 1);
            top.SetColumnSpan(_lblGameState, 3);

            root.Controls.Add(top, 0, 0);

            // ---------- 中间：列表 + 详情 ----------
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Margin = new Padding(0, 4, 0, 4),
                Panel1MinSize = 160,
                Panel2MinSize = 90
            };
            // SplitterDistance 必须在尺寸确定后设置，否则会抛异常
            split.SizeChanged += delegate
            {
                try
                {
                    int target = (int)(split.Height * 0.62);
                    if (target > split.Panel1MinSize && target < split.Height - split.Panel2MinSize)
                        split.SplitterDistance = target;
                }
                catch { }
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 30,
                EnableHeadersVisualStyles = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                RowTemplate = { Height = 26 }
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Enabled", HeaderText = "状态", Width = 72 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CnName", HeaderText = "中文名", Width = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "英文名", Width = 210 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "版本", Width = 80 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Guid", HeaderText = "GUID", Width = 230, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "文件", Width = 180 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Dep", HeaderText = "依赖", Width = 100 });
            _grid.SelectionChanged += delegate { ShowDetail(); };
            _grid.CellDoubleClick += delegate { ToggleSelectedOne(); };
            _grid.ColumnHeaderMouseClick += delegate(object s, DataGridViewCellMouseEventArgs e) { SortByColumn(e.ColumnIndex); };
            split.Panel1.Controls.Add(_grid);

            _txtDetail = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle
            };
            split.Panel2.Controls.Add(_txtDetail);

            root.Controls.Add(split, 0, 1);

            // ---------- 底部 ----------
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 7,
                RowCount = 2,
                Margin = new Padding(0)
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));

            _btnEnable = MakeButton("启用所选");
            _btnEnable.Click += delegate { ApplyStateToSelected(true); };
            bottom.Controls.Add(_btnEnable, 0, 0);

            _btnDisable = MakeButton("停用所选");
            _btnDisable.Click += delegate { ApplyStateToSelected(false); };
            bottom.Controls.Add(_btnDisable, 1, 0);

            _btnOpenPlugins = MakeButton("打开 plugins");
            _btnOpenPlugins.Click += delegate { OpenFolder(_mgr != null ? _mgr.PluginsDir : null); };
            bottom.Controls.Add(_btnOpenPlugins, 2, 0);

            _btnOpenConfig = MakeButton("打开 config");
            _btnOpenConfig.Click += delegate { OpenFolder(_mgr != null ? _mgr.ConfigDir : null); };
            bottom.Controls.Add(_btnOpenConfig, 3, 0);

            _btnExport = MakeButton("导出状态清单");
            _btnExport.Click += delegate { ExportState(); };
            bottom.Controls.Add(_btnExport, 4, 0);

            var lblHint = new Label
            {
                Text = "双击某行可快速切换启用/停用；改动需重启游戏生效。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                AutoEllipsis = true,
                Margin = new Padding(8, 0, 0, 0)
            };
            var btnNames = MakeButton("编辑中文名");
            btnNames.Click += delegate { EditNameTable(); };
            bottom.Controls.Add(btnNames, 5, 0);

            bottom.Controls.Add(lblHint, 6, 0);

            _lblStatus = new Label
            {
                Text = "就绪",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                AutoEllipsis = true,
                Margin = new Padding(0)
            };
            bottom.Controls.Add(_lblStatus, 0, 1);
            bottom.SetColumnSpan(_lblStatus, 6);

            root.Controls.Add(bottom, 0, 2);

            _timerProc = new Timer { Interval = 2000 };
            _timerProc.Tick += delegate { UpdateGameState(); };
            _timerProc.Start();
        }

        private static Label MakeLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
        }

        private static Button MakeButton(string text)
        {
            return new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 5, 6, 5)
            };
        }

        // ===============================================================
        // 初始化
        // ===============================================================
        private void TryInitGameDir()
        {
            try
            {
                var found = ModManager.FindGameDirs();
                _cmbGameDir.Items.Clear();
                foreach (string d in found) _cmbGameDir.Items.Add(d);

                string last = LoadLastDir();
                if (!string.IsNullOrEmpty(last) && Directory.Exists(last) && !_cmbGameDir.Items.Contains(last))
                    _cmbGameDir.Items.Insert(0, last);

                if (_cmbGameDir.Items.Count > 0)
                {
                    int idx = !string.IsNullOrEmpty(last) ? _cmbGameDir.Items.IndexOf(last) : 0;
                    _cmbGameDir.SelectedIndex = idx >= 0 ? idx : 0;
                }
                else
                {
                    SetStatus("未自动找到游戏目录，请点「浏览...」手动指定含 DSPGAME.exe 的目录。", true);
                }
            }
            catch (Exception e)
            {
                SetStatus("自动探测失败：" + e.Message, true);
            }
        }

        private string _lastDirFile
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "modmanager.lastdir"); }
        }

        private string LoadLastDir()
        {
            try { if (File.Exists(_lastDirFile)) return File.ReadAllText(_lastDirFile).Trim(); }
            catch { }
            return null;
        }

        private void SaveLastDir(string d)
        {
            try { File.WriteAllText(_lastDirFile, d ?? ""); } catch { }
        }

        private void BrowseGameDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择戴森球计划游戏目录（包含 DSPGAME.exe 的那一层）";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    if (!_cmbGameDir.Items.Contains(dlg.SelectedPath))
                        _cmbGameDir.Items.Add(dlg.SelectedPath);
                    _cmbGameDir.SelectedItem = dlg.SelectedPath;
                }
            }
        }

        private void OnGameDirChanged()
        {
            string dir = _cmbGameDir.SelectedItem as string;
            if (string.IsNullOrEmpty(dir)) return;

            _mgr = new ModManager(dir);
            SaveLastDir(dir);
            PluginMetadataReader.ConfigureCecil(Path.Combine(dir, "BepInEx", "core", "Mono.Cecil.dll"));

            if (!_mgr.IsValidGameDir())
            {
                _mods.Clear();
                BindGrid();
                SetStatus("这不是有效的 DSP 游戏目录（需要 DSPGAME.exe 和 BepInEx\\plugins）。", true);
                return;
            }
            if (!_mgr.HasBepInEx())
                SetStatus("检测到游戏，但没装 BepInEx（缺 BepInEx\\core）。", true);

            ReloadMods();
        }

        // ===============================================================
        // 扫描 / 绑定
        // ===============================================================
        private void ReloadMods()
        {
            if (_mgr == null) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                _mods = _mgr.Scan();
                BindGrid();
                SetStatus(string.Format("共 {0} 个模组：启用 {1} / 停用 {2}",
                    _mods.Count, _mods.Count(x => x.Enabled), _mods.Count(x => !x.Enabled)), false);
            }
            catch (Exception e)
            {
                SetStatus("扫描失败：" + e.Message, true);
            }
            finally
            {
                Cursor = Cursors.Default;
                UpdateGameState();
            }
        }

        private void BindGrid()
        {
            if (_grid == null) return;

            string filter = (_txtSearch.Text ?? "").Trim();
            _grid.Rows.Clear();

            foreach (var m in _mods)
            {
                if (filter.Length > 0)
                {
                    bool hit = Contains(m.DisplayName, filter) || Contains(m.Guid, filter)
                            || Contains(m.FileName, filter) || Contains(m.DisplayVersion, filter);
                    if (!hit) continue;
                }

                // Rows.Add(object[]) 遇到 null 元素会静默丢弃整行 —— 所有值都必须非 null
                object[] cells = new object[7];
                cells[0] = m.Enabled ? "● 启用" : "○ 停用";
                cells[1] = m.ChineseName ?? "";      // 未收录中文名时留空
                cells[2] = m.EnglishName ?? "";
                cells[3] = m.DisplayVersion ?? "";
                cells[4] = m.Guid ?? "";
                cells[5] = m.FileName ?? "";
                cells[6] = (m.Dependencies != null && m.Dependencies.Count > 0)
                         ? string.Join(", ", m.Dependencies.ToArray()) : "";

                int i = _grid.Rows.Add(cells);
                _grid.Rows[i].Tag = m;
                _grid.Rows[i].DefaultCellStyle.ForeColor = m.Enabled ? Color.Black : Color.Gray;

                if (!m.MetadataOk)
                {
                    _grid.Rows[i].Cells["Name"].Style.BackColor = Color.LightYellow;
                    _grid.Rows[i].Cells["Name"].ToolTipText = m.MetadataError;
                }
            }

            if (_grid.Rows.Count > 0 && _grid.SelectedRows.Count == 0)
                _grid.Rows[0].Selected = true;

            UpdateGameState();
        }

        private static bool Contains(string s, string sub)
        {
            return s != null && s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private List<ModInfo> SelectedMods()
        {
            var list = new List<ModInfo>();
            if (_grid == null) return list;
            foreach (DataGridViewRow r in _grid.SelectedRows)
            {
                var m = r.Tag as ModInfo;
                if (m != null && !list.Contains(m)) list.Add(m);
            }
            return list;
        }

        // ===============================================================
        // 启停
        // ===============================================================
        private void ToggleSelectedOne()
        {
            var sel = SelectedMods();
            if (sel.Count != 1) return;
            ApplyStateToSelected(!sel[0].Enabled);
        }

        private void ApplyStateToSelected(bool enable)
        {
            if (_mgr == null) return;

            var sel = SelectedMods();
            if (sel.Count == 0)
            {
                SetStatus("请先在列表中选择模组（可按住 Ctrl 多选）。", true);
                return;
            }

            if (_mgr.IsGameRunning())
            {
                MessageBox.Show(this,
                    "游戏正在运行！\n\n请先完全退出游戏（确认任务管理器里没有残留的 DSPGAME.exe），\n再启用/停用模组。",
                    "游戏运行中", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!enable)
            {
                var dependents = _mgr.FindDependents(sel);
                if (dependents.Count > 0)
                {
                    string names = string.Join("\n  ", dependents.Select(x => "· " + x.DisplayName).ToArray());
                    var r = MessageBox.Show(this,
                        "以下【已启用】的模组依赖你要停用的模组：\n\n  " + names
                        + "\n\n停用后它们会加载失败。仍要继续吗？",
                        "依赖警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                }
            }

            var results = _mgr.SetEnabled(sel, enable);
            int ok = results.Count(x => x.Ok);
            int bad = results.Count - ok;

            ReloadMods();

            if (bad == 0)
            {
                SetStatus(string.Format("{0} 个模组已{1}。需重启游戏生效。",
                    ok, enable ? "启用" : "停用"), false);
            }
            else
            {
                var sb = new StringBuilder("成功 " + ok + " 个，失败 " + bad + " 个：\n");
                foreach (var r in results.Where(x => !x.Ok)) sb.AppendLine("· " + r.Message);
                SetStatus("部分操作失败", true);
                MessageBox.Show(this, sb.ToString(), "操作结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateGameState();
        }

        // ===============================================================
        // 详情
        // ===============================================================
        private void ShowDetail()
        {
            if (_txtDetail == null) return;
            var sel = SelectedMods();
            var sb = new StringBuilder();

            if (sel.Count == 0)
            {
                _txtDetail.Text = "（在上方列表中选择一个模组查看详情；双击某行可切换启用/停用）";
                return;
            }

            if (sel.Count > 1)
            {
                sb.AppendLine("已选中 " + sel.Count + " 个模组：");
                sb.AppendLine();
                foreach (var m in sel)
                    sb.AppendLine("· " + m.DisplayName + "   " + (m.Enabled ? "[启用]" : "[停用]"));
                _txtDetail.Text = sb.ToString();
                return;
            }

            var mod = sel[0];
            sb.AppendLine("中文名    : " + (mod.ChineseName ?? "(未收录，可在 mod-names.txt 里补)"));
            sb.AppendLine("英文名    : " + mod.EnglishName);
            sb.AppendLine("版本      : " + mod.DisplayVersion);
            sb.AppendLine("状态      : " + (mod.Enabled ? "启用（会被游戏加载）" : "停用（不会被加载）"));
            sb.AppendLine("GUID      : " + (mod.Guid ?? "(无)"));
            sb.AppendLine("文件      : " + mod.FileName);
            sb.AppendLine("路径      : " + mod.FilePath);
            sb.AppendLine("大小      : " + FormatSize(mod.FileSize));
            sb.AppendLine("修改时间  : " + mod.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("依赖      : " + ((mod.Dependencies != null && mod.Dependencies.Count > 0)
                ? string.Join(", ", mod.Dependencies.ToArray()) : "(无)"));

            if (!mod.MetadataOk)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ 元数据读取提示：");
                sb.AppendLine("  " + mod.MetadataError);
                sb.AppendLine("  （仍可通过改文件名正常启停，只是显示信息不全）");
            }

            if (_mgr != null)
            {
                var cfgs = _mgr.FindConfigFiles(mod);
                sb.AppendLine();
                if (cfgs.Count > 0)
                {
                    sb.AppendLine("配置文件：");
                    foreach (string c in cfgs)
                    {
                        long len = 0;
                        try { len = new FileInfo(c).Length; } catch { }
                        sb.AppendLine("  " + c + "   (" + FormatSize(len) + ")");
                    }
                }
                else
                {
                    sb.AppendLine("配置文件：未找到（模组首次运行后才会生成）");
                }
            }

            _txtDetail.Text = sb.ToString();
        }

        private static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.0") + " KB";
            return (b / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }

        private void SortByColumn(int col)
        {
            if (_mods.Count == 0 || col < 0) return;
            string name = _grid.Columns[col].Name;
            switch (name)
            {
                case "Enabled": _mods = _mods.OrderByDescending(x => x.Enabled).ThenBy(x => x.DisplayName).ToList(); break;
                case "Version": _mods = _mods.OrderBy(x => x.DisplayVersion).ToList(); break;
                case "Guid": _mods = _mods.OrderBy(x => x.Guid).ToList(); break;
                case "File": _mods = _mods.OrderBy(x => x.FileName).ToList(); break;
                default: _mods = _mods.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(); break;
            }
            BindGrid();
        }

        // ===============================================================
        // 状态
        // ===============================================================
        private void UpdateGameState()
        {
            if (_lblGameState == null) return;

            if (_mgr == null)
            {
                _lblGameState.Text = "未选择游戏目录";
                _lblGameState.ForeColor = Color.DimGray;
                return;
            }

            bool running = _mgr.IsGameRunning();
            if (running)
            {
                _lblGameState.Text = "● 游戏运行中 —— 启用/停用已锁定，请先退出游戏";
                _lblGameState.ForeColor = Color.Firebrick;
                if (_btnEnable != null) _btnEnable.Enabled = false;
                if (_btnDisable != null) _btnDisable.Enabled = false;
            }
            else
            {
                _lblGameState.Text = "○ 游戏未运行 —— 可以安全启停模组";
                _lblGameState.ForeColor = Color.SeaGreen;
                if (_btnEnable != null) _btnEnable.Enabled = true;
                if (_btnDisable != null) _btnDisable.Enabled = true;
            }
        }

        private void LaunchGame()
        {
            try
            {
                // 必须走 Steam：直接运行 DSPGAME.exe 会 SteamAPI_Init 失败
                Process.Start(new ProcessStartInfo("steam://rungameid/1366540") { UseShellExecute = true });
                SetStatus("已请求 Steam 启动游戏；启动后启停功能会自动锁定。", false);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "启动失败：" + e.Message + "\n\n请手动从 Steam 启动游戏。",
                    "启动游戏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    MessageBox.Show(this, "目录不存在：\n" + (path ?? "(空)"), "打开目录",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "打开失败：" + e.Message, "打开目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExportState()
        {
            if (_mgr == null) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "文本文件|*.txt|所有文件|*.*";
                dlg.FileName = "dsp-mod-state-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, _mgr.ExportState(), Encoding.UTF8);
                    SetStatus("已导出状态清单：" + dlg.FileName, false);
                }
                catch (Exception e)
                {
                    MessageBox.Show(this, "导出失败：" + e.Message, "导出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        /// <summary>
        /// 生成/打开中文名对照表。
        /// 模板会保留用户已填的内容，只补上还没有中文名的模组。
        /// 用户改完保存、关掉记事本，这里会自动重新加载并刷新列表。
        /// </summary>
        private void EditNameTable()
        {
            if (_mgr == null) return;

            string path = ModNameLocalizer.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show(this, "内部错误：对照表路径未初始化。", "编辑中文名",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                ModNameLocalizer.WriteTemplate(_mods, path);

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

                MessageBox.Show(this,
                    "已打开中文名对照表：\n" + path + "\n\n"
                    + "格式：GUID <Tab> 中文名\n"
                    + "（每行一个，以 # 开头的是注释）\n\n"
                    + "填好后保存并关闭记事本，再点「确定」即可刷新列表。",
                    "编辑中文名", MessageBoxButtons.OK, MessageBoxIcon.Information);

                // 用户改完回来了：丢掉缓存，重新读表并刷新
                ModNameLocalizer.Configure(path);
                ReloadMods();
                SetStatus("已重新加载中文名对照表。", false);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "打开对照表失败：\n" + e.Message, "编辑中文名",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SetStatus(string msg, bool warn)
        {
            if (_lblStatus == null) return;
            _lblStatus.Text = msg;
            _lblStatus.ForeColor = warn ? Color.Firebrick : Color.DimGray;
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception e)
            {
                MessageBox.Show("程序启动失败：\n\n" + e, "DSP 模组管理器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}