// ============================================================================
//  DiskUsage - Phan tich dung luong o dia (WinForms, .NET Framework 4.x)
//  Giao dien phong cach Windows 7 Aero. Build: build.bat (dung csc.exe co san)
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskUsage
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ------------------------------------------------------------------ Model
    class FolderNode
    {
        public string Name;
        public string FullPath;
        public long Size;
        public long FileCount;
        public long DirCount;
        public long OwnSize;      // dung luong cac tep nam truc tiep trong thu muc
        public long OwnFiles;
        public bool AccessDenied;
        public List<FolderNode> Children = new List<FolderNode>();
    }

    class FileEntry { public string Path; public long Size; public bool IsVideo; }
    class ExtStat { public long Size; public long Count; }

    class NodeInfo
    {
        public FolderNode F;
        public double Pct;       // ty le so voi thu muc cha
        public bool IsFiles;     // node gia "[Tep nam truc tiep...]"
    }

    // ---------------------------------------------------------------- Scanner
    class Scanner
    {
        const int FindExInfoBasic = 1;
        const int FIND_FIRST_EX_LARGE_FETCH = 2;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId,
            out WIN32_FIND_DATA lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr hFindFile);

        static readonly HashSet<string> ImgExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico" };

        static readonly HashSet<string> VidExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
          ".mpg", ".mpeg", ".3gp", ".ts", ".vob" };

        public static bool IsVideoPath(string path)
        {
            int dot = path.LastIndexOf('.');
            return dot >= 0 && VidExt.Contains(path.Substring(dot));
        }

        public long TotalFiles, TotalDirs, TotalBytes, DeniedDirs;
        public volatile string CurrentPath = "";

        public ConcurrentBag<FileEntry> Images = new ConcurrentBag<FileEntry>();
        public ConcurrentDictionary<string, ExtStat> ExtStats =
            new ConcurrentDictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);

        const int TOP_N = 15;
        public List<FileEntry> TopFiles = new List<FileEntry>();
        readonly object _topLock = new object();
        long _minTop;
        int _topCount;

        static string ToLong(string p)
        {
            return p.StartsWith(@"\\?\") ? p : @"\\?\" + p;
        }

        public FolderNode Scan(string root, CancellationToken ct)
        {
            string display = root.Length == 2 && root[1] == ':' ? root + "\\" : root;
            string name = display.TrimEnd('\\');
            if (name.Length == 2 && name[1] == ':') name = display; // "C:\"
            else name = Path.GetFileName(name);
            if (string.IsNullOrEmpty(name)) name = display;
            return ScanDir(display, name, 0, ct);
        }

        FolderNode ScanDir(string path, string name, int depth, CancellationToken ct)
        {
            var node = new FolderNode { FullPath = path, Name = name };
            if (ct.IsCancellationRequested) return node;
            CurrentPath = path;

            string prefix = path.EndsWith("\\") ? path : path + "\\";
            WIN32_FIND_DATA fd;
            IntPtr h = FindFirstFileExW(ToLong(prefix) + "*", FindExInfoBasic, out fd, 0,
                                        IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
            if (h == INVALID_HANDLE_VALUE)
            {
                node.AccessDenied = true;
                Interlocked.Increment(ref DeniedDirs);
                return node;
            }

            var subdirs = new List<string>();
            long ownSize = 0, ownFiles = 0;
            try
            {
                do
                {
                    if (ct.IsCancellationRequested) break;
                    uint attr = fd.dwFileAttributes;
                    string fn = fd.cFileName;
                    if ((attr & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        if (fn == "." || fn == "..") continue;
                        if ((attr & FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue; // tranh junction/symlink lap vo han
                        subdirs.Add(fn);
                    }
                    else
                    {
                        long sz = ((long)fd.nFileSizeHigh << 32) + fd.nFileSizeLow;
                        ownSize += sz;
                        ownFiles++;
                        RecordFile(prefix + fn, fn, sz);
                    }
                } while (FindNextFileW(h, out fd));
            }
            finally { FindClose(h); }

            Interlocked.Add(ref TotalBytes, ownSize);
            Interlocked.Add(ref TotalFiles, ownFiles);
            Interlocked.Add(ref TotalDirs, subdirs.Count);

            node.OwnSize = ownSize;
            node.OwnFiles = ownFiles;
            long total = ownSize, fileCount = ownFiles, dirCount = subdirs.Count;

            if (subdirs.Count > 0)
            {
                var children = new FolderNode[subdirs.Count];
                if (depth < 2 && subdirs.Count > 1)
                {
                    Parallel.For(0, subdirs.Count, i =>
                    {
                        children[i] = ScanDir(prefix + subdirs[i], subdirs[i], depth + 1, ct);
                    });
                }
                else
                {
                    for (int i = 0; i < subdirs.Count; i++)
                        children[i] = ScanDir(prefix + subdirs[i], subdirs[i], depth + 1, ct);
                }
                foreach (var c in children)
                {
                    total += c.Size;
                    fileCount += c.FileCount;
                    dirCount += c.DirCount;
                }
                node.Children = new List<FolderNode>(children);
            }

            node.Size = total;
            node.FileCount = fileCount;
            node.DirCount = dirCount;
            return node;
        }

        void RecordFile(string full, string name, long size)
        {
            int dot = name.LastIndexOf('.');
            string ext = (dot > 0 && dot < name.Length - 1)
                ? name.Substring(dot).ToLowerInvariant() : "(không có đuôi)";

            var st = ExtStats.GetOrAdd(ext, delegate { return new ExtStat(); });
            Interlocked.Add(ref st.Size, size);
            Interlocked.Increment(ref st.Count);

            bool isVid = VidExt.Contains(ext);
            if (isVid || ImgExt.Contains(ext))
                Images.Add(new FileEntry { Path = full, Size = size, IsVideo = isVid });

            if (size > _minTop || _topCount < TOP_N)
            {
                lock (_topLock)
                {
                    TopFiles.Add(new FileEntry { Path = full, Size = size });
                    if (TopFiles.Count > TOP_N)
                    {
                        TopFiles.Sort(delegate(FileEntry a, FileEntry b) { return b.Size.CompareTo(a.Size); });
                        TopFiles.RemoveRange(TOP_N, TopFiles.Count - TOP_N);
                        _minTop = TopFiles[TopFiles.Count - 1].Size;
                    }
                    _topCount = TopFiles.Count;
                }
            }
        }
    }

    // ------------------------------------------------------- Bang mau Aero 7
    static class Aero
    {
        public static readonly Color ToolbarTop = Color.FromArgb(250, 252, 255);
        public static readonly Color ToolbarBottom = Color.FromArgb(222, 233, 246);
        public static readonly Color ToolbarBorder = Color.FromArgb(178, 195, 214);
        public static readonly Color StatusTop = Color.FromArgb(238, 244, 251);
        public static readonly Color StatusBottom = Color.FromArgb(207, 221, 238);
        public static readonly Color PaneBack = Color.FromArgb(240, 246, 252);
        public static readonly Color SelBorder = Color.FromArgb(125, 162, 206);
        public static readonly Color SelTop = Color.FromArgb(220, 235, 252);
        public static readonly Color SelBottom = Color.FromArgb(193, 219, 246);
        public static readonly Color BarBlue = Color.FromArgb(86, 148, 210);
        public static readonly Color BarGreen = Color.FromArgb(96, 169, 88);
        public static readonly Color BarOrange = Color.FromArgb(235, 166, 61);
        public static readonly Color BarRed = Color.FromArgb(213, 88, 79);
        public static readonly Color TextMain = Color.FromArgb(28, 40, 56);
        public static readonly Color TextDim = Color.FromArgb(110, 125, 140);

        public static void FillGradient(Graphics g, Rectangle r, Color top, Color bottom)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var br = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                g.FillRectangle(br, r);
        }

        // Thanh dung luong bong kinh kieu Win7
        public static void GlossyBar(Graphics g, Rectangle r, Color c)
        {
            if (r.Width < 2 || r.Height < 4) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(r, 3))
            {
                using (var br = new LinearGradientBrush(r,
                    ControlPaint.Light(c, 0.6f), c, LinearGradientMode.Vertical))
                    g.FillPath(br, path);
                var half = new Rectangle(r.X, r.Y, r.Width, r.Height / 2);
                if (half.Height > 0)
                    using (var shine = new LinearGradientBrush(half,
                        Color.FromArgb(160, 255, 255, 255), Color.FromArgb(30, 255, 255, 255),
                        LinearGradientMode.Vertical))
                        g.FillRectangle(shine, half);
                using (var pen = new Pen(ControlPaint.Dark(c, 0.05f)))
                    g.DrawPath(pen, path);
            }
            g.SmoothingMode = old;
        }

        public static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Vien chon gradient kieu item Explorer Win7
        public static void DrawSelection(Graphics g, Rectangle r)
        {
            var rr = new Rectangle(r.X, r.Y, Math.Max(2, r.Width - 1), Math.Max(2, r.Height - 1));
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(rr, 3))
            {
                using (var br = new LinearGradientBrush(rr,
                    Color.FromArgb(130, SelTop), Color.FromArgb(130, SelBottom),
                    LinearGradientMode.Vertical))
                    g.FillPath(br, path);
                using (var pen = new Pen(SelBorder))
                    g.DrawPath(pen, path);
            }
            g.SmoothingMode = old;
        }

        public static Color PctColor(double p)
        {
            if (p >= 0.50) return BarRed;
            if (p >= 0.20) return BarOrange;
            if (p >= 0.05) return BarBlue;
            return BarGreen;
        }
    }

    // --------------------------------------------------------------- MainForm
    class MainForm : Form
    {
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const int TVM_SETEXTENDEDSTYLE = 0x1100 + 44;
        const int TVS_EX_DOUBLEBUFFER = 0x0004;

        class DriveItem
        {
            public string Path; public string Label;
            public override string ToString() { return Label; }
        }

        ComboBox cboDrive;
        Button btnBrowse, btnScan;
        Label lblStatus;
        Panel toolbar, statusBar;
        System.Windows.Forms.Timer uiTimer;
        TabControl tabs;

        TreeView tree;
        Label lblInfo;
        Panel driveBar;
        ListView lvFolders, lvBigFiles, lvExt;

        ThumbGrid grid;
        ComboBox cboMinSize, cboMediaType;
        Label lblImgCount;
        NumericUpDown numPageSize, numPage;
        Button btnPrev, btnNext;
        Label lblPageTotal;
        int pageIndex;
        int pageStart;   // index cua anh dau trang trong filteredImages
        bool pageGuard;  // chan ValueChanged khi tu cap nhat numPage

        Scanner scanner;
        CancellationTokenSource cts;
        FolderNode rootNode;
        Stopwatch sw = new Stopwatch();
        bool scanning;
        string scanPath = "";

        List<FileEntry> allImages = new List<FileEntry>();
        List<FileEntry> filteredImages = new List<FileEntry>();

        long driveTotal, driveFree;

        static readonly long[] MinSizes = { 0, 100 * 1024, 500 * 1024, 1024 * 1024, 5 * 1024 * 1024 };

        public MainForm()
        {
            Text = "Disk Usage Analyzer — Phân tích dung lượng ổ đĩa";
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Aero.PaneBack;
            Size = new Size(1280, 800);
            MinimumSize = new Size(960, 620);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildToolbar();
            BuildTabs();
            BuildStatusBar();

            Controls.Add(tabs);
            Controls.Add(statusBar);
            Controls.Add(toolbar);

            uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
            uiTimer.Tick += delegate { UpdateProgress(); };

            LoadDrives();

            // Nap cache anh cua duong dan dang chon (sau khi form co handle)
            cboDrive.SelectedIndexChanged += delegate
            {
                if (!scanning && IsHandleCreated) TryLoadImageCache();
            };
            // Khi go tay xong (roi o nhap) cung thu nap cache cua duong dan do
            cboDrive.Leave += delegate
            {
                if (!scanning && IsHandleCreated) TryLoadImageCache();
            };
            Load += delegate { TryLoadImageCache(); };
        }

        // ------------------------------------------------------------ Toolbar
        void BuildToolbar()
        {
            toolbar = new Panel { Dock = DockStyle.Top, Height = 46 };
            toolbar.Paint += delegate(object s, PaintEventArgs e)
            {
                Aero.FillGradient(e.Graphics, toolbar.ClientRectangle, Aero.ToolbarTop, Aero.ToolbarBottom);
                using (var pen = new Pen(Aero.ToolbarBorder))
                    e.Graphics.DrawLine(pen, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
            };

            var lbl = new Label
            {
                Text = "Ổ đĩa / thư mục:", AutoSize = true, Location = new Point(12, 14),
                BackColor = Color.Transparent, ForeColor = Aero.TextMain
            };
            cboDrive = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,   // cho phep go tay duong dan
                Location = new Point(120, 10), Width = 282,
                Font = new Font("Segoe UI", 9.75f)
            };
            // Enter trong o duong dan = bam Quet
            cboDrive.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    if (!scanning) ToggleScan();
                }
            };
            btnBrowse = MakeButton("Chọn thư mục…", new Point(412, 9), 120);
            btnBrowse.Click += delegate { BrowseFolder(); };

            btnScan = MakeButton("▶  Quét", new Point(542, 9), 110);
            btnScan.Font = new Font("Segoe UI Semibold", 9.75f);
            btnScan.Click += delegate { ToggleScan(); };

            toolbar.Controls.Add(lbl);
            toolbar.Controls.Add(cboDrive);
            toolbar.Controls.Add(btnBrowse);
            toolbar.Controls.Add(btnScan);
        }

        static Button MakeButton(string text, Point loc, int w)
        {
            return new Button
            {
                Text = text, Location = loc, Size = new Size(w, 28),
                BackColor = Color.FromArgb(245, 249, 254),
                FlatStyle = FlatStyle.System,
                UseVisualStyleBackColor = false
            };
        }

        void BuildStatusBar()
        {
            statusBar = new Panel { Dock = DockStyle.Bottom, Height = 26 };
            statusBar.Paint += delegate(object s, PaintEventArgs e)
            {
                Aero.FillGradient(e.Graphics, statusBar.ClientRectangle, Aero.StatusTop, Aero.StatusBottom);
                using (var pen = new Pen(Aero.ToolbarBorder))
                    e.Graphics.DrawLine(pen, 0, 0, statusBar.Width, 0);
            };
            lblStatus = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent, ForeColor = Aero.TextMain,
                Padding = new Padding(10, 0, 0, 0),
                Text = "Sẵn sàng. Chọn ổ đĩa rồi bấm Quét."
            };
            statusBar.Controls.Add(lblStatus);
        }

        // --------------------------------------------------------------- Tabs
        void BuildTabs()
        {
            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 5) };

            // ---- Tab 1: Cay thu muc
            var tpTree = new TabPage("  Cây thư mục  ") { BackColor = Color.White };
            tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                DrawMode = TreeViewDrawMode.OwnerDrawText,
                FullRowSelect = true,
                ShowLines = false,
                HideSelection = false,
                ItemHeight = 24,
                Indent = 20,
                Font = new Font("Segoe UI", 9.75f),
                BackColor = Color.White
            };
            tree.HandleCreated += delegate
            {
                SendMessage(tree.Handle, TVM_SETEXTENDEDSTYLE,
                    (IntPtr)TVS_EX_DOUBLEBUFFER, (IntPtr)TVS_EX_DOUBLEBUFFER);
            };
            tree.DrawNode += Tree_DrawNode;
            tree.BeforeExpand += delegate(object s, TreeViewCancelEventArgs e) { EnsurePopulated(e.Node); };
            tree.NodeMouseClick += delegate(object s, TreeNodeMouseClickEventArgs e)
            {
                if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node;
            };
            tree.Resize += delegate { tree.Invalidate(); };

            var cms = new ContextMenuStrip();
            cms.Items.Add("Mở trong Explorer", null, delegate
            {
                var ni = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as NodeInfo;
                if (ni != null) TryOpenExplorer(ni.F.FullPath);
            });
            cms.Items.Add("Sao chép đường dẫn", null, delegate
            {
                var ni = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as NodeInfo;
                if (ni != null) try { Clipboard.SetText(ni.F.FullPath); } catch { }
            });
            tree.ContextMenuStrip = cms;
            tpTree.Controls.Add(tree);

            // ---- Tab 2: Tong quan
            var tpSum = new TabPage("  Tổng quan  ") { BackColor = Aero.PaneBack };
            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            lblInfo = new Label
            {
                Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10.5f),
                ForeColor = Aero.TextMain, Padding = new Padding(14, 10, 10, 0),
                Text = "Chưa có dữ liệu. Hãy chạy quét trước."
            };
            driveBar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 8) };
            driveBar.Paint += DriveBar_Paint;
            driveBar.Resize += delegate { driveBar.Invalidate(); };

            var grids = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            grids.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            grids.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            grids.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));

            lvFolders = MakeListView(new string[] { "Thư mục", "Dung lượng", "%" }, new int[] { 300, 95, 55 });
            lvBigFiles = MakeListView(new string[] { "Tệp lớn nhất", "Dung lượng" }, new int[] { 320, 95 });
            lvExt = MakeListView(new string[] { "Loại tệp", "Số lượng", "Dung lượng" }, new int[] { 90, 90, 95 });

            lvFolders.DoubleClick += delegate
            {
                if (lvFolders.SelectedItems.Count > 0)
                    JumpToPath((string)lvFolders.SelectedItems[0].Tag);
            };
            lvBigFiles.DoubleClick += delegate
            {
                if (lvBigFiles.SelectedItems.Count > 0)
                    TrySelectInExplorer((string)lvBigFiles.SelectedItems[0].Tag);
            };

            grids.Controls.Add(WrapGroup("Top 15 thư mục lớn nhất  (nháy đúp để mở trong cây)", lvFolders), 0, 0);
            grids.Controls.Add(WrapGroup("Top 15 tệp lớn nhất  (nháy đúp để mở Explorer)", lvBigFiles), 1, 0);
            grids.Controls.Add(WrapGroup("Top 15 loại tệp theo dung lượng", lvExt), 2, 0);

            tlp.Controls.Add(lblInfo, 0, 0);
            tlp.Controls.Add(driveBar, 0, 1);
            tlp.Controls.Add(grids, 0, 2);
            tpSum.Controls.Add(tlp);

            // ---- Tab 3: Anh & Video
            var tpImg = new TabPage("  Ảnh && Video  ") { BackColor = Aero.PaneBack };
            var imgTop = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 7, 0, 0),
                BackColor = Aero.PaneBack
            };
            lblImgCount = new Label
            {
                Text = "Chưa quét.", AutoSize = true, Margin = new Padding(4, 6, 16, 0),
                ForeColor = Aero.TextMain
            };
            cboMinSize = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Width = 130,
                Margin = new Padding(0, 2, 10, 0)
            };
            cboMinSize.Items.AddRange(new object[]
                { "Tất cả kích cỡ", "≥ 100 KB", "≥ 500 KB", "≥ 1 MB", "≥ 5 MB" });
            cboMinSize.SelectedIndex = 1;
            cboMinSize.SelectedIndexChanged += delegate { ApplyImageFilter(); };

            cboMediaType = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Width = 110,
                Margin = new Padding(0, 2, 10, 0)
            };
            cboMediaType.Items.AddRange(new object[] { "Ảnh & video", "Chỉ ảnh", "Chỉ video" });
            cboMediaType.SelectedIndex = 0;
            cboMediaType.SelectedIndexChanged += delegate { ApplyImageFilter(); };

            var lblPs = new Label
            {
                Text = "Ảnh/trang:", AutoSize = true, Margin = new Padding(0, 6, 4, 0),
                ForeColor = Aero.TextMain
            };
            numPageSize = new NumericUpDown
            {
                Minimum = 12, Maximum = 2000, Value = 36, Increment = 12,
                Width = 65, Margin = new Padding(0, 3, 14, 0),
                TextAlign = HorizontalAlignment.Right
            };
            numPageSize.ValueChanged += delegate { if (!pageGuard) ShowPage(0); };

            btnPrev = new Button
            {
                Text = "◀", Width = 34, Height = 26, Margin = new Padding(0, 1, 4, 0),
                FlatStyle = FlatStyle.System, Enabled = false
            };
            btnPrev.Click += delegate { ShowPage(pageIndex - 1); };
            numPage = new NumericUpDown
            {
                Minimum = 1, Maximum = 1, Value = 1,
                Width = 60, Margin = new Padding(0, 3, 4, 0),
                TextAlign = HorizontalAlignment.Right
            };
            numPage.ValueChanged += delegate { if (!pageGuard) ShowPage((int)numPage.Value - 1); };
            lblPageTotal = new Label
            {
                Text = "/ 1 trang", AutoSize = true, Margin = new Padding(0, 6, 4, 0),
                ForeColor = Aero.TextMain
            };
            btnNext = new Button
            {
                Text = "▶", Width = 34, Height = 26, Margin = new Padding(0, 1, 0, 0),
                FlatStyle = FlatStyle.System, Enabled = false
            };
            btnNext.Click += delegate { ShowPage(pageIndex + 1); };

            imgTop.Controls.Add(lblImgCount);
            imgTop.Controls.Add(cboMediaType);
            imgTop.Controls.Add(cboMinSize);
            imgTop.Controls.Add(lblPs);
            imgTop.Controls.Add(numPageSize);
            imgTop.Controls.Add(btnPrev);
            imgTop.Controls.Add(numPage);
            imgTop.Controls.Add(lblPageTotal);
            imgTop.Controls.Add(btnNext);

            grid = new ThumbGrid { Dock = DockStyle.Fill };
            grid.ItemActivate += delegate(int idx)
            {
                int g = pageStart + idx;
                if (g < 0 || g >= filteredImages.Count) return;
                if (filteredImages[g].IsVideo)
                {
                    try { Process.Start(filteredImages[g].Path); } catch { }
                    return;
                }
                using (var f = new PreviewForm(filteredImages, g))
                    f.ShowDialog(this);
            };
            var cmsImg = new ContextMenuStrip();
            cmsImg.Items.Add("Xem / Phát", null, delegate
            {
                var fe = SelectedImage();
                if (fe == null) return;
                if (fe.IsVideo)
                {
                    try { Process.Start(fe.Path); } catch { }
                    return;
                }
                using (var f = new PreviewForm(filteredImages, pageStart + grid.SelectedIndex))
                    f.ShowDialog(this);
            });
            cmsImg.Items.Add("Mở trong Explorer", null, delegate
            {
                var fe = SelectedImage();
                if (fe != null) TrySelectInExplorer(fe.Path);
            });
            cmsImg.Items.Add("Sao chép đường dẫn", null, delegate
            {
                var fe = SelectedImage();
                if (fe != null) try { Clipboard.SetText(fe.Path); } catch { }
            });
            cmsImg.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (SelectedImage() == null) e.Cancel = true;
            };
            grid.ContextMenuStrip = cmsImg;

            tpImg.Controls.Add(grid);
            tpImg.Controls.Add(imgTop);

            tabs.TabPages.Add(tpTree);
            tabs.TabPages.Add(tpSum);
            tabs.TabPages.Add(tpImg);
        }

        static ListView MakeListView(string[] cols, int[] widths)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = Color.White
            };
            for (int i = 0; i < cols.Length; i++)
                lv.Columns.Add(cols[i], widths[i],
                    i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            EnableDoubleBuffer(lv);
            return lv;
        }

        static void EnableDoubleBuffer(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(c, true, null);
            }
            catch { }
        }

        static GroupBox WrapGroup(string title, Control inner)
        {
            var gb = new GroupBox
            {
                Text = title, Dock = DockStyle.Fill,
                Padding = new Padding(6, 4, 6, 6),
                ForeColor = Color.FromArgb(30, 57, 91),
                Margin = new Padding(6)
            };
            inner.Dock = DockStyle.Fill;
            gb.Controls.Add(inner);
            return gb;
        }

        // ---------------------------------------------------- Ve node cua cay
        void Tree_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null || e.Bounds.Height <= 0) { e.DrawDefault = true; return; }
            var ni = e.Node.Tag as NodeInfo;
            if (ni == null) { e.DrawDefault = true; return; } // node "..." tam

            var g = e.Graphics;
            var row = new Rectangle(e.Bounds.X, e.Bounds.Y,
                Math.Max(40, tree.ClientSize.Width - e.Bounds.X - 4), e.Bounds.Height);
            bool sel = (e.State & TreeNodeStates.Selected) != 0;

            using (var bg = new SolidBrush(Color.White)) g.FillRectangle(bg, row);

            // thanh dung luong theo % cua thu muc cha (bo goc, gradient nhe kieu Aero)
            int barW = (int)(row.Width * Math.Min(1.0, ni.Pct));
            if (barW > 6)
            {
                var barRect = new Rectangle(row.X, row.Y + 3, barW, row.Height - 6);
                Color c = ni.IsFiles ? Color.FromArgb(150, 160, 172) : Aero.PctColor(ni.Pct);
                var oldSm = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Aero.Rounded(barRect, 4))
                {
                    using (var br = new LinearGradientBrush(barRect,
                        Color.FromArgb(75, c), Color.FromArgb(38, c), LinearGradientMode.Vertical))
                        g.FillPath(br, path);
                    var half = new Rectangle(barRect.X, barRect.Y, barRect.Width, barRect.Height / 2);
                    if (half.Height > 0)
                        using (var region = new Region(path))
                        {
                            region.Intersect(half);
                            using (var shine = new SolidBrush(Color.FromArgb(48, 255, 255, 255)))
                                g.FillRegion(shine, region);
                        }
                    using (var pen = new Pen(Color.FromArgb(130, c)))
                        g.DrawPath(pen, path);
                }
                g.SmoothingMode = oldSm;
            }

            if (sel) Aero.DrawSelection(g, row); // vien chon kieu Aero

            Color fore = ni.F.AccessDenied || ni.IsFiles ? Aero.TextDim : Aero.TextMain;
            TextRenderer.DrawText(g, e.Node.Text, tree.Font,
                new Rectangle(row.X + 6, row.Y, row.Width - 8, row.Height),
                fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // ------------------------------------------------------- Drive combo
        void LoadDrives()
        {
            cboDrive.Items.Clear();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    string label = string.Format("{0}  [{1}]  —  {2} / còn trống {3}",
                        d.Name, d.DriveFormat, Util.FormatSize(d.TotalSize), Util.FormatSize(d.TotalFreeSpace));
                    cboDrive.Items.Add(new DriveItem { Path = d.Name, Label = label });
                }
            }
            catch { }
            if (cboDrive.Items.Count > 0) cboDrive.SelectedIndex = 0;
        }

        void BrowseFolder()
        {
            string cur = (cboDrive.Text ?? "").Trim().Trim('"');
            string init = cur.Length > 0 && Directory.Exists(cur) ? cur : null;

            // Hop thoai kieu Vista: co thanh dia chi + o "Folder:" de go/dan duong dan
            string picked;
            if (FolderPicker.TryPick(Handle, init,
                    "Chọn thư mục cần phân tích dung lượng", out picked))
            {
                if (string.IsNullOrEmpty(picked)) return; // nguoi dung bam Huy
            }
            else
            {
                // OS cu khong ho tro -> quay ve hop thoai truyen thong
                using (var dlg = new FolderBrowserDialog
                    { Description = "Chọn thư mục cần phân tích dung lượng" })
                {
                    if (init != null) dlg.SelectedPath = init;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    picked = dlg.SelectedPath;
                }
            }

            var item = new DriveItem { Path = picked, Label = "📂 " + picked };
            cboDrive.Items.Add(item);
            cboDrive.SelectedItem = item;
        }

        // Phan giai duong dan can quet: uu tien muc chon trong list, neu khong
        // lay text nguoi dung go (bo dau nhay, khoang trang, dau \ thua)
        string ResolveScanPath()
        {
            var item = cboDrive.SelectedItem as DriveItem;
            string text = (cboDrive.Text ?? "").Trim().Trim('"');
            if (item != null && string.Equals(cboDrive.Text, item.Label, StringComparison.Ordinal))
                return item.Path;
            if (text.Length == 0) return null;
            // "D:" -> "D:\"; giu nguyen UNC \\server\share
            if (text.Length == 2 && text[1] == ':') text += "\\";
            else if (text.Length > 3) text = text.TrimEnd('\\');
            return text;
        }

        // --------------------------------------------------------------- Scan
        void ToggleScan()
        {
            if (scanning) { if (cts != null) cts.Cancel(); return; }
            string path = ResolveScanPath();
            if (string.IsNullOrEmpty(path))
            { MessageBox.Show(this, "Hãy chọn ổ đĩa hoặc nhập đường dẫn thư mục."); return; }
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "Không tìm thấy thư mục:\r\n" + path,
                    "Đường dẫn không hợp lệ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            scanPath = path;
            scanning = true;
            btnScan.Text = "■  Dừng";
            btnBrowse.Enabled = false;
            cboDrive.Enabled = false;

            tree.Nodes.Clear();
            lvFolders.Items.Clear();
            lvBigFiles.Items.Clear();
            lvExt.Items.Clear();
            allImages.Clear();
            filteredImages.Clear();
            ShowPage(0);
            lblImgCount.Text = "Đang quét…";
            lblInfo.Text = "Đang quét, vui lòng chờ…";
            driveTotal = driveFree = 0;
            driveBar.Invalidate();

            try
            {
                var di = new DriveInfo(scanPath.Substring(0, 1));
                if (scanPath.Length <= 3) { driveTotal = di.TotalSize; driveFree = di.TotalFreeSpace; }
            }
            catch { }

            scanner = new Scanner();
            cts = new CancellationTokenSource();
            sw.Restart();
            uiTimer.Start();

            var token = cts.Token;
            var sc = scanner;
            Task.Run(delegate { return sc.Scan(scanPath, token); })
                .ContinueWith(delegate(Task<FolderNode> t)
                {
                    try
                    {
                        BeginInvoke((Action)delegate { ScanFinished(t); });
                    }
                    catch { }
                });
        }

        void ScanFinished(Task<FolderNode> t)
        {
            sw.Stop();
            uiTimer.Stop();
            scanning = false;
            btnScan.Text = "▶  Quét";
            btnBrowse.Enabled = true;
            cboDrive.Enabled = true;

            if (t.IsFaulted || t.Result == null)
            {
                lblStatus.Text = "Lỗi khi quét: " +
                    (t.Exception != null ? t.Exception.InnerException.Message : "không rõ");
                return;
            }

            rootNode = t.Result;
            bool canceled = cts.IsCancellationRequested;

            BuildTree();
            BuildSummary(canceled);
            PrepareImages();

            lblStatus.Text = string.Format(
                "{0}  —  {1} · {2:N0} tệp · {3:N0} thư mục · quét trong {4:0.0} giây{5}",
                canceled ? "Đã dừng (kết quả một phần)" : "Hoàn tất",
                Util.FormatSize(rootNode.Size), rootNode.FileCount, rootNode.DirCount,
                sw.Elapsed.TotalSeconds,
                scanner.DeniedDirs > 0
                    ? string.Format(" · {0:N0} thư mục không truy cập được", Interlocked.Read(ref scanner.DeniedDirs))
                    : "");
        }

        void UpdateProgress()
        {
            if (scanner == null) return;
            string p = scanner.CurrentPath ?? "";
            if (p.Length > 70) p = p.Substring(0, 34) + "…" + p.Substring(p.Length - 34);
            lblStatus.Text = string.Format("Đang quét…  {0:N0} tệp · {1:N0} thư mục · {2}    {3}",
                Interlocked.Read(ref scanner.TotalFiles),
                Interlocked.Read(ref scanner.TotalDirs),
                Util.FormatSize(Interlocked.Read(ref scanner.TotalBytes)), p);
        }

        // ----------------------------------------------------------- Cay UI
        void BuildTree()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            var rootTn = new TreeNode(NodeLabel(rootNode, 1.0))
            { Tag = new NodeInfo { F = rootNode, Pct = 1.0 } };
            tree.Nodes.Add(rootTn);
            PopulateChildren(rootTn);
            rootTn.Expand();
            tree.EndUpdate();
        }

        static string NodeLabel(FolderNode f, double pct)
        {
            if (f.AccessDenied)
                return f.Name + "    (không có quyền truy cập)";
            return string.Format("{0}      {1}   ·   {2:0.0}%   ·   {3:N0} tệp",
                f.Name, Util.FormatSize(f.Size), pct * 100.0, f.FileCount);
        }

        void EnsurePopulated(TreeNode tn)
        {
            if (tn.Nodes.Count == 1 && tn.Nodes[0].Tag == null)
                PopulateChildren(tn);
        }

        void PopulateChildren(TreeNode tn)
        {
            var info = tn.Tag as NodeInfo;
            if (info == null) return;
            var f = info.F;
            tn.Nodes.Clear();

            var kids = new List<KeyValuePair<FolderNode, bool>>();
            foreach (var c in f.Children) kids.Add(new KeyValuePair<FolderNode, bool>(c, false));
            if (f.OwnSize > 0 && f.Children.Count > 0)
            {
                kids.Add(new KeyValuePair<FolderNode, bool>(new FolderNode
                {
                    Name = "[Tệp nằm trực tiếp trong thư mục này]",
                    FullPath = f.FullPath,
                    Size = f.OwnSize,
                    FileCount = f.OwnFiles
                }, true));
            }
            kids.Sort(delegate(KeyValuePair<FolderNode, bool> a, KeyValuePair<FolderNode, bool> b)
                { return b.Key.Size.CompareTo(a.Key.Size); });

            var nodes = new List<TreeNode>();
            foreach (var kv in kids)
            {
                var c = kv.Key;
                double pct = f.Size > 0 ? (double)c.Size / f.Size : 0;
                var childTn = new TreeNode(kv.Value
                        ? string.Format("{0}      {1}   ·   {2:0.0}%   ·   {3:N0} tệp",
                            c.Name, Util.FormatSize(c.Size), pct * 100.0, c.FileCount)
                        : NodeLabel(c, pct))
                { Tag = new NodeInfo { F = c, Pct = pct, IsFiles = kv.Value } };
                if (!kv.Value && (c.Children.Count > 0 || (c.OwnSize > 0 && c.Children.Count > 0)))
                    childTn.Nodes.Add(new TreeNode("…"));
                nodes.Add(childTn);
            }
            tn.Nodes.AddRange(nodes.ToArray());
        }

        void JumpToPath(string path)
        {
            if (tree.Nodes.Count == 0) return;
            tabs.SelectedIndex = 0;
            var cur = tree.Nodes[0];
            while (true)
            {
                var info = cur.Tag as NodeInfo;
                if (info == null) break;
                if (string.Equals(info.F.FullPath.TrimEnd('\\'), path.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase)) break;
                EnsurePopulated(cur);
                TreeNode next = null;
                foreach (TreeNode c in cur.Nodes)
                {
                    var ci = c.Tag as NodeInfo;
                    if (ci == null || ci.IsFiles) continue;
                    string p = ci.F.FullPath.TrimEnd('\\');
                    if (string.Equals(p, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                    { next = c; break; }
                }
                if (next == null) break;
                cur.Expand();
                cur = next;
            }
            tree.SelectedNode = cur;
            cur.EnsureVisible();
            tree.Focus();
        }

        // -------------------------------------------------------- Tong quan
        void BuildSummary(bool canceled)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat("Đường dẫn quét:  {0}{1}\r\n",
                scanPath, canceled ? "   (đã dừng — kết quả một phần)" : "");
            if (driveTotal > 0)
                sb.AppendFormat("Ổ đĩa:  tổng {0}  ·  đã dùng {1}  ·  còn trống {2}\r\n",
                    Util.FormatSize(driveTotal),
                    Util.FormatSize(driveTotal - driveFree),
                    Util.FormatSize(driveFree));
            sb.AppendFormat("Đã quét:  {0}  ·  {1:N0} tệp  ·  {2:N0} thư mục",
                Util.FormatSize(rootNode.Size), rootNode.FileCount, rootNode.DirCount);
            if (scanner.DeniedDirs > 0)
                sb.AppendFormat("  ·  {0:N0} thư mục bị từ chối truy cập", scanner.DeniedDirs);
            sb.AppendLine();
            double secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            sb.AppendFormat("Thời gian:  {0:0.0} giây   (~{1:N0} tệp/giây)",
                secs, rootNode.FileCount / secs);
            lblInfo.Text = sb.ToString();
            driveBar.Invalidate();

            // Top thu muc: duyet toan bo cay bang stack
            var all = new List<FolderNode>();
            var stack = new Stack<FolderNode>();
            foreach (var c in rootNode.Children) stack.Push(c);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                all.Add(n);
                foreach (var c in n.Children) stack.Push(c);
            }
            all.Sort(delegate(FolderNode a, FolderNode b) { return b.Size.CompareTo(a.Size); });

            lvFolders.BeginUpdate();
            lvFolders.Items.Clear();
            foreach (var n in all.Take(15))
            {
                var it = new ListViewItem(n.FullPath) { Tag = n.FullPath, ToolTipText = n.FullPath };
                it.SubItems.Add(Util.FormatSize(n.Size));
                it.SubItems.Add(rootNode.Size > 0
                    ? string.Format("{0:0.0}%", 100.0 * n.Size / rootNode.Size) : "-");
                lvFolders.Items.Add(it);
            }
            lvFolders.EndUpdate();

            List<FileEntry> tops;
            lock (scanner.TopFiles) { tops = new List<FileEntry>(scanner.TopFiles); }
            tops.Sort(delegate(FileEntry a, FileEntry b) { return b.Size.CompareTo(a.Size); });
            lvBigFiles.BeginUpdate();
            lvBigFiles.Items.Clear();
            foreach (var fe in tops.Take(15))
            {
                var it = new ListViewItem(fe.Path) { Tag = fe.Path, ToolTipText = fe.Path };
                it.SubItems.Add(Util.FormatSize(fe.Size));
                lvBigFiles.Items.Add(it);
            }
            lvBigFiles.EndUpdate();

            lvExt.BeginUpdate();
            lvExt.Items.Clear();
            foreach (var kv in scanner.ExtStats.ToArray()
                .OrderByDescending(delegate(KeyValuePair<string, ExtStat> k) { return k.Value.Size; })
                .Take(15))
            {
                var it = new ListViewItem(kv.Key);
                it.SubItems.Add(kv.Value.Count.ToString("N0"));
                it.SubItems.Add(Util.FormatSize(kv.Value.Size));
                lvExt.Items.Add(it);
            }
            lvExt.EndUpdate();
        }

        void DriveBar_Paint(object sender, PaintEventArgs e)
        {
            var r = driveBar.ClientRectangle;
            r.Inflate(-14, -6);
            if (r.Width < 20 || r.Height < 8) return;
            var g = e.Graphics;

            long total = driveTotal > 0 ? driveTotal : (rootNode != null ? rootNode.Size : 0);
            if (total <= 0)
            {
                using (var br = new SolidBrush(Color.FromArgb(225, 232, 240)))
                    g.FillRectangle(br, r);
                return;
            }
            long used = driveTotal > 0 ? driveTotal - driveFree : total;
            double fUsed = Math.Min(1.0, (double)used / total);

            using (var back = new SolidBrush(Color.FromArgb(228, 236, 244))) g.FillRectangle(back, r);
            var usedRect = new Rectangle(r.X, r.Y, (int)(r.Width * fUsed), r.Height);
            Color c = fUsed >= 0.9 ? Aero.BarRed : (fUsed >= 0.75 ? Aero.BarOrange : Aero.BarBlue);
            Aero.GlossyBar(g, usedRect, c);
            using (var pen = new Pen(Aero.ToolbarBorder)) g.DrawRectangle(pen, r);

            string txt = string.Format("Đã dùng {0:0.0}%", fUsed * 100.0);
            TextRenderer.DrawText(g, txt, Font, r, Aero.TextMain,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---------------------------------------------------------- Hinh anh
        void PrepareImages()
        {
            allImages = scanner.Images.ToList();
            allImages.Sort(delegate(FileEntry a, FileEntry b) { return b.Size.CompareTo(a.Size); });
            SaveImageCache(scanPath, allImages);
            ApplyImageFilter();
        }

        FileEntry SelectedImage()
        {
            int idx = pageStart + grid.SelectedIndex;
            return grid.SelectedIndex >= 0 && idx < filteredImages.Count ? filteredImages[idx] : null;
        }

        void ApplyImageFilter()
        {
            long min = MinSizes[Math.Max(0, cboMinSize.SelectedIndex)];
            int mt = cboMediaType.SelectedIndex; // 0: ca hai, 1: chi anh, 2: chi video
            filteredImages = allImages.Where(delegate(FileEntry f)
            {
                if (f.Size < min) return false;
                if (mt == 1) return !f.IsVideo;
                if (mt == 2) return f.IsVideo;
                return true;
            }).ToList();
            ShowPage(0);
        }

        int PageSize { get { return (int)numPageSize.Value; } }
        int PageCount
        {
            get { return Math.Max(1, (filteredImages.Count + PageSize - 1) / PageSize); }
        }

        void ShowPage(int p)
        {
            p = Math.Max(0, Math.Min(PageCount - 1, p));
            pageIndex = p;
            pageStart = p * PageSize;
            grid.SetItems(filteredImages.Skip(pageStart).Take(PageSize).ToList());

            pageGuard = true;
            numPage.Maximum = PageCount;
            numPage.Value = p + 1;
            pageGuard = false;
            lblPageTotal.Text = string.Format("/ {0:N0} trang", PageCount);
            btnPrev.Enabled = p > 0;
            btnNext.Enabled = p < PageCount - 1;
            UpdateImgCount();
        }

        void UpdateImgCount()
        {
            if (filteredImages.Count == 0)
            {
                lblImgCount.Text = "0 mục.";
                return;
            }
            int last = Math.Min(filteredImages.Count, pageStart + PageSize);
            lblImgCount.Text = string.Format("{0:N0} mục · hiển thị {1:N0}–{2:N0}",
                filteredImages.Count, pageStart + 1, last);
        }

        // ------------------------------------------------- Cache danh sach anh
        // Luu danh sach anh cua lan quet vao %TEMP% de lan sau mo app nap lai ngay
        static string ImageCacheFile(string path)
        {
            string key = path.TrimEnd('\\').ToLowerInvariant();
            uint h = 2166136261;
            foreach (char c in key) { h ^= c; h *= 16777619; }
            return Path.Combine(Path.GetTempPath(), "DiskUsage_img_" + h.ToString("x8") + ".txt");
        }

        static void SaveImageCache(string path, List<FileEntry> images)
        {
            var snapshot = images;
            Task.Run(delegate
            {
                try
                {
                    using (var w = new StreamWriter(ImageCacheFile(path), false, System.Text.Encoding.UTF8))
                    {
                        w.WriteLine(path);
                        w.WriteLine(DateTime.Now.Ticks);
                        foreach (var fe in snapshot) w.WriteLine(fe.Size + "|" + fe.Path);
                    }
                }
                catch { }
            });
        }

        void TryLoadImageCache()
        {
            string path = ResolveScanPath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string file = ImageCacheFile(path);
                if (!File.Exists(file)) return;
                var list = new List<FileEntry>();
                DateTime when = DateTime.MinValue;
                using (var r = new StreamReader(file, System.Text.Encoding.UTF8))
                {
                    r.ReadLine(); // duong dan goc
                    long ticks;
                    if (long.TryParse(r.ReadLine(), out ticks)) when = new DateTime(ticks);
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        int sep = line.IndexOf('|');
                        if (sep <= 0) continue;
                        long sz;
                        if (!long.TryParse(line.Substring(0, sep), out sz)) continue;
                        string p = line.Substring(sep + 1);
                        list.Add(new FileEntry { Path = p, Size = sz, IsVideo = Scanner.IsVideoPath(p) });
                    }
                }
                if (list.Count == 0) return;
                allImages = list; // da sap xep giam dan khi luu
                ApplyImageFilter();
                lblStatus.Text = string.Format(
                    "Đã nạp {0:N0} mục ảnh/video từ cache{1} — bấm Quét để cập nhật dữ liệu mới.",
                    list.Count,
                    when > DateTime.MinValue ? " (quét lúc " + when.ToString("dd/MM/yyyy HH:mm") + ")" : "");
            }
            catch { }
        }

        // ------------------------------------------------------------- Utils
        static void TryOpenExplorer(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        static void TrySelectInExplorer(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }
    }

    // ------------------------------------------------------------- ThumbGrid
    // Luoi anh "ao": hien thi hang chuc nghin anh khong lag. Danh sach chi giu
    // duong dan (rat nhe); thumbnail chi duoc giai ma cho vung dang nhin thay
    // (+ prefetch vai hang), cache LRU co gioi han va tu giai phong bitmap
    // ngoai vung nhin. Cuon toi dau, anh tu tai toi do.
    class ThumbGrid : Control
    {
        public event Action<int> ItemActivate;

        const int CellW = 128, CellH = 152, Thumb = 110;
        const int CacheMax = 900; // so thumbnail giu trong RAM (~40 MB)
        // it worker + uu tien thap: khong tranh CPU voi luong UI -> khong treo app
        static readonly int MaxWorkers = Math.Max(1, Math.Min(3, Environment.ProcessorCount - 2));

        List<FileEntry> items = new List<FileEntry>();
        readonly Dictionary<int, Bitmap> cache = new Dictionary<int, Bitmap>();
        readonly LinkedList<int> lru = new LinkedList<int>();
        readonly Dictionary<int, LinkedListNode<int>> lruMap = new Dictionary<int, LinkedListNode<int>>();
        readonly HashSet<int> pending = new HashSet<int>();
        // thumbnail giai ma xong duoc gom vao day, UI timer nhat theo lo
        readonly Queue<KeyValuePair<int, Bitmap>> done = new Queue<KeyValuePair<int, Bitmap>>();
        readonly object sync = new object();
        int workers;
        int gen;            // tang moi lan SetItems -> huy ket qua tai tre
        volatile int anchor; // index dau vung nhin, de uu tien tai & don cache

        int selected = -1;
        int scrollY;
        VScrollBar vbar;
        ToolTip tip = new ToolTip();
        int tipIndex = -1;
        System.Windows.Forms.Timer pump;

        public ThumbGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.White;
            vbar = new VScrollBar { Dock = DockStyle.Right };
            vbar.ValueChanged += delegate { scrollY = vbar.Value; Invalidate(); };
            Controls.Add(vbar);
            // gom ket qua tu worker roi ve mot lan, tranh doi BeginInvoke lam nghen UI
            pump = new System.Windows.Forms.Timer { Interval = 80 };
            pump.Tick += delegate { DrainDone(); };
            pump.Start();
        }

        public int SelectedIndex { get { return selected; } }

        public void SetItems(List<FileEntry> list)
        {
            lock (sync)
            {
                gen++;
                pending.Clear();
                while (done.Count > 0)
                {
                    var kv = done.Dequeue();
                    if (kv.Value != null) kv.Value.Dispose();
                }
            }
            items = list ?? new List<FileEntry>();
            selected = -1;
            tipIndex = -1;
            anchor = 0;
            foreach (var b in cache.Values) if (b != null) b.Dispose();
            cache.Clear(); lru.Clear(); lruMap.Clear();
            scrollY = 0;
            UpdateScroll();
            Invalidate();
        }

        // ------------------------------------------------------------ Layout
        int ColsCount
        {
            get
            {
                int w = ClientSize.Width - vbar.Width - 8;
                return Math.Max(1, w / CellW);
            }
        }

        int TotalHeight
        {
            get
            {
                int rows = (items.Count + ColsCount - 1) / ColsCount;
                return rows * CellH + 8;
            }
        }

        void UpdateScroll()
        {
            int total = TotalHeight;
            int page = Math.Max(1, ClientSize.Height);
            int max = Math.Max(0, total - page);
            if (scrollY > max) scrollY = max;
            vbar.Minimum = 0;
            vbar.Maximum = Math.Max(0, total - 1);
            vbar.LargeChange = page;
            vbar.SmallChange = CellH / 2;
            vbar.Enabled = max > 0;
            if (vbar.Value != scrollY) vbar.Value = scrollY;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScroll();
        }

        Rectangle CellRect(int i)
        {
            int cols = ColsCount;
            return new Rectangle(4 + (i % cols) * CellW, 4 + (i / cols) * CellH - scrollY, CellW, CellH);
        }

        void VisibleRange(out int lo, out int hi)
        {
            if (items.Count == 0) { lo = 0; hi = -1; return; }
            int cols = ColsCount;
            int rowTop = Math.Max(0, (scrollY - 4) / CellH);
            int rowBot = Math.Max(0, (scrollY + ClientSize.Height - 4) / CellH);
            lo = Math.Min(rowTop * cols, items.Count - 1);
            hi = Math.Min(items.Count - 1, (rowBot + 1) * cols - 1);
            anchor = lo;
        }

        // -------------------------------------------------------------- Ve
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "Không có ảnh/video nào.", Font, ClientRectangle,
                    Aero.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int lo, hi;
            VisibleRange(out lo, out hi);
            for (int i = lo; i <= hi; i++) DrawCell(g, i);
            RequestRange(lo, hi);
        }

        void DrawCell(Graphics g, int i)
        {
            var cell = CellRect(i);
            var thumbRect = new Rectangle(cell.X + (CellW - Thumb) / 2, cell.Y + 4, Thumb, Thumb);
            var fe = items[i];

            if (i == selected) Aero.DrawSelection(g, cell);

            Bitmap bmp;
            bool have = cache.TryGetValue(i, out bmp);
            if (have && bmp != null)
            {
                g.DrawImage(bmp, thumbRect);
                Touch(i);
            }
            else
            {
                using (var br = new SolidBrush(Color.FromArgb(238, 242, 246)))
                    g.FillRectangle(br, thumbRect);
                // "…" = dang tai; loi doc: video hien "▶", anh hien "✕"
                TextRenderer.DrawText(g, have ? (fe.IsVideo ? "▶" : "✕") : "…",
                    Font, thumbRect, Aero.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            using (var pen = new Pen(Color.FromArgb(214, 222, 230)))
                g.DrawRectangle(pen, thumbRect);

            // badge "phat" cho video
            if (fe.IsVideo && have && bmp != null)
            {
                var badge = new Rectangle(thumbRect.X + 5, thumbRect.Bottom - 25, 20, 20);
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bbr = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    g.FillEllipse(bbr, badge);
                var tri = new Point[]
                {
                    new Point(badge.X + 8, badge.Y + 5),
                    new Point(badge.X + 8, badge.Y + 15),
                    new Point(badge.X + 16, badge.Y + 10)
                };
                using (var wbr = new SolidBrush(Color.White))
                    g.FillPolygon(wbr, tri);
                g.SmoothingMode = old;
            }

            var nameRect = new Rectangle(cell.X + 3, thumbRect.Bottom + 3, CellW - 6, 16);
            TextRenderer.DrawText(g, Path.GetFileName(fe.Path), Font, nameRect, Aero.TextMain,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var sizeRect = new Rectangle(cell.X + 3, nameRect.Bottom, CellW - 6, 15);
            TextRenderer.DrawText(g, Util.FormatSize(fe.Size), Font, sizeRect, Aero.TextDim,
                TextFormatFlags.HorizontalCenter);
        }

        // ----------------------------------------------------- Tai thumbnail
        void RequestRange(int lo, int hi)
        {
            if (hi < lo) return;
            int pre = ColsCount * 3; // prefetch vai hang truoc/sau de cuon muot
            int a = Math.Max(0, lo - pre), b = Math.Min(items.Count - 1, hi + pre);
            lock (sync)
            {
                for (int i = a; i <= b; i++)
                    if (!cache.ContainsKey(i)) pending.Add(i);
                while (workers < MaxWorkers && pending.Count > 0)
                {
                    workers++;
                    var t = new Thread(PumpLoader)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal
                    };
                    t.Start();
                }
            }
        }

        void PumpLoader()
        {
            while (true)
            {
                int idx = -1, g;
                FileEntry fe = null;
                lock (sync)
                {
                    g = gen;
                    if (pending.Count > 0)
                    {
                        int anc = anchor, best = int.MaxValue;
                        foreach (int p in pending)
                        {
                            int d = Math.Abs(p - anc);
                            if (d < best) { best = d; idx = p; }
                        }
                        pending.Remove(idx);
                        var list = items;
                        if (idx >= 0 && idx < list.Count) fe = list[idx];
                    }
                    if (fe == null) { workers--; return; }
                }

                var bmp = fe.IsVideo
                    ? ShellThumb(fe.Path, Thumb, Thumb)   // video: nho Windows Shell tao thumbnail
                    : MakeThumb(fe.Path, Thumb, Thumb);
                lock (sync)
                {
                    if (g != gen) { if (bmp != null) bmp.Dispose(); }
                    else done.Enqueue(new KeyValuePair<int, Bitmap>(idx, bmp));
                }
            }
        }

        // Chay tren luong UI (timer): nhat ca lo thumbnail da xong, ve mot lan
        void DrainDone()
        {
            List<KeyValuePair<int, Bitmap>> batch = null;
            lock (sync)
            {
                if (done.Count > 0)
                {
                    batch = new List<KeyValuePair<int, Bitmap>>(done);
                    done.Clear();
                }
            }
            if (batch == null) return;
            foreach (var kv in batch) Store(kv.Key, kv.Value);
            Invalidate();
        }

        // Cache LRU: giu toi da CacheMax thumbnail, don bot cai o xa vung nhin
        void Store(int idx, Bitmap bmp)
        {
            Bitmap old;
            if (cache.TryGetValue(idx, out old) && old != null) old.Dispose();
            cache[idx] = bmp;
            LinkedListNode<int> node;
            if (lruMap.TryGetValue(idx, out node)) lru.Remove(node);
            lruMap[idx] = lru.AddFirst(idx);
            if (cache.Count > CacheMax) Evict();
        }

        void Touch(int idx)
        {
            LinkedListNode<int> node;
            if (lruMap.TryGetValue(idx, out node) && node != lru.First)
            {
                lru.Remove(node);
                lruMap[idx] = lru.AddFirst(idx);
            }
        }

        void Evict()
        {
            int anc = anchor, keep = ColsCount * 8;
            var node = lru.Last;
            while (cache.Count > CacheMax - 150 && node != null)
            {
                var prev = node.Previous;
                int i = node.Value;
                if (Math.Abs(i - anc) > keep)
                {
                    lru.Remove(node);
                    lruMap.Remove(i);
                    Bitmap b;
                    if (cache.TryGetValue(i, out b) && b != null) b.Dispose();
                    cache.Remove(i);
                }
                node = prev;
            }
        }

        // --------------------------- Thumbnail video qua Windows Shell (nhu Explorer)
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE_ { public int cx, cy; public SIZE_(int x, int y) { cx = x; cy = y; } }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE_ size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
            ref Guid riid, out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        const int SIIGBF_BIGGERSIZEOK = 0x01;

        static Bitmap ShellThumb(string path, int w, int h)
        {
            IShellItemImageFactory factory = null;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0
                    || factory == null)
                    return null;
                IntPtr hbm;
                if (factory.GetImage(new SIZE_(w, h), SIIGBF_BIGGERSIZEOK, out hbm) != 0
                    || hbm == IntPtr.Zero)
                    return null;
                try
                {
                    using (var raw = Image.FromHbitmap(hbm))
                    {
                        var bmp = new Bitmap(w, h);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.FromArgb(24, 28, 34));
                            g.InterpolationMode = InterpolationMode.Bilinear;
                            double scale = Math.Min((double)w / raw.Width, (double)h / raw.Height);
                            int dw = Math.Max(1, (int)(raw.Width * scale));
                            int dh = Math.Max(1, (int)(raw.Height * scale));
                            g.DrawImage(raw, (w - dw) / 2, (h - dh) / 2, dw, dh);
                        }
                        return bmp;
                    }
                }
                finally { DeleteObject(hbm); }
            }
            catch { return null; }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        const int PropertyTagThumbnailData = 0x501B;

        static Bitmap MakeThumb(string path, int w, int h)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var img = Image.FromStream(fs, false, false))
                {
                    // Uu tien thumbnail EXIF nhung san (vai KB) thay vi giai ma
                    // ca anh goc nhieu MB -> nhanh hon nhieu voi anh chup
                    Image src = img;
                    Image embedded = null;
                    try
                    {
                        foreach (int id in img.PropertyIdList)
                        {
                            if (id != PropertyTagThumbnailData) continue;
                            var pi = img.GetPropertyItem(PropertyTagThumbnailData);
                            if (pi != null && pi.Value != null && pi.Value.Length > 0)
                                embedded = Image.FromStream(new MemoryStream(pi.Value));
                            break;
                        }
                    }
                    catch { }
                    if (embedded != null) src = embedded;

                    var bmp = new Bitmap(w, h);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(24, 28, 34));
                        g.InterpolationMode = InterpolationMode.Bilinear;
                        double scale = Math.Min((double)w / src.Width, (double)h / src.Height);
                        int dw = Math.Max(1, (int)(src.Width * scale));
                        int dh = Math.Max(1, (int)(src.Height * scale));
                        g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
                    }
                    if (embedded != null) embedded.Dispose();
                    return bmp;
                }
            }
            catch { return null; }
        }

        // ------------------------------------------------------ Chuot & phim
        int HitTest(Point p)
        {
            if (items.Count == 0) return -1;
            int cols = ColsCount;
            if (p.X < 4 || p.X >= 4 + cols * CellW) return -1;
            int col = (p.X - 4) / CellW;
            int row = (p.Y + scrollY - 4) / CellH;
            if (row < 0) return -1;
            int i = row * cols + col;
            return i < items.Count ? i : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = HitTest(e.Location);
            if (i != selected) { selected = i; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = HitTest(e.Location);
            if (i >= 0 && ItemActivate != null) ItemActivate(i);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(scrollY - Math.Sign(e.Delta) * CellH);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.Location);
            if (i != tipIndex)
            {
                tipIndex = i;
                tip.SetToolTip(this, i >= 0
                    ? items[i].Path + "\r\n" + Util.FormatSize(items[i].Size) : "");
            }
        }

        void ScrollTo(int y)
        {
            int max = Math.Max(0, TotalHeight - ClientSize.Height);
            y = Math.Max(0, Math.Min(max, y));
            if (y == scrollY) return;
            scrollY = y;
            if (vbar.Value != y) vbar.Value = y; else Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End:
                case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (items.Count == 0) return;
            int cols = ColsCount;
            int rowsPage = Math.Max(1, ClientSize.Height / CellH);
            int i = selected < 0 ? 0 : selected;
            switch (e.KeyCode)
            {
                case Keys.Left: i--; break;
                case Keys.Right: i++; break;
                case Keys.Up: i -= cols; break;
                case Keys.Down: i += cols; break;
                case Keys.PageUp: i -= cols * rowsPage; break;
                case Keys.PageDown: i += cols * rowsPage; break;
                case Keys.Home: i = 0; break;
                case Keys.End: i = items.Count - 1; break;
                case Keys.Enter:
                    if (selected >= 0 && ItemActivate != null) ItemActivate(selected);
                    return;
                default: return;
            }
            e.Handled = true;
            i = Math.Max(0, Math.Min(items.Count - 1, i));
            if (i != selected)
            {
                selected = i;
                EnsureVisible(i);
                Invalidate();
            }
        }

        void EnsureVisible(int i)
        {
            var r = CellRect(i);
            if (r.Top < 4) ScrollTo(scrollY + r.Top - 4);
            else if (r.Bottom > ClientSize.Height) ScrollTo(scrollY + r.Bottom - ClientSize.Height + 4);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pump.Stop();
                pump.Dispose();
                lock (sync)
                {
                    gen++;
                    pending.Clear();
                    while (done.Count > 0)
                    {
                        var kv = done.Dequeue();
                        if (kv.Value != null) kv.Value.Dispose();
                    }
                }
                foreach (var b in cache.Values) if (b != null) b.Dispose();
                cache.Clear(); lru.Clear(); lruMap.Clear();
                tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ----------------------------------------------------------- PreviewForm
    class PreviewForm : Form
    {
        readonly List<FileEntry> list;
        int idx;
        PictureBox pb;
        Label lblInfo;

        public PreviewForm(List<FileEntry> images, int index)
        {
            list = images;
            idx = index;

            Text = "Xem ảnh";
            Size = new Size(1000, 720);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 28, 34);
            KeyPreview = true;

            pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                                  BackColor = Color.FromArgb(24, 28, 34) };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40,
                                     BackColor = Color.FromArgb(38, 44, 52) };
            var btnPrev = NavButton("◀", 8);
            var btnNext = NavButton("▶", 52);
            var btnOpen = NavButton("Mở tệp", 96); btnOpen.Width = 80;
            var btnExp = NavButton("Explorer", 184); btnExp.Width = 80;
            lblInfo = new Label
            {
                ForeColor = Color.Gainsboro, BackColor = Color.Transparent,
                AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 12, 0)
            };
            btnPrev.Click += delegate { Nav(-1); };
            btnNext.Click += delegate { Nav(1); };
            btnOpen.Click += delegate { try { Process.Start(list[idx].Path); } catch { } };
            btnExp.Click += delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + list[idx].Path + "\""); } catch { }
            };

            bottom.Controls.Add(lblInfo);
            bottom.Controls.Add(btnPrev);
            bottom.Controls.Add(btnNext);
            bottom.Controls.Add(btnOpen);
            bottom.Controls.Add(btnExp);

            Controls.Add(pb);
            Controls.Add(bottom);

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Left) Nav(-1);
                else if (e.KeyCode == Keys.Right) Nav(1);
            };
            pb.DoubleClick += delegate { try { Process.Start(list[idx].Path); } catch { } };

            LoadImage();
        }

        static Button NavButton(string text, int x)
        {
            return new Button
            {
                Text = text, Location = new Point(x, 6), Size = new Size(38, 28),
                FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                BackColor = Color.FromArgb(58, 66, 78), TabStop = false
            };
        }

        void Nav(int d)
        {
            int n = idx + d;
            if (n < 0 || n >= list.Count) return;
            idx = n;
            LoadImage();
        }

        void LoadImage()
        {
            var old = pb.Image;
            pb.Image = null;
            if (old != null) old.Dispose();
            var fe = list[idx];
            if (fe.IsVideo)
            {
                lblInfo.Text = string.Format(
                    "🎬 {0}   ·   video   ·   {1}   ·   [{2}/{3}]  —  nháy đúp hoặc bấm \"Mở tệp\" để phát",
                    Path.GetFileName(fe.Path), Util.FormatSize(fe.Size), idx + 1, list.Count);
                Text = fe.Path;
                return;
            }
            try
            {
                var bytes = File.ReadAllBytes(fe.Path);
                var img = Image.FromStream(new MemoryStream(bytes));
                pb.Image = img;
                lblInfo.Text = string.Format("{0}   ·   {1}×{2}   ·   {3}   ·   [{4}/{5}]",
                    Path.GetFileName(fe.Path), img.Width, img.Height,
                    Util.FormatSize(fe.Size), idx + 1, list.Count);
                Text = fe.Path;
            }
            catch
            {
                lblInfo.Text = "Không đọc được ảnh: " + fe.Path;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (pb.Image != null) pb.Image.Dispose();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ Util
    static class Util
    {
        public static string FormatSize(long b)
        {
            if (b >= 1099511627776L) return string.Format("{0:0.##} TB", b / 1099511627776.0);
            if (b >= 1073741824L) return string.Format("{0:0.##} GB", b / 1073741824.0);
            if (b >= 1048576L) return string.Format("{0:0.##} MB", b / 1048576.0);
            if (b >= 1024L) return string.Format("{0:0.#} KB", b / 1024.0);
            return b + " B";
        }
    }

    // -------------------------------------------------------- FolderPicker
    // Hop thoai chon thu muc kieu Vista qua COM IFileOpenDialog: co thanh dia
    // chi va o "Folder:" cho phep go/dan truc tiep duong dan (khac han
    // FolderBrowserDialog cu chi co cay thu muc).
    static class FolderPicker
    {
        const uint FOS_PICKFOLDERS = 0x20;
        const uint FOS_FORCEFILESYSTEM = 0x40;
        const uint FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        // true = da dung duoc hop thoai Vista (path = ket qua, "" = Huy)
        // false = OS khong ho tro -> goi y dung fallback
        public static bool TryPick(IntPtr owner, string initialPath, string title, out string path)
        {
            path = "";
            IFileOpenDialog dlg = null;
            try { dlg = (IFileOpenDialog)new FileOpenDialogRCW(); }
            catch { return false; }

            try
            {
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);

                if (!string.IsNullOrEmpty(initialPath))
                {
                    IShellItem start;
                    if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero,
                            typeof(IShellItem).GUID, out start) == 0 && start != null)
                    {
                        dlg.SetFolder(start);
                        Marshal.ReleaseComObject(start);
                    }
                }

                int hr = dlg.Show(owner);
                if (hr != 0) return true; // Huy hoac dong -> path = ""

                IShellItem result;
                if (dlg.GetResult(out result) == 0 && result != null)
                {
                    string p;
                    result.GetDisplayName(SIGDN_FILESYSPATH, out p);
                    Marshal.ReleaseComObject(result);
                    path = p ?? "";
                }
                return true;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(dlg); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName,
                [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        // Khai bao phang toan bo vtable (IModalWindow -> IFileDialog ->
        // IFileOpenDialog) theo dung thu tu; chi vai method duoc dung.
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            [PreserveSig] int SetFileTypeIndex(uint iFileType);
            [PreserveSig] int GetFileTypeIndex(out uint piFileType);
            [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
            [PreserveSig] int Unadvise(uint dwCookie);
            [PreserveSig] int SetOptions(uint fos);
            [PreserveSig] int GetOptions(out uint pfos);
            [PreserveSig] int SetDefaultFolder(IShellItem psi);
            [PreserveSig] int SetFolder(IShellItem psi);
            [PreserveSig] int GetFolder(out IShellItem ppsi);
            [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
            [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            [PreserveSig] int GetResult(out IShellItem ppsi);
            [PreserveSig] int AddPlace(IShellItem psi, int fdap);
            [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            [PreserveSig] int Close(int hr);
            [PreserveSig] int SetClientGuid(ref Guid guid);
            [PreserveSig] int ClearClientData();
            [PreserveSig] int SetFilter(IntPtr pFilter);
            [PreserveSig] int GetResults(out IntPtr ppenum);
            [PreserveSig] int GetSelectedItems(out IntPtr ppsai);
        }
    }
}
