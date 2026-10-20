using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[DataContract] public class FileEntry {
    [DataMember] public string Path;
    [DataMember] public string Name;
    [DataMember] public bool Folder;
    [DataMember] public long Size;
    [DataMember] public DateTime Modified;
    string cachedParent, cachedSearchName, cachedExtension;
    public string Parent { get { return cachedParent ?? (cachedParent = System.IO.Path.GetDirectoryName(Path)); } }
    public string SearchName { get { return cachedSearchName ?? (cachedSearchName = Name.ToUpperInvariant()); } }
    public string Extension { get { return cachedExtension ?? (cachedExtension = System.IO.Path.GetExtension(Name)); } }
}
[DataContract] public class SavedIndex {
    [DataMember] public string[] Roots = new string[0];
    [DataMember] public FileEntry[] Entries = new FileEntry[0];
}
public class FileIndex {
    public readonly Dictionary<string, FileEntry> Items = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
    public int Skipped;
    public static bool Under(string path, string root) {
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
    public void RemoveTree(string path) {
        foreach (string key in Items.Keys.Where(p => Under(p, path)).ToArray()) Items.Remove(key);
    }
    public void AddPath(string path, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        try {
            FileAttributes attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) != 0) return;
            bool folder = (attr & FileAttributes.Directory) != 0;
            FileEntry entry = new FileEntry { Path = path, Name = System.IO.Path.GetFileName(path.TrimEnd('\\')), Folder = folder,
                Size = folder ? 0 : new FileInfo(path).Length, Modified = File.GetLastWriteTime(path) };
            Items[path] = entry;
            if (!folder) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) AddPath(child, token);
        } catch (UnauthorizedAccessException) { Skipped++; }
          catch (IOException) { Skipped++; }
          catch (System.Security.SecurityException) { Skipped++; }
    }
    public static FileIndex Scan(IEnumerable<string> roots, CancellationToken token) {
        FileIndex index = new FileIndex();
        foreach (string root in roots) index.AddPath(root, token);
        return index;
    }
    public void Apply(string path, CancellationToken token) {
        RemoveTree(path);
        if (File.Exists(path) || Directory.Exists(path)) AddPath(path, token);
    }
    public static void Save(string file, SavedIndex data) {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
        string temp = file + ".tmp";
        using (FileStream stream = File.Create(temp)) new DataContractJsonSerializer(typeof(SavedIndex)).WriteObject(stream, data);
        if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
    }
    public static SavedIndex Load(string file) {
        if (!File.Exists(file)) return new SavedIndex();
        using (FileStream stream = File.OpenRead(file)) return (SavedIndex)new DataContractJsonSerializer(typeof(SavedIndex)).ReadObject(stream);
    }
}
public class SearchSnapshot {
    public readonly FileEntry[] Entries;
    readonly Dictionary<int, FileEntry[]> sorted = new Dictionary<int, FileEntry[]>();
    public SearchSnapshot(FileEntry[] entries) { Entries = entries; }
    public FileEntry[] Sorted(int column) {
        lock (sorted) {
            FileEntry[] cached;
            if (sorted.TryGetValue(column, out cached)) return cached;
            cached = (FileEntry[])Entries.Clone();
            Array.Sort(cached, delegate(FileEntry a, FileEntry b) {
                int order = column == 1 ? StringComparer.OrdinalIgnoreCase.Compare(a.Parent, b.Parent) : column == 2 ? a.Size.CompareTo(b.Size) : column == 3 ? a.Modified.CompareTo(b.Modified) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
            });
            sorted[column] = cached; return cached;
        }
    }
}
public class SearchQuery {
    private readonly List<List<Func<FileEntry, bool>>> groups = new List<List<Func<FileEntry, bool>>>();
    bool hasTerms;
    private static bool Has(string value, string term) { return value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0; }
    public static bool FuzzyMatches(string name, string term) {
        return new FuzzyPattern(term).Matches(name.ToUpperInvariant());
    }
    sealed class FuzzyPattern {
        readonly string word;
        readonly int tolerance;
        readonly bool allowEdits;
        int[] bufferA = new int[0], bufferB = new int[0], bufferC = new int[0];
        public FuzzyPattern(string term) { word = term.ToUpperInvariant(); tolerance = word.Length >= 6 ? 2 : 1; allowEdits = word.Length >= 3 && word.Length <= 64 && !word.All(char.IsDigit); }
        public bool Matches(string text) {
            if (text.IndexOf(word, StringComparison.Ordinal) >= 0) return true;
            if (word.Length < 2) return false;
            int position = 0, shared = 0;
            foreach (char ch in text) {
                if (ch == word[position] && ++position == word.Length) return true;
                if (word.IndexOf(ch) >= 0) shared++;
            }
            if (!allowEdits || text.Length < word.Length - tolerance || shared < word.Length - tolerance) return false;
            if (bufferA.Length < text.Length + 1) { bufferA = new int[text.Length + 1]; bufferB = new int[text.Length + 1]; bufferC = new int[text.Length + 1]; }
            int[] previous = bufferA, current = bufferB, spare = bufferC, beforePrevious = null;
            Array.Clear(previous, 0, text.Length + 1);
            for (int i = 1; i <= word.Length; i++) {
                current[0] = i; int minimum = i;
                for (int j = 1; j <= text.Length; j++) {
                    int distance = Math.Min(previous[j] + 1, Math.Min(current[j - 1] + 1, previous[j - 1] + (word[i - 1] == text[j - 1] ? 0 : 1)));
                    if (beforePrevious != null && j > 1 && word[i - 1] == text[j - 2] && word[i - 2] == text[j - 1]) distance = Math.Min(distance, beforePrevious[j - 2] + 1);
                    current[j] = distance; if (distance < minimum) minimum = distance;
                }
                if (minimum > tolerance + 1) return false;
                int[] old = beforePrevious; beforePrevious = previous; previous = current; current = old ?? spare;
            }
            for (int j = 0; j <= text.Length; j++) if (previous[j] <= tolerance) return true;
            return false;
        }
    }
    static IEnumerable<string> Tokens(string query) {
        System.Text.StringBuilder token = new System.Text.StringBuilder(); bool quoted = false;
        foreach (char ch in query) {
            if (ch == '"') { quoted = !quoted; continue; }
            if (!quoted && (char.IsWhiteSpace(ch) || ch == '|')) {
                if (token.Length > 0) { yield return token.ToString(); token.Clear(); }
                if (ch == '|') yield return "|";
            } else token.Append(ch);
        }
        if (token.Length > 0) yield return token.ToString();
    }
    public SearchQuery(string query, bool matchPath, bool fuzzy = false) {
        List<Func<FileEntry, bool>> current = new List<Func<FileEntry, bool>>(); groups.Add(current);
        foreach (string raw in Tokens(query)) {
            string term = raw;
            if (term == "|") { current = new List<Func<FileEntry, bool>>(); groups.Add(current); continue; }
            bool exclude = term.StartsWith("!"); if (exclude) term = term.Substring(1);
            term = term.Trim('"'); if (term.Length == 0) continue;
            Func<FileEntry, bool> predicate;
            if (term.StartsWith("ext:", StringComparison.OrdinalIgnoreCase)) {
                HashSet<string> extensions = new HashSet<string>(term.Substring(4).Split(';').Select(t => "." + t.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
                predicate = e => !e.Folder && extensions.Contains(e.Extension);
            } else if (term.StartsWith("path:", StringComparison.OrdinalIgnoreCase)) {
                string value = term.Substring(5); predicate = e => Has(e.Path, value);
            } else if (term.Equals("type:folder", StringComparison.OrdinalIgnoreCase)) predicate = e => e.Folder;
            else if (term.Equals("type:file", StringComparison.OrdinalIgnoreCase)) predicate = e => !e.Folder;
            else if (term.Contains("*") || term.Contains("?")) {
                Regex glob = new Regex("^" + Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                predicate = e => glob.IsMatch(matchPath ? e.Path : e.Name);
            } else { string value = term; bool useFuzzy = fuzzy && !exclude && !value.Contains("\\") && !value.Contains("/");
                FuzzyPattern pattern = useFuzzy ? new FuzzyPattern(value) : null;
                predicate = e => Has(matchPath || value.Contains("\\") ? e.Path : e.Name, value) || (useFuzzy && pattern.Matches(e.SearchName)); }
            if (exclude) { Func<FileEntry, bool> positive = predicate; predicate = e => !positive(e); }
            current.Add(predicate);
            hasTerms = true;
        }
    }
    public bool Matches(FileEntry entry) {
        if (!hasTerms) return true;
        foreach (List<Func<FileEntry, bool>> group in groups) {
            if (group.Count == 0) continue; bool match = true;
            foreach (Func<FileEntry, bool> predicate in group) if (!predicate(entry)) { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}
public static class SearchEngine {
    static readonly HashSet<string>[] categories = new[] {
        new HashSet<string>(".txt .md .pdf .doc .docx .xls .xlsx .ppt .pptx .csv .rtf".Split(' '), StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(".png .jpg .jpeg .gif .webp .bmp .svg .ico .tiff".Split(' '), StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(".mp3 .wav .flac .aac .ogg .mp4 .mkv .avi .mov .webm".Split(' '), StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(".zip .7z .rar .gz .tar .bz2".Split(' '), StringComparer.OrdinalIgnoreCase)
    };
    static bool CategoryMatches(FileEntry e, int category) {
        if (category == 0) return true; if (category == 1) return !e.Folder; if (category == 2) return e.Folder;
        return !e.Folder && categories[category - 3].Contains(e.Extension);
    }
    public static FileEntry[] Execute(SearchSnapshot snapshot, string query, bool matchPath, bool fuzzy, int category, int column, bool descending, Func<bool> cancelled) {
        FileEntry[] ordered = snapshot.Sorted(column);
        bool rank = fuzzy && column == 0 && !string.IsNullOrWhiteSpace(query);
        if (!rank && category == 0 && string.IsNullOrWhiteSpace(query)) {
            if (cancelled()) throw new OperationCanceledException();
            if (!descending) return ordered;
            FileEntry[] reverse = (FileEntry[])ordered.Clone(); Array.Reverse(reverse); return reverse;
        }
        SearchQuery exact = new SearchQuery(query, matchPath), approximate = fuzzy ? new SearchQuery(query, matchPath, true) : null;
        List<FileEntry> direct = new List<FileEntry>(), suggestions = rank ? new List<FileEntry>() : direct;
        for (int position = 0; position < ordered.Length; position++) {
            if ((position & 255) == 0 && cancelled()) throw new OperationCanceledException();
            FileEntry entry = ordered[descending ? ordered.Length - position - 1 : position];
            if (!CategoryMatches(entry, category)) continue;
            if (exact.Matches(entry)) direct.Add(entry);
            else if (fuzzy && approximate.Matches(entry)) suggestions.Add(entry);
        }
        if (rank) direct.AddRange(suggestions);
        if (cancelled()) throw new OperationCanceledException();
        return direct.ToArray();
    }
}
public static class FileActions {
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
    public static FileEntry[] PlanSelection(IEnumerable<FileEntry> selected) {
        FileEntry[] items = selected.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        if (items.Length == 0) throw new ArgumentException("请先在搜索结果中选中文件。");
        if (items.Any(e => e.Folder)) throw new ArgumentException("当前仅支持删除文件。请取消选中的文件夹后重试。");
        return items;
    }
    public static void RecycleFile(string path, IntPtr owner) {
        if (Environment.OSVersion.Version < new Version(6, 2)) throw new NotSupportedException("回收站删除功能需要 Windows 8 或更新版本。");
        if (!System.IO.Path.IsPathRooted(path)) throw new ArgumentException("文件路径必须是绝对路径。");
        string fullPath = System.IO.Path.GetFullPath(path);
        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("不支持删除文件夹或链接，请在资源管理器中操作。");
        IFileOperation operation = null; IShellItem item = null;
        try {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
            // RECYCLEONDELETE + ALLOWUNDO + NO_CONNECTED_ELEMENTS; never invoke a permanent-delete fallback.
            // NOERRORUI + EARLYFAILURE return failures to the app instead of opening secondary dialogs.
            operation.SetOperationFlags(0x00080000 | 0x40 | 0x2000 | 0x10 | 0x400 | 0x00100000 | 0x4);
            operation.SetOwnerWindow(owner);
            Guid iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref iid, out item);
            operation.DeleteItem(item, IntPtr.Zero); operation.PerformOperations();
            bool aborted; operation.GetAnyOperationsAborted(out aborted);
            if (aborted || File.Exists(fullPath)) throw new IOException("文件未移至回收站，可能被占用、权限不足或此位置不支持回收站。");
        } finally { if (item != null) Marshal.FinalReleaseComObject(item); if (operation != null) Marshal.FinalReleaseComObject(operation); }
    }
}
public class MainWindow : Form {
    readonly TextBox search = new TextBox();
    readonly ComboBox category = new ComboBox();
    readonly CheckBox matchPath = new CheckBox();
    readonly CheckBox fuzzySearch = new CheckBox();
    readonly QuietButton confirmSearch = new QuietButton();
    Button deleteFiles;
    bool deleting;
    readonly ListView results = new ResultList();
    readonly List<QuietButton> navigation = new List<QuietButton>();
    readonly ToolTip tips = new ToolTip();
    readonly ContextMenuStrip moreMenu = new ContextMenuStrip();
    readonly ContextMenuStrip resultMenu = new ContextMenuStrip();
    readonly Label emptyMessage = new Label();
    readonly Label status = new Label();
    readonly Label rootsLabel = new Label();
    readonly System.Windows.Forms.Timer searchTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer changeTimer = new System.Windows.Forms.Timer();
    readonly ConcurrentQueue<string> changes = new ConcurrentQueue<string>();
    readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    readonly CancellationTokenSource life = new CancellationTokenSource();
    readonly string dataFile;
    readonly bool preview;
    List<string> roots = new List<string>();
    FileIndex index = new FileIndex();
    FileEntry[] entries = new FileEntry[0], visible = new FileEntry[0];
    SearchSnapshot searchSnapshot = new SearchSnapshot(new FileEntry[0]);
    bool working;
    volatile bool fullRefresh;
    volatile int queryVersion;
    int sortColumn;
    bool descending;
    int skipped;

    public MainWindow(bool isPreview, string testDataFile = null) {
        preview = isPreview;
        dataFile = testDataFile ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FindLite", "index.json");
        Text = "FindLite · 寻文件"; ClientSize = new Size(1180, 780); MinimumSize = new Size(1000, 640); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 9); BackColor = Color.White;
        using (Stream iconStream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("FindLite.Icon")) { if(iconStream != null) Icon = new Icon(iconStream); }
        BuildUI();
        searchTimer.Interval = 60; searchTimer.Tick += delegate { searchTimer.Stop(); Search(); };
        changeTimer.Interval = 650; changeTimer.Tick += delegate { ProcessChanges(); }; changeTimer.Start();
        FormClosing += delegate { life.Cancel(); foreach (FileSystemWatcher w in watchers) w.Dispose(); searchTimer.Stop(); changeTimer.Stop(); };
        Shown += delegate {
            if (preview) return;
            working = true; status.Text = "正在读取本地索引…";
            Task.Run(delegate {
                try { SavedIndex saved = FileIndex.Load(dataFile); List<string> loadedRoots = (saved.Roots ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); FileIndex loaded = new FileIndex();
                    foreach(FileEntry entry in saved.Entries ?? new FileEntry[0]) if(entry != null && entry.Path != null && loadedRoots.Any(r => FileIndex.Under(entry.Path,r))) loaded.Items[entry.Path] = entry;
                    SearchSnapshot snapshot = new SearchSnapshot(loaded.Items.Values.ToArray()); snapshot.Sorted(0);
                    OnUI(delegate { index=loaded; roots=loadedRoots; entries=snapshot.Entries; searchSnapshot=snapshot; working=false; UpdateRoots(); Search(); Watch(); if(roots.Count>0) Rebuild(); });
                } catch(Exception ex) { OnUI(delegate { working=false; UpdateEmptyState(0); status.Text="无法读取缓存，可重新添加文件夹："+ex.Message; }); }
            });
            search.Focus();
        };
    }
    Button Button(string text, EventHandler click, bool primary) {
        QuietButton button = new QuietButton { Text = text, AutoSize = true, Primary = primary };
        button.Click += click; return button;
    }
    void BuildUI() {
        TableLayoutPanel frame=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty };
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute,82)); frame.RowStyles.Add(new RowStyle(SizeType.Percent,100)); Controls.Add(frame);
        Panel header=new Panel { Dock=DockStyle.Fill,BackColor=Color.White,Margin=Padding.Empty };
        header.Paint+=delegate(object sender,PaintEventArgs e) { using(Pen pen=new Pen(Theme.Line)) e.Graphics.DrawLine(pen,0,header.Height-1,header.Width,header.Height-1); };
        header.Controls.Add(new BrandMark { Location=new Point(24,22),Size=new Size(38,38) });
        header.Controls.Add(new Label { Text="FindLite",Font=new Font("Segoe UI",18,FontStyle.Bold),ForeColor=Theme.Ink,AutoSize=true,Location=new Point(74,12) });
        header.Controls.Add(new Label { Text="寻文件 · 让查找更轻松",Font=new Font("Microsoft YaHei UI",8),ForeColor=Theme.Muted,AutoSize=true,Location=new Point(77,49) });
        Label version=new Label { Text="本地运行  ·  v1.0.1",AutoSize=false,TextAlign=ContentAlignment.MiddleRight,ForeColor=Theme.Muted,Anchor=AnchorStyles.Top|AnchorStyles.Right,Location=new Point(header.Width-210,27),Size=new Size(180,28) };
        header.Controls.Add(version); header.Resize+=delegate { version.Left=header.Width-210; }; frame.Controls.Add(header,0,0);
        TableLayoutPanel body=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty,BackColor=Color.FromArgb(247,249,247) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,196)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); frame.Controls.Add(body,0,1);
        TableLayoutPanel sidebar=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(16,22,16,18),Margin=Padding.Empty,BackColor=Color.FromArgb(247,249,247) };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,296)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent,100)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,140)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,32)); body.Controls.Add(sidebar,0,0);
        sidebar.Controls.Add(new Label { Text="文件类型",Dock=DockStyle.Fill,ForeColor=Theme.Muted,Font=new Font("Microsoft YaHei UI",8),Padding=new Padding(12,0,0,0) },0,0);
        FlowLayoutPanel nav=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=Padding.Empty,BackColor=sidebar.BackColor };
        string[] names={"全部内容","仅文件","文件夹","文档","图片","音频与视频","压缩文件"};
        category.Items.AddRange(new object[]{"全部","文件","文件夹","文档","图片","音频 / 视频","压缩包"}); category.SelectedIndex=0;
        for(int i=0;i<names.Length;i++) { int selected=i; QuietButton button=new QuietButton { Text=names[i],Navigation=true,Active=i==0,Width=164,Height=38,Margin=new Padding(0,0,0,4) };
            button.Click+=delegate { category.SelectedIndex=selected; }; navigation.Add(button); nav.Controls.Add(button); }
        category.SelectedIndexChanged+=delegate { for(int i=0;i<navigation.Count;i++) { navigation[i].Active=i==category.SelectedIndex; navigation[i].Invalidate(); } ScheduleSearch(); };
        sidebar.Controls.Add(nav,0,1);
        FlowLayoutPanel scopeActions=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=Padding.Empty,BackColor=sidebar.BackColor };
        Button add=Button("＋ 添加文件夹",delegate { AddRoot(); },true); add.AutoSize=false; add.Width=164; add.Margin=new Padding(0,0,0,8); scopeActions.Controls.Add(add);
        Button manage=Button("管理索引范围",delegate { ManageRoots(); },false); manage.AutoSize=false; manage.Width=164; manage.Margin=new Padding(0,0,0,8); scopeActions.Controls.Add(manage);
        Button refresh=Button("刷新索引",delegate { Rebuild(); },false); refresh.AutoSize=false; refresh.Width=164; scopeActions.Controls.Add(refresh); sidebar.Controls.Add(scopeActions,0,3);
        sidebar.Controls.Add(new Label { Text="文件信息只保存在本机",Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomCenter,ForeColor=Theme.Muted,Font=new Font("Microsoft YaHei UI",7.8f) },0,4);
        TableLayoutPanel main=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(28,22,28,16),Margin=Padding.Empty,BackColor=Color.White };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute,64)); main.RowStyles.Add(new RowStyle(SizeType.Absolute,64)); main.RowStyles.Add(new RowStyle(SizeType.Absolute,24)); main.RowStyles.Add(new RowStyle(SizeType.Absolute,50)); main.RowStyles.Add(new RowStyle(SizeType.Absolute,30)); main.RowStyles.Add(new RowStyle(SizeType.Percent,100)); main.RowStyles.Add(new RowStyle(SizeType.Absolute,34)); body.Controls.Add(main,1,0);
        Panel title=new Panel { Dock=DockStyle.Fill,Margin=Padding.Empty };
        title.Controls.Add(new Label { Text="文件搜索",AutoSize=true,Font=new Font("Microsoft YaHei UI",18,FontStyle.Bold),ForeColor=Theme.Ink,Location=new Point(-3,0) });
        title.Controls.Add(new Label { Text="从一个关键词，找到所需。",AutoSize=true,Font=new Font("Microsoft YaHei UI",9),ForeColor=Theme.Muted,Location=new Point(0,43) });
        moreMenu.Items.Add("搜索语法",null,delegate { ShowSyntax(); });
        moreMenu.Items.Add("关于 FindLite",null,delegate { MessageBox.Show(this,"FindLite 1.0.1\n轻量本地文件搜索\n\n支持模糊搜索、文件类型筛选和回收站删除。\n文件名索引存储于本机，不上传文件或索引。","关于 FindLite",MessageBoxButtons.OK,MessageBoxIcon.Information); });
        // Closed fires before all WinForms menu/focus callbacks have finished.
        // Reuse form-owned menus and dispose only with the owning window.
        Button more=Button("更多  ···",delegate(object sender,EventArgs e) { if(!moreMenu.IsDisposed) moreMenu.Show((Control)sender,new Point(0,38)); },false);
        more.Width=88; more.Height=34; more.AutoSize=false; more.Anchor=AnchorStyles.Top|AnchorStyles.Right; title.Controls.Add(more); title.Resize+=delegate { more.Left=title.Width-more.Width; }; main.Controls.Add(title,0,0);
        TableLayoutPanel searchBox=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Padding=new Padding(14,11,12,10),Margin=new Padding(0,3,0,5),BackColor=Color.FromArgb(247,249,247) };
        searchBox.Paint+=delegate(object sender,PaintEventArgs e) { using(Pen p=new Pen(Theme.Line)) using(System.Drawing.Drawing2D.GraphicsPath path=Theme.Round(new RectangleF(.5f,.5f,searchBox.Width-1,searchBox.Height-1),10)) { e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias; e.Graphics.DrawPath(p,path); } };
        searchBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,32)); searchBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); searchBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,104));
        searchBox.Controls.Add(new SearchMark { Dock=DockStyle.Fill,Margin=Padding.Empty },0,0);
        search.Font=new Font("Microsoft YaHei UI",13); search.BorderStyle=BorderStyle.None; search.BackColor=searchBox.BackColor; search.ForeColor=Theme.Ink; search.Dock=DockStyle.Top; search.Margin=new Padding(1,5,12,0);
        search.HandleCreated+=delegate { SetCueBanner(search.Handle,0,"搜索文件名或关键词…"); };
        search.TextChanged+=delegate { ScheduleSearch(); }; search.KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Enter) { ConfirmSearch(); e.Handled=true; e.SuppressKeyPress=true; } if(e.KeyCode==Keys.Down&&results.VirtualListSize>0) { results.Focus(); results.SelectedIndices.Add(0); e.Handled=true; } if(e.KeyCode==Keys.Escape) search.Clear(); };
        confirmSearch.Text="确认搜索"; confirmSearch.Primary=true; confirmSearch.Dock=DockStyle.Fill; confirmSearch.Margin=Padding.Empty; confirmSearch.Click+=delegate { ConfirmSearch(); };
        searchBox.Controls.Add(search,1,0); searchBox.Controls.Add(confirmSearch,2,0); main.Controls.Add(searchBox,0,1);
        main.Controls.Add(new Label { Text="输入即搜索  ·  Enter 确认  ·  Ctrl+F 快速定位",Dock=DockStyle.Fill,ForeColor=Theme.Muted,Font=new Font("Microsoft YaHei UI",8),Margin=Padding.Empty },0,2);
        TableLayoutPanel options=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty,Padding=new Padding(0,6,0,4) }; options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));
        FlowLayoutPanel toggles=new FlowLayoutPanel { Dock=DockStyle.Fill,Margin=Padding.Empty,WrapContents=false };
        fuzzySearch.Text="模糊搜索"; fuzzySearch.Checked=true; fuzzySearch.AutoSize=true; fuzzySearch.ForeColor=Theme.Ink; fuzzySearch.Margin=new Padding(0,10,18,0); fuzzySearch.CheckedChanged+=delegate { ScheduleSearch(); }; toggles.Controls.Add(fuzzySearch);
        matchPath.Text="匹配路径"; matchPath.AutoSize=true; matchPath.ForeColor=Theme.Ink; matchPath.Margin=new Padding(0,10,0,0); matchPath.CheckedChanged+=delegate { ScheduleSearch(); }; toggles.Controls.Add(matchPath); options.Controls.Add(toggles,0,0);
        FlowLayoutPanel actions=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=Padding.Empty };
        deleteFiles=Button("删除选中",delegate { DeleteSelected(); },false); ((QuietButton)deleteFiles).Danger=true; deleteFiles.Enabled=false; deleteFiles.Margin=Padding.Empty; actions.Controls.Add(deleteFiles);
        actions.Controls.Add(Button("导出结果",delegate { Export(); },false)); options.Controls.Add(actions,1,0); main.Controls.Add(options,0,3);
        rootsLabel.Text="尚未添加索引范围 · 从左侧添加文件夹开始"; rootsLabel.Dock=DockStyle.Fill; rootsLabel.AutoEllipsis=true; rootsLabel.ForeColor=Theme.Muted; rootsLabel.Font=new Font("Microsoft YaHei UI",8); rootsLabel.Margin=Padding.Empty; rootsLabel.TextAlign=ContentAlignment.MiddleLeft; main.Controls.Add(rootsLabel,0,4);
        results.Dock=DockStyle.Fill; results.View=View.Details; results.FullRowSelect=true; results.GridLines=false; results.BorderStyle=BorderStyle.None; results.Font=new Font("Microsoft YaHei UI",9); results.VirtualMode=true; results.HideSelection=false; results.MultiSelect=true;
        ImageList heights=new ImageList { ImageSize=new Size(1,36) }; results.SmallImageList=heights; results.Disposed+=delegate { heights.Dispose(); };
        results.Columns.Add("名称",250); results.Columns.Add("位置",340); results.Columns.Add("大小",110); results.Columns.Add("修改时间",178);
        results.Resize+=delegate { if(results.Columns.Count==4) { bool narrow=results.ClientSize.Width<820; int first=narrow?210:250, size=narrow?100:110, date=narrow?160:178; results.Columns[0].Width=first; results.Columns[2].Width=size; results.Columns[3].Width=date; results.Columns[1].Width=Math.Max(140,results.ClientSize.Width-first-size-date-20); } };
        results.SelectedIndexChanged+=delegate { deleteFiles.Enabled=!deleting&&results.SelectedIndices.Count>0; };
        results.RetrieveVirtualItem+=delegate(object sender,RetrieveVirtualItemEventArgs e) { if(e.ItemIndex>=visible.Length) { e.Item=new ListViewItem(""); return; } FileEntry item=visible[e.ItemIndex]; e.Item=new ListViewItem(new string[]{item.Name,item.Parent,item.Folder?"文件夹":FormatSize(item.Size),item.Modified.ToString("yyyy-MM-dd HH:mm")}) { Tag=item }; };
        results.ColumnClick+=delegate(object sender,ColumnClickEventArgs e) { descending=sortColumn==e.Column?!descending:false; sortColumn=e.Column; Search(); };
        results.DoubleClick+=delegate { OpenSelected(false); }; results.KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Enter) OpenSelected(e.Control); if(e.Control&&e.KeyCode==Keys.C) CopySelected(); if(e.KeyCode==Keys.Delete) { e.Handled=true; e.SuppressKeyPress=true; DeleteSelected(); } };
        resultMenu.Items.Add("打开",null,delegate { OpenSelected(false); }); resultMenu.Items.Add("在资源管理器中定位",null,delegate { OpenSelected(true); }); resultMenu.Items.Add("复制完整路径",null,delegate { CopySelected(); }); resultMenu.Items.Add(new ToolStripSeparator()); resultMenu.Items.Add("移至回收站",null,delegate { DeleteSelected(); }); results.ContextMenuStrip=resultMenu;
        Panel resultArea=new Panel { Dock=DockStyle.Fill,Margin=Padding.Empty,Padding=new Padding(1),BackColor=Theme.Line };
        emptyMessage.Dock=DockStyle.Fill; emptyMessage.TextAlign=ContentAlignment.MiddleCenter; emptyMessage.Font=new Font("Microsoft YaHei UI",11); emptyMessage.ForeColor=Theme.Muted; emptyMessage.BackColor=Color.White;
        resultArea.Controls.Add(results); resultArea.Controls.Add(emptyMessage); main.Controls.Add(resultArea,0,5); UpdateEmptyState(0);
        status.Dock=DockStyle.Fill; status.TextAlign=ContentAlignment.MiddleLeft; status.ForeColor=Theme.Muted; status.Font=new Font("Microsoft YaHei UI",8); status.Text="就绪 · 文件名与元数据仅在本机保存"; status.Margin=Padding.Empty; main.Controls.Add(status,0,6);
        tips.SetToolTip(fuzzySearch,"支持部分字符和少量拼写错误；直接匹配优先显示。"); tips.SetToolTip(deleteFiles,"选中文件后移至回收站，也可按 Delete；操作前会确认。");
        KeyPreview=true; KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.Control&&e.KeyCode==Keys.F) { search.Focus(); search.SelectAll(); e.Handled=true; } if(e.KeyCode==Keys.F5) Rebuild(); };
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]
    static extern IntPtr SetCueBanner(IntPtr handle,uint message,IntPtr wParam,string text);
    static void SetCueBanner(IntPtr handle,int unused,string text) { SetCueBanner(handle,0x1501,IntPtr.Zero,text); }
    void ShowSyntax() { MessageBox.Show(this,"空格：同时包含多个词\n|：或者\n!：排除\n双引号：包含空格的短语\n*.pdf：通配符\next:pdf;docx：扩展名\npath:Downloads：路径包含\ntype:folder / type:file：类型\n\n例如：报告 ext:pdf !草稿","搜索语法",MessageBoxButtons.OK,MessageBoxIcon.Information); }
    protected override void Dispose(bool disposing) {
        if(disposing) { moreMenu.Dispose(); resultMenu.Dispose(); tips.Dispose(); }
        base.Dispose(disposing);
    }
    static string FormatSize(long size) { if (size < 1024) return size + " B"; if (size < 1048576) return (size / 1024.0).ToString("0.#") + " KB"; if (size < 1073741824) return (size / 1048576.0).ToString("0.#") + " MB"; return (size / 1073741824.0).ToString("0.#") + " GB"; }
    void OnUI(Action action) { if (!life.IsCancellationRequested && !IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
    void UpdateRoots() { rootsLabel.Text = roots.Count == 0 ? "尚未添加索引范围 · 从左侧添加文件夹开始" : roots.Count + " 个索引位置  ·  " + string.Join("  ·  ", roots); tips.SetToolTip(rootsLabel,string.Join("\n",roots)); }
    void AddRoot() {
        if (working) { MessageBox.Show(this, "索引正在更新，请稍后再添加文件夹。"); return; }
        using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择要建立索引的文件夹（包括子文件夹）", ShowNewFolderButton = false }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string path = System.IO.Path.GetFullPath(dialog.SelectedPath);
            if (roots.Any(r => FileIndex.Under(path, r))) { MessageBox.Show(this, "该文件夹已包含在当前范围中。"); return; }
            roots.RemoveAll(r => FileIndex.Under(r, path)); roots.Add(path); UpdateRoots(); Watch(); Rebuild();
        }
    }
    void ManageRoots() {
        if (working) { MessageBox.Show(this, "索引正在更新，请稍后再管理范围。"); return; }
        using (Form dialog = new Form { Text = "管理索引范围", ClientSize = new Size(620, 290), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false }) {
            ListBox list = new ListBox { Dock = DockStyle.Fill }; list.Items.AddRange(roots.ToArray()); dialog.Controls.Add(list);
            Button remove = Button("移除选中的范围", delegate { if (list.SelectedItem != null) list.Items.Remove(list.SelectedItem); }, false); remove.Dock = DockStyle.Bottom; dialog.Controls.Add(remove);
            Button save = Button("保存", delegate { dialog.DialogResult = DialogResult.OK; }, true); save.Dock = DockStyle.Bottom; dialog.Controls.Add(save);
            if (dialog.ShowDialog(this) == DialogResult.OK) { roots = list.Items.Cast<string>().ToList(); UpdateRoots(); Watch(); Rebuild(); }
        }
    }
    void Watch() {
        foreach (FileSystemWatcher old in watchers) old.Dispose(); watchers.Clear();
        foreach (string root in roots) {
            try { FileSystemWatcher watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 32768, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs e) { if (!FileIndex.Under(e.FullPath, System.IO.Path.GetDirectoryName(dataFile))) changes.Enqueue(e.FullPath); };
                watcher.Created += changed; watcher.Changed += changed; watcher.Deleted += changed;
                watcher.Renamed += delegate(object sender, RenamedEventArgs e) { changes.Enqueue(e.OldFullPath); changes.Enqueue(e.FullPath); };
                watcher.Error += delegate { fullRefresh = true; }; watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            } catch (IOException) { } catch (ArgumentException) { } catch (UnauthorizedAccessException) { }
        }
    }
    void Rebuild() {
        if (working || preview) return;
        working = true; queryVersion++; status.Text = "正在建立索引…  可继续输入搜索条件"; UpdateEmptyState(visible.Length); string[] selected = roots.ToArray();
        Task.Run(delegate {
            try { FileIndex next = FileIndex.Scan(selected, life.Token); Save(next, selected); SearchSnapshot snapshot = new SearchSnapshot(next.Items.Values.ToArray()); snapshot.Sorted(0);
                OnUI(delegate { index = next; entries = snapshot.Entries; searchSnapshot = snapshot; skipped = next.Skipped; working = false; Watch(); Search(); });
            } catch (OperationCanceledException) { } catch (Exception ex) { OnUI(delegate { working = false; status.Text = "索引失败：" + ex.Message; }); }
        });
    }
    void Save(FileIndex value, string[] selected) { if (!preview) FileIndex.Save(dataFile, new SavedIndex { Roots = selected, Entries = value.Items.Values.ToArray() }); }
    void ProcessChanges() {
        if (working || preview) return;
        if (fullRefresh) { fullRefresh = false; Watch(); Rebuild(); return; }
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string path;
        while (changes.TryDequeue(out path)) { paths.Add(path); if (paths.Count >= 2000) { fullRefresh = true; return; } }
        if (paths.Count == 0) return;
        working = true; string[] selected = roots.ToArray();
        Task.Run(delegate {
            try { foreach (string changed in paths.Where(p => !paths.Any(other => !other.Equals(p, StringComparison.OrdinalIgnoreCase) && FileIndex.Under(p, other)))) {
                    if (selected.Any(r => FileIndex.Under(changed, r)) || selected.Any(r => FileIndex.Under(r, changed))) index.Apply(changed, life.Token);
                }
                Save(index, selected); SearchSnapshot snapshot = new SearchSnapshot(index.Items.Values.ToArray()); snapshot.Sorted(0); OnUI(delegate { entries = snapshot.Entries; searchSnapshot = snapshot; working = false; Search(); });
            } catch (OperationCanceledException) { } catch (Exception ex) { OnUI(delegate { working = false; fullRefresh = true; status.Text = "变更同步失败：" + ex.Message; }); }
        });
    }
    void ScheduleSearch() { queryVersion++; searchTimer.Stop(); searchTimer.Start(); }
    void ConfirmSearch() { searchTimer.Stop(); status.Text = "正在搜索…"; Search(); }
    void UpdateEmptyState(int count) {
        results.Visible = count != 0;
        emptyMessage.Visible = count == 0;
        if (count != 0) return;
        emptyMessage.Text = roots.Count == 0 ? "还没有索引范围\n\n点击「添加文件夹」，选择需要搜索的目录。" : working ? "正在建立或更新索引…\n\n完成后会自动显示匹配结果。" : entries.Length == 0 ? "索引中没有可搜索的项目\n\n检查文件夹是否可访问，或点击「重新索引」。" : "未找到匹配结果\n\n试试更短的关键词、启用模糊搜索，\n或选择「全部」并检查索引范围。";
        emptyMessage.BringToFront();
    }
    void Search() {
        int version = ++queryVersion; string query = search.Text; bool paths = matchPath.Checked; bool fuzzy = fuzzySearch.Checked; int filter = category.SelectedIndex; SearchSnapshot snapshot = searchSnapshot; int col = sortColumn; bool reverse = descending;
        Task.Run(delegate {
            try { Stopwatch clock = Stopwatch.StartNew(); FileEntry[] matches = SearchEngine.Execute(snapshot, query, paths, fuzzy, filter, col, reverse, () => version != queryVersion || life.IsCancellationRequested);
                long elapsed = clock.ElapsedMilliseconds;
                OnUI(delegate { if (version != queryVersion) return; results.VirtualListSize = 0; visible = matches; results.VirtualListSize = matches.Length; results.Invalidate();
                    UpdateEmptyState(matches.Length);
                    status.Text = string.Format("{0}{1:N0} 个结果  /  {2:N0} 个索引项  ·  {3} ms{4}{5}", matches.Length == 0 && !working && roots.Count > 0 && entries.Length > 0 ? "未找到匹配结果 · " : "", matches.Length, entries.Length, elapsed, working ? "  ·  正在更新索引" : "", skipped > 0 ? "  ·  跳过 " + skipped + " 个不可访问项" : ""); });
            } catch (OperationCanceledException) { } catch (Exception ex) { OnUI(delegate { if (version == queryVersion) status.Text = "搜索失败：" + ex.Message; }); }
        });
    }
    FileEntry Selected() { return results.SelectedIndices.Count == 0 || results.SelectedIndices[0] >= visible.Length ? null : visible[results.SelectedIndices[0]]; }
    FileEntry[] SelectedFiles() { FileEntry[] snapshot = visible; return FileActions.PlanSelection(results.SelectedIndices.Cast<int>().Where(i => i >= 0 && i < snapshot.Length).Select(i => snapshot[i])); }
    void DeleteSelected() {
        if (deleting) return;
        FileEntry[] selected;
        try { selected = SelectedFiles(); } catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "删除文件", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string details = string.Join("\n", selected.Take(8).Select(e => e.Path));
        if (selected.Length > 8) details += "\n…另有 " + (selected.Length - 8) + " 个文件";
        if (MessageBox.Show(this, "确定将选中的 " + selected.Length + " 个文件移至回收站？\n\n" + details + "\n\n可从 Windows 回收站恢复。", "确认删除文件", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        deleting = true; deleteFiles.Enabled = false; IntPtr owner = Handle; status.Text = "正在将文件移至回收站…";
        Thread worker = new Thread(delegate() {
            int done = 0; List<string> errors = new List<string>();
            foreach (FileEntry entry in selected) {
                try { FileActions.RecycleFile(entry.Path, owner); done++; }
                catch (Exception ex) { errors.Add(entry.Name + "：" + ex.Message); }
                finally { changes.Enqueue(entry.Path); }
            }
            OnUI(delegate { deleting = false; deleteFiles.Enabled = results.SelectedIndices.Count > 0; ProcessChanges();
                string summary = "已移至回收站 " + done + " 个文件。";
                if (errors.Count > 0) summary += "\n\n" + errors.Count + " 个文件未删除：\n" + string.Join("\n", errors.Take(5));
                MessageBox.Show(this, summary, "删除结果", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            });
        });
        worker.SetApartmentState(ApartmentState.STA); worker.IsBackground = true; worker.Start();
    }
    void OpenSelected(bool reveal) {
        FileEntry item = Selected(); if (item == null) return;
        try { if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) { MessageBox.Show(this, "此项已不存在，请刷新索引。"); return; }
            if (reveal) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开"); }
    }
    void CopySelected() { FileEntry item = Selected(); if (item != null) { try { Clipboard.SetText(item.Path); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } } }
    void Export() {
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "FindLite-results.csv" }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { using (StreamWriter writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true))) {
                    writer.WriteLine("Name,Path,Type,Size,Modified"); foreach (FileEntry item in visible) writer.WriteLine(Csv(item.Name) + "," + Csv(item.Path) + "," + (item.Folder ? "folder" : "file") + "," + item.Size + "," + item.Modified.ToString("o")); }
                status.Text = "已导出 " + visible.Length + " 个结果";
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败"); }
        }
    }
    static string Csv(string text) { if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text; return "\"" + text.Replace("\"", "\"\"") + "\""; }
    public void Preview(string path, bool empty = false) {
        roots = new List<string> { @"C:\Users\You\Documents", @"D:\Projects" }; UpdateRoots();
        visible = new[] { new FileEntry { Name = "2026 项目报告.pdf", Path = @"C:\Users\You\Documents\2026 项目报告.pdf", Size = 840230, Modified = DateTime.Today }, new FileEntry { Name = "设计资料", Path = @"D:\Projects\设计资料", Folder = true, Modified = DateTime.Today }, new FileEntry { Name = "产品方案.docx", Path = @"C:\Users\You\Documents\产品方案.docx", Size = 125440, Modified = DateTime.Today }, new FileEntry { Name = "FindLite.cs", Path = @"D:\Projects\FindLite.cs", Size = 22010, Modified = DateTime.Today } };
        entries = visible; if (empty) { visible = new FileEntry[0]; search.Text = "不存在的文件"; searchTimer.Stop(); queryVersion++; }
        results.VirtualListSize = visible.Length; UpdateEmptyState(visible.Length); status.Text = empty ? "未找到匹配结果 · 界面预览（示例数据）" : "界面预览（示例数据） · 4 个结果 · 模糊搜索已启用";
        Show(); Application.DoEvents(); using (Bitmap bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path); } Close();
    }
    static void WaitFor(Func<bool> condition, string message) {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition()) { Application.DoEvents(); Thread.Sleep(20); if (timer.ElapsedMilliseconds > 15000) throw new Exception("UI integration timeout: " + message); }
    }
    public void IntegrationTest(string directory) {
        string root = System.IO.Path.Combine(directory, "live-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        IntPtr handle = Handle; roots.Add(root); UpdateRoots(); Watch(); Rebuild();
        WaitFor(() => !working && entries.Length == 1, "initial scan");
        string first = System.IO.Path.Combine(root, "created.txt"); File.WriteAllText(first, "fixture");
        WaitFor(() => entries.Any(e => e.Path == first), "live create");
        string second = System.IO.Path.Combine(root, "renamed.txt"); File.Move(first, second);
        WaitFor(() => !entries.Any(e => e.Path == first) && entries.Any(e => e.Path == second), "live rename");
        category.SelectedIndex = 2; search.Clear(); ConfirmSearch(); WaitFor(() => visible.Length == 1 && visible[0].Folder, "sidebar folder filter");
        category.SelectedIndex = 0; ConfirmSearch(); WaitFor(() => visible.Length == 2 && navigation[0].Active, "sidebar all filter");
        search.Text = "renmaed ext:txt"; ConfirmSearch();
        if (searchTimer.Enabled) throw new Exception("Confirm search left debounce timer running");
        WaitFor(() => visible.Length == 1 && visible[0].Path == second, "confirmed fuzzy typo query");
        fuzzySearch.Checked = false; ConfirmSearch(); WaitFor(() => visible.Length == 0 && emptyMessage.Text.StartsWith("未找到匹配结果") && status.Text.StartsWith("未找到匹配结果"), "empty result feedback / strict mode");
        fuzzySearch.Checked = true; search.Text = "rmd ext:txt"; ConfirmSearch(); WaitFor(() => visible.Length == 1 && visible[0].Path == second, "fuzzy subsequence query");
        File.Delete(second); WaitFor(() => !entries.Any(e => e.Path == second) && visible.Length == 0, "live delete and results refresh");
        WaitFor(() => !working, "cache flush");
        if (FileIndex.Load(dataFile).Entries.Any(e => e.Path == second)) throw new Exception("Deleted file retained in cache");
        life.Cancel(); foreach (FileSystemWatcher watcher in watchers) watcher.Dispose(); searchTimer.Stop(); changeTimer.Stop();
        File.WriteAllText(System.IO.Path.Combine(directory, "ui-test-result.txt"), "PASS: real FileSystemWatcher creation, rename, delete; sidebar filters; confirmed fuzzy typo/subsequence queries; fuzzy toggle; no-result message; UI search updates; persisted cache deletion.\r\n");
    }
    public void StartupTest(string directory) {
        string root=System.IO.Path.Combine(directory,"startup-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); File.WriteAllText(System.IO.Path.Combine(root,"启动测试.txt"),"fixture");
        FileIndex fixture=FileIndex.Scan(new[]{root},CancellationToken.None); FileIndex.Save(dataFile,new SavedIndex{Roots=new[]{root},Entries=fixture.Items.Values.ToArray()});
        Opacity=0; ShowInTaskbar=false; Show(); WaitFor(()=>!working&&entries.Length==2,"background cache loading and refresh");
        search.Text="启动"; ConfirmSearch(); WaitFor(()=>visible.Length==1&&visible[0].Name=="启动测试.txt","search after normal startup");
        ClientSize=new Size(1000,640); Application.DoEvents();
        if(confirmSearch.Width<70 || deleteFiles.Width<60 || navigation.Any(b=>b.Width<100)) throw new Exception("Small-window layout invalid");
        Close(); File.WriteAllText(System.IO.Path.Combine(directory,"startup-test-result.txt"),"PASS: normal Shown lifecycle, asynchronous cache loading, automatic rescan, search after startup, minimum-window layout.\r\n");
    }
    public void MenuLifecycleTest(string directory) {
        Directory.CreateDirectory(directory); Opacity=0; ShowInTaskbar=false;
        Exception failure=null;
        Shown+=delegate { BeginInvoke((Action)delegate { try {
        int clicks=0; ToolStripMenuItem probe=new ToolStripMenuItem("Regression probe"); probe.Click+=delegate { clicks++; }; moreMenu.Items.Add(probe);
        for(int i=0;i<40;i++) {
            moreMenu.Show(this,new Point(250,120)); Application.DoEvents();
            if(i%2==0) { probe.PerformClick(); moreMenu.Close(ToolStripDropDownCloseReason.ItemClicked); }
            else moreMenu.Close(ToolStripDropDownCloseReason.Keyboard);
            Application.DoEvents();
            if(moreMenu.IsDisposed) throw new Exception("More menu was disposed during its close callback");
            resultMenu.Show(results,new Point(20,20)); Application.DoEvents(); resultMenu.Close(ToolStripDropDownCloseReason.AppClicked); Application.DoEvents();
            if(resultMenu.IsDisposed) throw new Exception("Result context menu disposed before window close");
        }
        if(clicks!=20) throw new Exception("Menu activation was not preserved");
        } catch(Exception ex) { failure=ex; } finally { Close(); } }); };
        Application.Run(this);
        if(failure!=null) throw new Exception("Menu lifecycle regression failed",failure);
        if(!moreMenu.IsDisposed||!resultMenu.IsDisposed) throw new Exception("Window-owned menus were not released on shutdown");
        File.WriteAllText(System.IO.Path.Combine(directory,"menu-test-result.txt"),"PASS: 40 More-menu open/close cycles; item activation and keyboard dismissal; 40 result-menu dismissals; menus retained during close events and disposed with their window.\r\n");
    }
}
public static class Program {
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void RecycleTest(string directory) {
        string root = System.IO.Path.GetFullPath(directory); Directory.CreateDirectory(root);
        string name = "FindLite-recycle-test-" + Guid.NewGuid().ToString("N") + "-测试.txt";
        string path = System.IO.Path.Combine(root, name);
        Require(FileIndex.Under(path, root), "test path boundary"); File.WriteAllText(path, "FindLite recycle test fixture");
        FileEntry fixture = new FileEntry { Name = name, Path = path };
        Require(FileActions.PlanSelection(new[] { fixture, fixture }).Length == 1, "deduplicate selected files");
        bool folderRejected = false; try { FileActions.PlanSelection(new[] { new FileEntry { Path = root, Folder = true } }); } catch (ArgumentException) { folderRejected = true; }
        Require(folderRejected, "folder selection rejection");
        bool emptyRejected = false; try { FileActions.PlanSelection(new FileEntry[0]); } catch (ArgumentException) { emptyRejected = true; }
        Require(emptyRejected, "empty selection rejection");
        FileActions.RecycleFile(path, IntPtr.Zero); Require(!File.Exists(path), "file removed from original location");
        bool missingRejected = false; try { FileActions.RecycleFile(path, IntPtr.Zero); } catch (FileNotFoundException) { missingRejected = true; }
        Require(missingRejected, "stale result rejection");
        bool directoryRejected = false; try { FileActions.RecycleFile(root, IntPtr.Zero); } catch (IOException) { directoryRejected = true; }
        Require(directoryRejected && Directory.Exists(root), "backend directory protection");
        File.WriteAllText(System.IO.Path.Combine(root, "recycle-fixture-name.txt"), name);
        File.WriteAllText(System.IO.Path.Combine(root, "recycle-test-result.txt"), "PASS: actual Windows recycle operation on a newly created unique test file; duplicate/empty/folder selection handling; missing file and backend directory rejection.\r\n");
    }
    static void Test(string directory) {
        Directory.CreateDirectory(directory); string root = System.IO.Path.Combine(directory, "fixture"); Directory.CreateDirectory(root); Directory.CreateDirectory(System.IO.Path.Combine(root, "子目录"));
        File.WriteAllText(System.IO.Path.Combine(root, "Report 2026.PDF"), "report"); File.WriteAllText(System.IO.Path.Combine(root, "草稿.txt"), "draft"); File.WriteAllText(System.IO.Path.Combine(root, "子目录", "image.png"), "image");
        FileIndex index = FileIndex.Scan(new[] { root }, CancellationToken.None); Require(index.Items.Count == 5, "recursive scan");
        Func<string, int> count = q => index.Items.Values.Count(e => new SearchQuery(q, false).Matches(e));
        Require(count("report 2026") == 1, "AND / case matching"); Require(count("*.pdf") == 1, "wildcard"); Require(count("ext:pdf;png") == 2, "extensions");
        Require(count("type:file !草稿") == 2, "negative / file filter"); Require(count("\"Report 2026\"") == 1, "quoted token"); Require(count("*.pdf|*.png") == 2, "OR"); Require(count("type:folder") == 2, "folder filter"); Require(count("path:子目录") == 2, "path search");
        Require(count("!\"Report 2026\" type:file") == 2, "quoted exclusion"); Require(count("*.pdf|") == 1, "empty OR branch");
        Require(SearchQuery.FuzzyMatches("report.pdf", "rpt"), "fuzzy subsequence");
        Require(SearchQuery.FuzzyMatches("report.pdf", "repotr"), "fuzzy transposition");
        Require(SearchQuery.FuzzyMatches("项目报告.pdf", "项目报高"), "Chinese typo");
        Require(!SearchQuery.FuzzyMatches("photo.jpg", "zebra"), "unrelated fuzzy query");
        Require(!SearchQuery.FuzzyMatches("photo.jpg", "xy"), "short fuzzy precision");
        Require(!SearchQuery.FuzzyMatches("2026.pdf", "2027"), "numeric precision");
        FileEntry pdf = index.Items.Values.First(e => e.Name == "Report 2026.PDF");
        Require(!new SearchQuery("ext:pfd", false, true).Matches(pdf), "extension syntax stays exact");
        Require(new SearchQuery("!reprot", false, true).Matches(pdf), "exclusion stays exact");
        string added = System.IO.Path.Combine(root, "new.txt"); File.WriteAllText(added, "new"); index.Apply(added, CancellationToken.None); Require(count("new.txt") == 1, "incremental create");
        string renamed = System.IO.Path.Combine(root, "renamed.txt"); File.Move(added, renamed); index.Apply(added, CancellationToken.None); index.Apply(renamed, CancellationToken.None); Require(count("new.txt") == 0 && count("renamed.txt") == 1, "incremental rename");
        File.Delete(renamed); index.Apply(renamed, CancellationToken.None); Require(count("renamed.txt") == 0, "incremental delete");
        string cache = System.IO.Path.Combine(directory, "index.json"); FileIndex.Save(cache, new SavedIndex { Roots = new[] { root }, Entries = index.Items.Values.ToArray() }); Require(FileIndex.Load(cache).Entries.Length == 5, "cache roundtrip"); FileIndex.Save(cache, FileIndex.Load(cache)); Require(FileIndex.Load(cache).Entries.Length == 5, "atomic replacement");
        index.RemoveTree(System.IO.Path.Combine(root, "子目录")); Require(count("image.png") == 0, "subtree removal");
        Require(!FileIndex.Under(root + "-other\\x", root), "path boundary");
        FileEntry[] benchmark = Enumerable.Range(0, 100000).Select(i => new FileEntry { Name = "document-" + i + ".pdf", Path = "C:\\fixture\\document-" + i + ".pdf" }).ToArray();
        Stopwatch clock = Stopwatch.StartNew(); SearchQuery query = new SearchQuery("document 999", false); int matches = benchmark.Count(query.Matches); clock.Stop(); Require(matches > 0, "large index query");
        long strictMs = clock.ElapsedMilliseconds; clock.Restart(); SearchQuery fuzzy = new SearchQuery("documemt", false, true); int fuzzyCount = benchmark.Count(fuzzy.Matches); clock.Stop(); Require(fuzzyCount == benchmark.Length, "large fuzzy index query");
        File.WriteAllText(System.IO.Path.Combine(directory, "test-result.txt"), "PASS: recursive indexing, AND/case, wildcard, extensions, exclusion, quoted text, OR, folder/path filters, fuzzy subsequence/typo/Chinese, fuzzy precision, incremental create/rename/delete, persistence/replacement, subtree removal, path boundary.\r\n100,000 synthetic entries: strict query " + strictMs + " ms; matches=" + matches + "; fuzzy typo query " + clock.ElapsedMilliseconds + " ms; matches=" + fuzzyCount + ".\r\n");
    }
    [STAThread] public static int Main(string[] args) {
        try { if (args.Length == 2 && args[0] == "--self-test") { Test(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--recycle-test") { RecycleTest(args[1]); return 0; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--ui-test") { using (MainWindow window = new MainWindow(false, System.IO.Path.Combine(args[1], "ui-cache", "index.json"))) window.IntegrationTest(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--startup-test") { using (MainWindow window = new MainWindow(false, System.IO.Path.Combine(args[1], "startup-cache", "index.json"))) window.StartupTest(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--menu-test") { using (MainWindow window = new MainWindow(true)) window.MenuLifecycleTest(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--preview") { using (MainWindow window = new MainWindow(true)) window.Preview(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--preview-empty") { using (MainWindow window = new MainWindow(true)) window.Preview(args[1], true); return 0; }
            if (args.Length == 2 && args[0] == "--preview-small") { using (MainWindow window = new MainWindow(true)) { window.ClientSize=new Size(1000,640); window.Preview(args[1]); } return 0; }
            bool created; using(Mutex mutex=new Mutex(true,"Local\\FindLite.Desktop.v1",out created)) {
                if(!created) { MessageBox.Show("FindLite 已在运行，请从任务栏打开现有窗口。","FindLite",MessageBoxButtons.OK,MessageBoxIcon.Information); return 0; }
                try { Application.Run(new MainWindow(false)); } finally { mutex.ReleaseMutex(); }
            }
            return 0;
        } catch (Exception ex) { if (args.Length > 0) { File.WriteAllText("FindLite-error.txt", ex.ToString()); return 1; } MessageBox.Show(ex.Message, "FindLite"); return 1; }
    }
}
