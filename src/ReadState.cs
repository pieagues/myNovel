using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace MyNovel
{
    /// <summary>单本书的阅读状态。</summary>
    [DataContract]
    internal sealed class BookState
    {
        [DataMember(Name = "offset")] public int Offset { get; set; }
        [DataMember(Name = "total")] public int Total { get; set; }
        [DataMember(Name = "time")] public string Time { get; set; }
    }

    /// <summary>书库状态：上次读的书 + 每本书的进度。</summary>
    [DataContract]
    internal sealed class LibraryState
    {
        [DataMember(Name = "last")] public string LastBook { get; set; }
        [DataMember(Name = "books")] public Dictionary<string, BookState> Books { get; set; }

        public LibraryState()
        {
            Books = new Dictionary<string, BookState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>把阅读进度存到 %APPDATA%\MyNovel\library.json，实现下次打开回到原位。</summary>
    internal static class ReadStateStore
    {
        private static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyNovel");

        private static string FilePath { get { return Path.Combine(Folder, "library.json"); } }

        public static LibraryState Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new LibraryState();
                using (var fs = File.OpenRead(FilePath))
                {
                    var ser = new DataContractJsonSerializer(typeof(LibraryState));
                    var state = ser.ReadObject(fs) as LibraryState;
                    return state ?? new LibraryState();
                }
            }
            catch
            {
                return new LibraryState();
            }
        }

        public static void Save(LibraryState state)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                using (var fs = File.Create(FilePath))
                {
                    var ser = new DataContractJsonSerializer(typeof(LibraryState));
                    ser.WriteObject(fs, state);
                }
            }
            catch
            {
                // 进度保存失败不影响阅读，静默忽略
            }
        }
    }
}
