using System;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DSPModManager
{
    /// <summary>把窗体直接渲染成 PNG（DrawToBitmap），不受窗口位置/遮挡影响。</summary>
    public static class ShotTool
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string outPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "dspmm-ui.png");

            var form = new MainForm();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-3000, -3000);  // 挪到屏幕外，但依然会被渲染
            form.Width = 1010;
            form.Height = 720;
            form.ShowInTaskbar = false;

            form.Shown += delegate
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(1500);   // 等扫描完成
                Application.DoEvents();

                try
                {
                    var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(outPath, ImageFormat.Png);
                    bmp.Dispose();
                    Console.WriteLine("saved: " + outPath);
                }
                catch (Exception e)
                {
                    Console.WriteLine("shot failed: " + e.Message);
                }
                form.Close();
            };

            Application.Run(form);
            return 0;
        }
    }
}

