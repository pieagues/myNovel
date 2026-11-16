using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;

namespace MyNovel
{
    /// <summary>
    /// 阅读界面：纯代码构建（无 XAML）。背景/前景取自 VS 主题色，字体与字号取自用户编辑器设置，
    /// 因此观感与 IDE 完全一致，不会突兀。
    /// </summary>
    internal sealed class ReaderControl : UserControl
    {
        private const int TocVisibleLines = 16;

        private readonly TextBlock _body;
        private readonly TextBlock _status;
        private readonly Border _tocPanel;
        private readonly TextBlock _tocTitle;
        private readonly StackPanel _tocList;
        private readonly ScrollViewer _tocScroll;
        private readonly TextBlock _tocHint;
        private readonly DispatcherTimer _saveTimer;

        private NovelDocument _doc;
        private int _pageStart;
        private int _pageEnd;

        /// <summary>按当前宽高切好的页起点缓存，用于上一页/下一页的确定性定位（不再依赖历史栈）。</summary>
        private readonly List<int> _pageStarts = new List<int>();
        private readonly List<int> _pageEnds = new List<int>();
        private const int MaxCachedPages = 20000;
        /// <summary>页缓存的起点（通常是所在章的开头），缓存只从这里向后推进。</summary>
        private int _cacheAnchor;

        private double _lineHeight = 20;
        private double _fontSizePx = 15;
        private FontFamily _fontFamily = new FontFamily("Consolas");
        private Brush _background = Brushes.Black;
        private Brush _foreground = Brushes.White;
        private Brush _dimBrush = Brushes.Gray;
        private Brush _tocSelectedBackground = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        private Brush _tocSelectedForeground = Brushes.White;
        private Brush _tocHoverBackground = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255));
        private Brush _tocBackground = Brushes.Black;

        private bool _tocVisible;
        private int _tocIndex;
        private int _tocTop;

        /// <summary>请求隐藏宿主工具窗口（由 ToolWindowPane 处理）。</summary>
        public event EventHandler HideRequested;

        public ReaderControl()
        {
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition());

            _body = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            // 透明底会让 WPF 退回灰度抗锯齿，文字发虚；显式要求 ClearType
            TextOptions.SetTextRenderingMode(_body, TextRenderingMode.ClearType);
            TextOptions.SetTextFormattingMode(_body, TextFormattingMode.Display);
            RenderOptions.SetClearTypeHint(_body, ClearTypeHint.Enabled);

            var bodyHost = new Border
            {
                Padding = new Thickness(16, 10, 16, 6),
                Child = _body
            };
            root.Children.Add(bodyHost);

            _status = new TextBlock
            {
                Margin = new Thickness(14, 0, 14, 6),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var statusHost = new Grid();
            statusHost.VerticalAlignment = VerticalAlignment.Bottom;
            statusHost.Children.Add(_status);
            root.Children.Add(statusHost);

            _tocTitle = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 2, 8),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            _tocList = new StackPanel { Orientation = Orientation.Vertical };

            // 章节行放在可滚动区域里：行数受限（窗口化），工具窗口很矮时仍可滚动查看
            _tocScroll = new ScrollViewer
            {
                Content = _tocList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };

            _tocHint = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(2, 8, 2, 0),
                TextWrapping = TextWrapping.NoWrap
            };

            var tocStack = new StackPanel { Orientation = Orientation.Vertical };
            tocStack.Children.Add(_tocTitle);
            tocStack.Children.Add(_tocScroll);
            tocStack.Children.Add(_tocHint);

            _tocPanel = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14, 10, 14, 10),
                MinWidth = 420,
                MaxWidth = 640,
                Visibility = Visibility.Collapsed,
                Child = tocStack
            };
            root.Children.Add(_tocPanel);

            Content = root;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveProgress(); };

            Loaded += OnLoaded;
            SizeChanged += (s, e) =>
            {
                RecalcLineHeight();
                RenderPage();
                if (_tocVisible) RenderToc();   // 尺寸变化后重新窗口化目录
            };
            PreviewMouseDown += (s, e) =>
            {
                FocusInput();
                if (_tocVisible || e.ChangedButton != MouseButton.Left) return;

                // 左半边翻上一页、右半边翻下一页：阅读器最直观的点击方式
                if (e.GetPosition(this).X < ActualWidth / 2) PrevPage();
                else NextPage();
                e.Handled = true;
            };
            Unloaded += (s, e) => { _saveTimer.Stop(); SaveProgress(); };
        }

        #region 初始化：主题与字体

        private bool _initialized;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme();
            ApplyEditorFont();

            if (!_initialized)
            {
                _initialized = true;
                VSColorTheme.ThemeChanged += OnThemeChanged;

                // 首次显示时自动回到上次读的那本书和位置
                try
                {
                    var state = ReadStateStore.Load();
                    if (!string.IsNullOrEmpty(state.LastBook) && File.Exists(state.LastBook))
                        OpenFile(state.LastBook);
                    else
                        ShowWelcome();
                }
                catch
                {
                    ShowWelcome();
                }
            }

            FocusInput();
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyTheme();
                RecalcLineHeight();
                RenderPage();
            }));
        }

        /// <summary>取 VS 主题的「工具窗口背景/前景」色，保证和 IDE 融为一体。</summary>
        private void ApplyTheme()
        {
            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var fg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);

            _background = new SolidColorBrush(Color.FromArgb(bg.A, bg.R, bg.G, bg.B));
            _background.Freeze();
            // 正文比 IDE 前景色明显压暗，读起来像次要文字，不抢视觉
            _foreground = new SolidColorBrush(Blend(fg, bg, 0.68));
            _foreground.Freeze();

            var dim = new SolidColorBrush(Color.FromArgb((byte)(fg.A * 0.35), fg.R, fg.G, fg.B));
            dim.Freeze();
            _dimBrush = dim;

            // 目录底板比工具窗口再深一点，避免整块面板"浮"起来
            _tocBackground = new SolidColorBrush(Blend(bg, System.Drawing.Color.Black, 0.85));
            _tocBackground.Freeze();

            Background = _background;
            _body.Foreground = _foreground;
            _body.Background = Brushes.Transparent;
            _status.Foreground = _dimBrush;
            _tocTitle.Foreground = _dimBrush;
            _tocHint.Foreground = _dimBrush;
            _tocPanel.Background = _tocBackground;
            _tocPanel.BorderBrush = _dimBrush;

            // 选中行只做极淡的提亮，不引入彩度高的主题色，整体对比度不超过正文
            var selBg = new SolidColorBrush(Color.FromArgb(28, fg.R, fg.G, fg.B));
            selBg.Freeze();
            _tocSelectedBackground = selBg;

            var hoverBg = new SolidColorBrush(Color.FromArgb(16, fg.R, fg.G, fg.B));
            hoverBg.Freeze();
            _tocHoverBackground = hoverBg;

            var selFg = new SolidColorBrush(Blend(fg, bg, 0.72));
            selFg.Freeze();
            _tocSelectedForeground = selFg;

            if (_tocVisible) RenderToc();
        }

        /// <summary>字体与字号完全跟随用户在「文本编辑器」里的设置。</summary>
        private void ApplyEditorFont()
        {
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null) return;

                var props = dte.Properties["FontsAndColors", "TextEditor"];
                string family = null;
                short size = 0;

                try { family = Convert.ToString(props.Item("FontFamily").Value); }
                catch { }
                try { size = Convert.ToInt16(props.Item("FontSize").Value); }
                catch { }

                if (!string.IsNullOrWhiteSpace(family))
                    _fontFamily = new FontFamily(family);
                if (size > 0)
                    _fontSizePx = size * 96.0 / 72.0;     // 磅 → 像素
            }
            catch
            {
                // 取不到就保持默认，不影响使用
            }

            _body.FontFamily = _fontFamily;
            _body.FontSize = _fontSizePx;
            RecalcLineHeight();
        }

        private void RecalcLineHeight()
        {
            try
            {
                var ft = new FormattedText("国Ag", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    _fontSizePx, _foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                // 行距收在 1.25 倍：既要行间不粘，也不能拉得太散显得松散显眼
                _lineHeight = Math.Ceiling(ft.Height * 1.25);
            }
            catch
            {
                _lineHeight = Math.Ceiling(_fontSizePx * 1.35);
            }

            _body.LineHeight = _lineHeight;
            InvalidatePageCache();      // 行高变了，旧的页边界不再成立
            UpdateTocMetrics();
        }

        #endregion

        #region 对外接口

        public void FocusInput()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Focus();
                Keyboard.Focus(this);
            }), DispatcherPriority.Input);
        }

        public void OpenFile(string path)
        {
            try
            {
                var doc = NovelDocument.Load(path);
                _doc = doc;
                InvalidatePageCache();
                _pageStart = 0;

                var state = ReadStateStore.Load();
                BookState book;
                if (state.Books.TryGetValue(path, out book) && book.Offset > 0 && book.Offset < doc.Text.Length)
                    _pageStart = book.Offset;

                // 以所在章为锚点，保证「上一页」能回到本章内的上一屏
                RebuildCacheFrom(doc.Chapters[doc.ChapterIndexAt(_pageStart)].Offset);

                HideToc();
                RecalcLineHeight();
                RenderPage();
                FocusInput();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开小说失败：" + ex.Message, "mynovel",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void ShowOpenDialog()
        {
            var dlg = new OpenFileDialog
            {
                Title = "打开小说",
                Filter = "小说文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
                OpenFile(dlg.FileName);
        }

        #endregion

        #region 排版与渲染

        private void ShowWelcome()
        {
            _doc = null;
            _body.Text = "mynovel\n\nCtrl+Alt+N  打开小说\nO           在此窗口打开\nG           目录\n滚轮        翻页\nCtrl+Alt+H  显示/隐藏窗口";
            UpdateStatus();
        }

        private void RenderPage()
        {
            if (_doc == null) return;

            double width = AvailableTextWidth();
            int linesPerPage = LinesPerPage();

            string text = _doc.Text;
            // 这里只做边界修正，不清零 _pageStart（归零只发生在加载文档与 Home 键）
            if (_pageStart < 0) _pageStart = 0;
            if (_pageStart >= text.Length) _pageStart = Math.Max(0, text.Length - 1);

            var lines = new List<string>(linesPerPage);
            int pos = _pageStart;

            while (pos < text.Length && lines.Count < linesPerPage)
            {
                int nl = text.IndexOf('\n', pos);
                int paraEnd = nl < 0 ? text.Length : nl;
                if (paraEnd > pos && text[paraEnd - 1] == '\r') paraEnd--;

                if (paraEnd == pos)
                {
                    lines.Add(string.Empty);
                    pos = nl < 0 ? text.Length : nl + 1;
                    continue;
                }

                int segStart = pos;
                while (segStart < paraEnd && lines.Count < linesPerPage)
                {
                    int fit = MaxCharsFitting(text, segStart, paraEnd, width);
                    int segEnd = Math.Min(paraEnd, segStart + Math.Max(1, fit));
                    lines.Add(text.Substring(segStart, segEnd - segStart).TrimEnd());
                    segStart = segEnd;
                }
                pos = segStart;
                if (pos < paraEnd) break;                       // 本页已满，停在这段中间
                pos = nl < 0 ? text.Length : nl + 1;            // 跳过换行
            }

            _pageEnd = pos;
            _body.Text = string.Join("\n", lines);
            FadeInBody();
            UpdateStatus();
        }

        /// <summary>翻页时做一次很轻的淡入，提示内容已换页，但不干扰阅读。</summary>
        private void FadeInBody()
        {
            var fade = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(110));
            _body.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>
        /// 正文可用宽度。必须取控件自身宽度而不是 TextBlock 的 ActualWidth，
        /// 否则会形成「上一页文字多宽、这一页就只能多宽」的收敛，导致整页挤成一条窄柱。
        /// </summary>
        private double AvailableTextWidth()
        {
            double w = ActualWidth;
            if (w <= 1) w = 900;
            // 32 = 左右内边距 16×2；再留 12px 余量吸收像素取整，避免行尾最后一个字被裁掉
            return Math.Max(200, w - 32 - 12);
        }

        /// <summary>正文可用高度，同样不能沿用 TextBlock 自身高度，否则每页行数会被上一页锁死。</summary>
        private double AvailableTextHeight()
        {
            double h = ActualHeight;
            if (h <= 1) h = 420;
            return Math.Max(80, h - 38);    // 减去上下内边距（10 + 6）与底部状态栏
        }

        /// <summary>按比例把前景色往背景色混合，得到柔和一些的文字色。</summary>
        private static Color Blend(System.Drawing.Color fg, System.Drawing.Color bg, double fgWeight)
        {
            double bgWeight = 1 - fgWeight;
            return Color.FromArgb(
                fg.A,
                (byte)Math.Round(fg.R * fgWeight + bg.R * bgWeight),
                (byte)Math.Round(fg.G * fgWeight + bg.G * bgWeight),
                (byte)Math.Round(fg.B * fgWeight + bg.B * bgWeight));
        }

        /// <summary>二分查找：从 start 起最多能放下多少个字符。</summary>
        private int MaxCharsFitting(string text, int start, int end, double maxWidth)
        {
            int lo = 1, hi = end - start, best = 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (MeasureWidth(text.Substring(start, mid)) <= maxWidth)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return best;
        }

        private double MeasureWidth(string s)
        {
            // 必须与正文渲染使用同一套字形模式（Display）。若这里用默认的 Ideal 模式测量，
            // 而正文按 Display 渲染，前者算出的宽度会略小于实际绘制宽度，最后一个字就会被右边缘切掉。
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                _fontSizePx, _foreground, null, TextFormattingMode.Display,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return ft.WidthIncludingTrailingWhitespace;
        }

        private void UpdateStatus()
        {
            if (_doc == null)
            {
                _status.Text = "Ctrl+Alt+N 打开小说      G 目录      滚轮 翻页      Ctrl+Alt+H 显示/隐藏";
                return;
            }

            int ci = _doc.ChapterIndexAt(_pageStart);
            string chapter = (_doc.Chapters.Count > ci) ? _doc.Chapters[ci].Title : string.Empty;
            double pct = _doc.Text.Length == 0 ? 0 : 100.0 * _pageStart / _doc.Text.Length;
            _status.Text = string.Format("《{0}》  {1}   {2:0.0}%", _doc.Title, chapter, pct);
        }

        #endregion

        #region 翻页与跳转

        private void NextPage()
        {
            if (_doc == null) return;

            int idx = PageIndexAt(_pageStart);
            if (idx < 0) { JumpTo(_pageStart); idx = PageIndexAt(_pageStart); }
            if (idx < 0) return;

            EnsurePageCacheAt(idx + 1);
            if (idx + 1 >= _pageStarts.Count) return;   // 已是最后一页

            _pageStart = _pageStarts[idx + 1];
            RenderPage();
            ScheduleSave();
        }

        private void PrevPage()
        {
            if (_doc == null) return;

            int idx = PageIndexAt(_pageStart);
            if (idx > 0)
            {
                _pageStart = _pageStarts[idx - 1];
                RenderPage();
                ScheduleSave();
                return;
            }

            // 已在缓存锚点（章首）：回退到上一章的最后一页，而不是整本回退
            int chapter = _doc.ChapterIndexAt(_pageStart);
            if (chapter <= 0) { _pageStart = 0; RenderPage(); ScheduleSave(); return; }

            JumpToChapterEnd(chapter - 1);
        }

        /// <summary>跳到指定章节的最后一页：只在本章范围内分页，绝不扫描到后续章节。</summary>
        private void JumpToChapterEnd(int chapter)
        {
            if (_doc == null || chapter < 0 || chapter >= _doc.Chapters.Count) return;

            int chapterStart = _doc.Chapters[chapter].Offset;
            int chapterEnd = chapter + 1 < _doc.Chapters.Count
                ? _doc.Chapters[chapter + 1].Offset
                : _doc.Text.Length;

            RebuildCacheFrom(chapterStart);
            // 让 PageIndexAt 算到"覆盖本章末尾的那一页"就停，不会把后续章节全部塞进缓存
            int idx = PageIndexAt(Math.Max(chapterStart, chapterEnd - 1));
            if (idx < 0) return;

            _pageStart = _pageStarts[idx];
            RenderPage();
            ScheduleSave();
        }

        private void JumpTo(int offset)
        {
            if (_doc == null) return;
            offset = Math.Max(0, Math.Min(offset, Math.Max(0, _doc.Text.Length - 1)));

            // 以所在章节为锚点重建缓存：跳转距离可能很远，从文档开头逐页推进代价太大
            int chapter = _doc.ChapterIndexAt(offset);
            RebuildCacheFrom(_doc.Chapters[chapter].Offset);

            int idx = PageIndexAt(offset);
            _pageStart = idx >= 0 ? _pageStarts[idx] : offset;
            RenderPage();

            // 兜底：渲染没能算出页尾（布局尺寸尚未就绪）时，按当前宽高强制算一遍
            if (_pageEnd <= _pageStart) _pageEnd = RecalcPageEnd(_pageStart);

            ScheduleSave();
        }

        /// <summary>以 offset 为缓存起点重新分页，之后只在此基础上向后推进。</summary>
        private void RebuildCacheFrom(int offset)
        {
            _pageStarts.Clear();
            _pageEnds.Clear();
            _cacheAnchor = Math.Max(0, offset);
        }

        /// <summary>
        /// 返回包含 start 的页序号；必要时在缓存里向后补齐。
        /// 缓存按文档顺序单调增长，任何一次跳转都不会破坏已算好的页边界。
        /// </summary>
        private int PageIndexAt(int start)
        {
            while (_doc != null && _pageStarts.Count < MaxCachedPages)
            {
                if (_pageEnds.Count == 0)
                {
                    int end = RecalcPageEnd(_cacheAnchor);
                    _pageStarts.Add(_cacheAnchor);
                    _pageEnds.Add(end);
                }
                else
                {
                    int lastEnd = _pageEnds[_pageEnds.Count - 1];
                    if (lastEnd > start || lastEnd >= _doc.Text.Length) break;
                    _pageStarts.Add(lastEnd);
                    _pageEnds.Add(RecalcPageEnd(lastEnd));
                }

                if (_pageEnds[_pageEnds.Count - 1] > start) break;
            }

            int lo = 0, hi = _pageStarts.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_pageStarts[mid] <= start) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        /// <summary>把页缓存补到至少 index 页；文档结尾或达到缓存上限即停止。</summary>
        private void EnsurePageCacheAt(int index)
        {
            if (_doc == null) return;
            // 防御性：任何调用方误传超大 index 时，上限钳死在 MaxCachedPages 内
            if (index >= MaxCachedPages) index = MaxCachedPages - 1;
            string text = _doc.Text;

            while (_pageStarts.Count <= index && _pageStarts.Count < MaxCachedPages)
            {
                int start = _pageEnds.Count == 0 ? _cacheAnchor : _pageEnds[_pageEnds.Count - 1];
                if (_pageEnds.Count > 0 && start >= text.Length) break;

                int end = RecalcPageEnd(start);
                _pageStarts.Add(start);
                _pageEnds.Add(end);
                if (end >= text.Length) break;
            }
        }

        /// <summary>宽高或字体变化后，旧的页边界作废，从所在章的开头重新起算。</summary>
        private void InvalidatePageCache()
        {
            _pageStarts.Clear();
            _pageEnds.Clear();
            _cacheAnchor = _doc != null && _doc.Chapters.Count > 0
                ? _doc.Chapters[_doc.ChapterIndexAt(Math.Min(_pageStart, _doc.Text.Length - 1))].Offset
                : _pageStart;
        }

        /// <summary>跳到最后一页：从尾部附近反复排版，取最后一页的起点。</summary>
        private void JumpToLastPage()
        {
            if (_doc == null) return;

            double width = AvailableTextWidth();
            int linesPerPage = LinesPerPage();

            int pos = Math.Max(0, _doc.Text.Length - 4000);
            int lastStart = pos;
            for (int guard = 0; guard < 400; guard++)
            {
                int end = LayoutForward(_doc.Text, pos, linesPerPage, width);
                if (end >= _doc.Text.Length) { lastStart = pos; break; }
                pos = end;
                lastStart = pos;
            }

            _pageStart = lastStart;
            RebuildCacheFrom(_doc.Chapters[_doc.ChapterIndexAt(lastStart)].Offset);
            RenderPage();
            ScheduleSave();
        }

        /// <summary>当前可用高度能放下多少行（至少 1 行）。</summary>
        private int LinesPerPage()
        {
            int lines = (int)Math.Floor(AvailableTextHeight() / Math.Max(1, _lineHeight));
            return Math.Max(1, lines);
        }

        /// <summary>按当前可用宽高强制算出从 start 起这一页的终点，返回值保证严格大于 start。</summary>
        private int RecalcPageEnd(int start)
        {
            if (_doc == null) return start;

            string text = _doc.Text;
            if (start >= text.Length) return text.Length;

            int end = LayoutForward(text, start, LinesPerPage(), AvailableTextWidth());
            if (end <= start)
            {
                // 极端情况（宽度异常等）下至少推进到下一条换行，避免翻页原地踏步
                int nl = text.IndexOf('\n', start);
                end = nl < 0 ? text.Length : nl + 1;
                if (end <= start) end = text.Length;
            }
            return end;
        }

        /// <summary>只算结束位置，不渲染。</summary>
        private int LayoutForward(string text, int start, int linesPerPage, double width)
        {
            var counter = new List<string>(linesPerPage);
            int pos = start;
            while (pos < text.Length && counter.Count < linesPerPage)
            {
                int nl = text.IndexOf('\n', pos);
                int paraEnd = nl < 0 ? text.Length : nl;
                if (paraEnd > pos && text[paraEnd - 1] == '\r') paraEnd--;

                if (paraEnd == pos)
                {
                    counter.Add(string.Empty);
                    pos = nl < 0 ? text.Length : nl + 1;
                    continue;
                }

                int segStart = pos;
                while (segStart < paraEnd && counter.Count < linesPerPage)
                {
                    int fit = MaxCharsFitting(text, segStart, paraEnd, width);
                    int segEnd = Math.Min(paraEnd, segStart + Math.Max(1, fit));
                    counter.Add("x");
                    segStart = segEnd;
                }
                pos = segStart;
                if (pos < paraEnd) break;
                pos = nl < 0 ? text.Length : nl + 1;
            }
            return pos;
        }

        #endregion

        #region 目录

        /// <summary>目录每一行的高度：跟随正文字号，留一点上下呼吸空间。</summary>
        private double TocRowHeight
        {
            get { return Math.Ceiling(_lineHeight) + 8; }
        }

        private void ToggleToc()
        {
            if (_doc == null) return;
            if (_tocVisible) { HideToc(); return; }

            _tocIndex = _doc.ChapterIndexAt(_pageStart);
            _tocTop = Math.Max(0, _tocIndex - TocVisibleRows() / 2);
            _tocVisible = true;
            _tocPanel.Visibility = Visibility.Visible;
            UpdateTocMetrics();
            RenderToc();

            // 打开时淡入，避免整块面板突然闪现
            _tocPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
        }

        private void HideToc()
        {
            _tocVisible = false;
            _tocPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>目录列表区可容纳的行数：不超过 TocVisibleLines，也不超出工具窗口高度。</summary>
        private int TocVisibleRows()
        {
            if (_doc == null) return 0;

            int rows = Math.Min(TocVisibleLines, _doc.Chapters.Count);
            int fit = (int)Math.Floor(TocAvailableListHeight() / Math.Max(1, TocRowHeight));
            if (fit < 1) fit = 1;
            return Math.Max(1, Math.Min(rows, fit));
        }

        /// <summary>列表区最大高度：给标题行与底部提示行留出空间。</summary>
        private double TocAvailableListHeight()
        {
            return Math.Max(TocRowHeight * 4, AvailableTextHeight() - 96);
        }

        /// <summary>字号或窗口尺寸变化后同步行高与列表区高度。</summary>
        private void UpdateTocMetrics()
        {
            if (_tocScroll == null) return;

            double rowHeight = TocRowHeight;
            foreach (var child in _tocList.Children)
            {
                var row = child as Border;
                if (row != null) row.Height = rowHeight;
            }

            _tocScroll.MaxHeight = Math.Min(rowHeight * TocVisibleLines, TocAvailableListHeight());
        }

        /// <summary>只重建可见范围内的章节行；选中行用主题色高亮，并在文字前加 ▸ 指示符。</summary>
        private void RenderToc()
        {
            if (!_tocVisible || _doc == null) return;

            int count = _doc.Chapters.Count;
            if (count == 0) { HideToc(); return; }

            int visible = TocVisibleRows();

            if (_tocIndex < 0) _tocIndex = 0;
            if (_tocIndex >= count) _tocIndex = count - 1;
            if (_tocTop < 0) _tocTop = 0;
            if (_tocTop > count - visible) _tocTop = Math.Max(0, count - visible);
            // 选中项移出窗口时，让窗口跟着滚动
            if (_tocIndex < _tocTop) _tocTop = _tocIndex;
            if (_tocIndex >= _tocTop + visible) _tocTop = _tocIndex - visible + 1;

            _tocTitle.Text = string.Format("目录 · 共 {0} 章", count);
            _tocHint.Text = "↑↓ 选择   Enter 跳转   G/Esc 关闭";

            _tocList.Children.Clear();
            int end = Math.Min(count, _tocTop + visible);
            for (int i = _tocTop; i < end; i++)
                _tocList.Children.Add(CreateTocRow(i, i == _tocIndex));

            _tocScroll.ScrollToTop();
        }

        /// <summary>一个章节行：高亮背景 + 序号 + 章节名。</summary>
        private Border CreateTocRow(int index, bool selected)
        {
            var ch = _doc.Chapters[index];
            string prefix = selected ? "▸ " : "   ";

            var text = new TextBlock
            {
                Text = prefix + (index + 1) + ". " + ch.Title,
                FontFamily = _fontFamily,
                FontSize = _fontSizePx,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = selected ? _tocSelectedForeground : _foreground
            };

            var row = new Border
            {
                Height = TocRowHeight,
                Padding = new Thickness(6, 0, 6, 0),
                CornerRadius = new CornerRadius(3),
                Background = selected ? _tocSelectedBackground : Brushes.Transparent,
                Child = text,
                Cursor = Cursors.Hand
            };

            if (!selected)
            {
                // 悬停反馈：极淡的提亮，指针移开即恢复，避免干扰
                row.MouseEnter += (s, e) => row.Background = _tocHoverBackground;
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            }

            // 单击直接跳转，比"选中后再按 Enter"更直观
            int chapterIndex = index;
            row.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                _tocIndex = chapterIndex;
                CommitToc();
            };

            return row;
        }

        private void MoveToc(int delta)
        {
            if (_doc == null || _doc.Chapters.Count == 0) return;
            _tocIndex = Math.Max(0, Math.Min(_doc.Chapters.Count - 1, _tocIndex + delta));
            RenderToc();
        }

        private void CommitToc()
        {
            if (_doc == null || _doc.Chapters.Count == 0) { HideToc(); return; }
            int offset = _doc.Chapters[_tocIndex].Offset;
            HideToc();
            JumpTo(offset);
        }

        #endregion

        #region 输入

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_tocVisible)
            {
                switch (e.Key)
                {
                    case Key.Up: MoveToc(-1); e.Handled = true; return;
                    case Key.Down: MoveToc(1); e.Handled = true; return;
                    case Key.PageUp: MoveToc(-10); e.Handled = true; return;
                    case Key.PageDown: MoveToc(10); e.Handled = true; return;
                    case Key.Enter: CommitToc(); e.Handled = true; return;
                    case Key.Escape:
                    case Key.G:
                        HideToc();
                        e.Handled = true;
                        return;
                }
            }

            switch (e.Key)
            {
                case Key.Right:
                case Key.Down:
                case Key.Space:
                case Key.PageDown:
                    NextPage();
                    e.Handled = true;
                    break;
                case Key.Left:
                case Key.Up:
                case Key.PageUp:
                    PrevPage();
                    e.Handled = true;
                    break;
                case Key.Home:
                    JumpTo(0);
                    e.Handled = true;
                    break;
                case Key.End:
                    JumpToLastPage();
                    e.Handled = true;
                    break;
                case Key.G:
                    ToggleToc();
                    e.Handled = true;
                    break;
                case Key.O:
                    ShowOpenDialog();
                    e.Handled = true;
                    break;
                case Key.H:
                case Key.Escape:
                    RequestHide();
                    e.Handled = true;
                    break;
            }

            base.OnPreviewKeyDown(e);
        }

        /// <summary>用隧道事件接管滚轮，避免目录里的 ScrollViewer 先把滚轮吃掉。</summary>
        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            if (_tocVisible)
            {
                MoveToc(e.Delta > 0 ? -3 : 3);
            }
            else if (e.Delta > 0)
            {
                PrevPage();
            }
            else if (e.Delta < 0)
            {
                NextPage();
            }
            e.Handled = true;
            base.OnPreviewMouseWheel(e);
        }

        private void RequestHide()
        {
            var handler = HideRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        #endregion

        #region 进度保存

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveProgress()
        {
            if (_doc == null) return;

            var state = ReadStateStore.Load();
            state.LastBook = _doc.FilePath;
            state.Books[_doc.FilePath] = new BookState
            {
                Offset = _pageStart,
                Total = _doc.Text.Length,
                Time = DateTime.Now.ToString("s")
            };
            ReadStateStore.Save(state);
        }

        #endregion
    }
}
