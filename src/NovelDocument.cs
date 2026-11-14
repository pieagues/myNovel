using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MyNovel
{
    /// <summary>章节：标题 + 在正文中的起始字符位置。</summary>
    internal sealed class Chapter
    {
        public string Title;
        public int Offset;
    }

    /// <summary>一本已经读入内存的小说：负责编码识别与章节切分。</summary>
    internal sealed class NovelDocument
    {
        /// <summary>常见中英文章节标题，整行匹配，避免误切正文。</summary>
        private static readonly Regex ChapterRegex = new Regex(
            @"^[ \t\u3000]*(?:第[0-9０-９一二三四五六七八九十百千万零〇两]{1,10}[章节回卷部篇集][^\r\n]{0,40}" +
            @"|(?:序章|序言|楔子|引子|终章|尾声|后记|番外)[^\r\n]{0,40}" +
            @"|[Cc][Hh][Aa][Pp][Tt][Ee][Rr]\s+\d+[^\r\n]{0,40})[ \t\u3000]*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public string FilePath { get; private set; }
        public string Title { get; private set; }
        public string Text { get; private set; }
        public IList<Chapter> Chapters { get; private set; }

        private NovelDocument() { }

        public static NovelDocument Load(string path)
        {
            string text = ReadTextSmart(path);
            // 统一换行，排版层只处理 \n
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            var doc = new NovelDocument
            {
                FilePath = path,
                Title = Path.GetFileNameWithoutExtension(path),
                Text = text,
                Chapters = ParseChapters(text)
            };
            return doc;
        }

        /// <summary>当前偏移所在的章节序号（0 起）。</summary>
        public int ChapterIndexAt(int offset)
        {
            int idx = 0;
            for (int i = 0; i < Chapters.Count; i++)
            {
                if (Chapters[i].Offset <= offset) idx = i;
                else break;
            }
            return idx;
        }

        private static List<Chapter> ParseChapters(string text)
        {
            var list = new List<Chapter>();
            foreach (Match m in ChapterRegex.Matches(text))
            {
                string title = m.Value.Trim();
                if (title.Length == 0) continue;
                // 章节标题行最长 60 字，超长的多半是正文里以「第X章」开头的句子
                if (title.Length > 60) continue;

                int offset = m.Index;
                while (offset > 0 && (text[offset - 1] == '\n' || text[offset - 1] == ' ' || text[offset - 1] == '\t'))
                    offset--;
                if (offset < text.Length && text[offset] == '\n') offset++;

                if (list.Count > 0 && list[list.Count - 1].Offset >= offset) continue;
                list.Add(new Chapter { Title = title, Offset = offset });
            }

            if (list.Count == 0 || list[0].Offset > 200)
                list.Insert(0, new Chapter { Title = "开始", Offset = 0 });

            return list;
        }

        /// <summary>编码识别：BOM 优先，其次严格 UTF-8，最后按 GB18030 兜底。</summary>
        private static string ReadTextSmart(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return string.Empty;

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return new UTF32Encoding(false, true).GetString(bytes, 4, bytes.Length - 4);
            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return new UTF32Encoding(true, true).GetString(bytes, 4, bytes.Length - 4);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try { return Encoding.GetEncoding(54936).GetString(bytes); }   // GB18030
                catch { return Encoding.Default.GetString(bytes); }
            }
        }
    }
}
