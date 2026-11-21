using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace DSPModManager
{
    /// <summary>布局诊断：把窗口里的控件树和实际 Bounds 打印出来，用于定位布局问题。</summary>
    public static class LayoutDump
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new MainForm();
            form.Width = 1000;
            form.Height = 700;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(0, 0);

            // 显示后立即 dump，再关闭
            form.Shown += delegate
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(1200);
                Application.DoEvents();
                Dump(form, 0);
                form.Close();
            };
            Application.Run(form);
            return 0;
        }

        static void Dump(Control c, int depth)
        {
            string ind = new string(' ', depth * 2);
            string name = string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name + " [" + c.GetType().Name + "]";
            string text = "";
            try { text = c.Text != null && c.Text.Length > 0 ? " \"" + (c.Text.Length > 22 ? c.Text.Substring(0, 22) + "..." : c.Text) + "\"" : ""; } catch { }
            Console.WriteLine(ind + name + "  bounds=" + c.Bounds + " visible=" + c.Visible + text);

            foreach (Control ch in c.Controls) Dump(ch, depth + 1);
        }
    }
}