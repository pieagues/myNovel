using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace MyNovel
{
    /// <summary>扩展入口：注册命令与工具窗口。</summary>
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [InstalledProductRegistration("mynovel 阅读器", "在 Visual Studio 中阅读 TXT 小说", "1.0.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ReaderToolWindow), Style = VsDockStyle.Tabbed,
        Window = "34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3", Width = 900, Height = 420)]
    [Guid(PackageGuidString)]
    public sealed class MyNovelPackage : Package
    {
        public const string PackageGuidString = "9f3c1e2a-5b7d-4a61-9c2e-8d4f1b6a7c31";

        /// <summary>命令集 GUID，必须与 MyNovelPackage.vsct 中的 guidMyNovelCmdSet 一致。</summary>
        public static readonly Guid CmdSetGuid = new Guid("4c2a8e71-3d95-4b6f-a1c8-7e5b2d9f0a13");

        private const int CmdOpenNovel = 0x0100;
        private const int CmdToggleWindow = 0x0101;
        private const int CmdReadThisFile = 0x0102;

        protected override void Initialize()
        {
            base.Initialize();

            var mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (mcs == null) return;

            mcs.AddCommand(new MenuCommand(OnOpenNovel, new CommandID(CmdSetGuid, CmdOpenNovel)));
            mcs.AddCommand(new MenuCommand(OnToggleWindow, new CommandID(CmdSetGuid, CmdToggleWindow)));
            mcs.AddCommand(new MenuCommand(OnReadThisFile, new CommandID(CmdSetGuid, CmdReadThisFile)));
        }

        /// <summary>显示阅读窗口，并把焦点交给阅读区域。</summary>
        private ReaderControl ShowReader()
        {
            var pane = FindToolWindow(typeof(ReaderToolWindow), 0, true) as ReaderToolWindow;
            if (pane == null || pane.Frame == null) return null;

            var frame = pane.Frame as IVsWindowFrame;
            if (frame != null) frame.Show();

            pane.Reader.FocusInput();
            return pane.Reader;
        }

        private void OnOpenNovel(object sender, EventArgs e)
        {
            var reader = ShowReader();
            if (reader != null) reader.ShowOpenDialog();
        }

        private void OnToggleWindow(object sender, EventArgs e)
        {
            ReaderToolWindow pane = null;
            try
            {
                pane = FindToolWindow(typeof(ReaderToolWindow), 0, false) as ReaderToolWindow;
            }
            catch
            {
                pane = null;
            }

            if (pane == null || pane.Frame == null)
            {
                ShowReader();
                return;
            }

            var frame = pane.Frame as IVsWindowFrame;
            if (frame == null) return;

            int visible = frame.IsVisible();
            if (visible != 0) frame.Hide();
            else frame.Show();
        }

        private void OnReadThisFile(object sender, EventArgs e)
        {
            string path = null;
            try
            {
                var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte != null)
                {
                    if (dte.SelectedItems != null && dte.SelectedItems.Count > 0)
                    {
                        try { path = dte.SelectedItems.Item(1).ProjectItem.FileNames[1]; }
                        catch { path = null; }
                    }
                    if (string.IsNullOrEmpty(path) && dte.ActiveDocument != null)
                        path = dte.ActiveDocument.FullName;
                }
            }
            catch
            {
                path = null;
            }

            if (string.IsNullOrEmpty(path) || !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show("请先在解决方案资源管理器或编辑器中选中一个 .txt 文件。",
                    "mynovel", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var reader = ShowReader();
            if (reader != null) reader.OpenFile(path);
        }
    }
}
