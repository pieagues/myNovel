using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace MyNovel
{
    /// <summary>承载阅读界面的停靠工具窗口（默认与「输出」窗口同区域）。</summary>
    [Guid("2b7e5c48-9a1d-4f32-8c6b-1d3a5e7f9b24")]
    internal sealed class ReaderToolWindow : ToolWindowPane
    {
        private readonly ReaderControl _control;

        public ReaderToolWindow() : base(null)
        {
            Caption = "mynovel 阅读器";
            _control = new ReaderControl();
            _control.HideRequested += OnHideRequested;
            Content = _control;
        }

        public ReaderControl Reader { get { return _control; } }

        private void OnHideRequested(object sender, EventArgs e)
        {
            try
            {
                var frame = Frame as IVsWindowFrame;
                if (frame != null) frame.Hide();
            }
            catch
            {
                // 隐藏失败不致命
            }
        }
    }
}
