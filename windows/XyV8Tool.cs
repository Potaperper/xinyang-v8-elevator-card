// 鑫洋V8 电梯卡工具 —— 单文件 WinForms (C# 5 / .NET Framework 4.x，Windows 自带，无需任何依赖)
// 编译： csc /target:winexe /out:鑫洋V8电梯卡工具.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll XyV8Tool.cs
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

// ============================================================ 算法核心
static class Algo
{
    public const int OFF_CHK = 64, OFF_DHI = 85, OFF_DLO = 86;
    public const int FLOOR_ABS = 73, FLOOR_LEN = 7;
    public const int S10_OFF = 640, S10_LEN = 48;
    public const byte MASK_LO = 0xD4;      // 日期低字节的固定掩码（老版鑫洋V8）

    public static byte Bitrev(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++) if (((b >> i) & 1) != 0) r |= 1 << (7 - i);
        return (byte)r;
    }
    public static int Pack(int y, int m, int d) { return ((y - 2000) << 9) | (m << 5) | d; }

    public static byte[] SectorKey(byte[] uid)
    {
        return new byte[]{
            (byte)(uid[0]^0xFD), (byte)(uid[1]^0x36), (byte)(uid[2]^0x2B),
            (byte)(uid[3]^0xC3), (byte)(uid[1]^0x41), (byte)(uid[2]^0x68) };
    }
    public static byte[] SectorKeyFromUidHex(string hex)
    {
        hex = hex.Replace(" ", "").Replace(":", "").ToUpper();
        if (hex.Length != 8) throw new Exception("UID 必须是 4 字节 = 8 个十六进制字符，例如 11223344");
        byte[] u = new byte[4];
        for (int i = 0; i < 4; i++) u[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return SectorKey(u);
    }

    // 楼层：位号 = 楼层号 - 1（位0=1楼）；base0 时 位0=负一层、位号=楼层号
    public static byte[] FloorsToBitmap(string spec, bool base0)
    {
        long v = 0;
        foreach (string raw in spec.Replace("，", ",").Split(','))
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;
            if (p.ToUpper() == "B1" || p == "-1")
            {
                if (!base0) throw new Exception("当前约定是「位0=1楼」，不支持负一层；要处理负一层请勾选旧约定");
                v |= 1; continue;
            }
            if (p.Contains("-") && p.TrimStart('-').Contains("-"))
            {
                int k = p.IndexOf('-', 1);
                int a = int.Parse(p.Substring(0, k)), b = int.Parse(p.Substring(k + 1));
                for (int f = a; f <= b; f++) v |= BitOf(f, base0);
            }
            else v |= BitOf(int.Parse(p), base0);
        }
        byte[] bm = new byte[FLOOR_LEN];
        for (int i = 0; i < FLOOR_LEN; i++) bm[FLOOR_LEN - 1 - i] = (byte)((v >> (8 * i)) & 0xFF);
        return bm;
    }
    static long BitOf(int floor, bool base0)
    {
        int bit = base0 ? floor : floor - 1;
        if (bit < 0 || bit >= FLOOR_LEN * 8) throw new Exception("楼层 " + floor + " 超出范围");
        return 1L << bit;
    }
    public static string BitmapToFloors(byte[] bm, bool base0)
    {
        long v = 0;
        for (int i = 0; i < bm.Length; i++) v = (v << 8) | bm[i];
        StringBuilder sb = new StringBuilder();
        for (int bit = 0; bit < FLOOR_LEN * 8; bit++)
        {
            if (((v >> bit) & 1) == 0) continue;
            string f = (base0 && bit == 0) ? "B1" : (bit + (base0 ? 0 : 1)).ToString();
            if (sb.Length > 0) sb.Append(",");
            sb.Append(f);
        }
        return sb.Length == 0 ? "(无)" : sb.ToString();
    }

    public static string Hex(byte[] b)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < b.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
        return sb.ToString();
    }
    public static byte[] ParseHex(string s)
    {
        s = s.Replace(" ", "").Replace(",", "").Replace("-", "").Trim();
        if (s.Length % 2 != 0) throw new Exception("十六进制长度必须是偶数");
        byte[] r = new byte[s.Length / 2];
        for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return r;
    }
    public static byte[] Slice(byte[] b, int off, int len)
    {
        byte[] r = new byte[len]; Array.Copy(b, off, r, 0, len); return r;
    }

    public class Edit { public int Off; public byte[] OldPt; public byte[] NewPt; }

    /// 一次改多个字段：每个字节的密文增量 = bitrev(旧明文 ^ 新明文)；校验码增量 = 全部增量的异或
    public static byte[] ApplyEdits(byte[] data, System.Collections.Generic.List<Edit> edits, out int totalDelta)
    {
        return ApplyEdits(data, edits, out totalDelta, true);
    }
    /// updateChecksum=false 时只改字段本身、不动校验码（给"只算数"的手动模式用，外层缓冲不一定是整张卡）
    public static byte[] ApplyEdits(byte[] data, System.Collections.Generic.List<Edit> edits, out int totalDelta, bool updateChecksum)
    {
        byte[] d = (byte[])data.Clone();
        totalDelta = 0;
        foreach (Edit e in edits)
        {
            if (e.OldPt.Length != e.NewPt.Length) throw new Exception("新旧明文字节数不一致");
            for (int i = 0; i < e.OldPt.Length; i++)
            {
                byte delta = Bitrev((byte)(e.OldPt[i] ^ e.NewPt[i]));
                if (delta != 0) { d[e.Off + i] ^= delta; totalDelta ^= delta; }
            }
        }
        if (updateChecksum) d[OFF_CHK] ^= (byte)totalDelta;
        return d;
    }

    public static byte[] ClearRolling(byte[] data, out int cleared)
    {
        byte[] d = (byte[])data.Clone();
        cleared = 0;
        for (int i = 0; i < S10_LEN; i++) { if (d[S10_OFF + i] != 0) { cleared++; d[S10_OFF + i] = 0; } }
        return d;
    }

    /// 由日期低字节密文反解 月低3位 和 日
    public static void ReadDayMonth(byte[] data, out int monthLow3, out int day)
    {
        byte pt = Bitrev((byte)(data[OFF_DLO] ^ MASK_LO));
        monthLow3 = pt >> 5; day = pt & 0x1F;
    }

    /// 月份的第 4 位（决定 1~8 还是 9~12）在高字节里、被每卡密钥流加密，读不出来。
    /// 所以只能给出候选：低3位 m3 -> {m3, m3+8}（m3=0 时只能是 8），再用"日"筛掉不存在的日期。
    public static int[] MonthCandidates(int m3, int day)
    {
        System.Collections.Generic.List<int> c = new System.Collections.Generic.List<int>();
        if (m3 == 0) c.Add(8);
        else { c.Add(m3); if (m3 + 8 <= 12) c.Add(m3 + 8); }
        System.Collections.Generic.List<int> ok = new System.Collections.Generic.List<int>();
        foreach (int m in c) if (DayValid(m, day)) ok.Add(m);
        return ok.Count > 0 ? ok.ToArray() : c.ToArray();   // 万一被筛空，退回原始候选
    }
    public static bool IsRealDate(int m, int d) { return DayValid(m, d); }
    static bool DayValid(int m, int d)
    {
        int[] dm = new int[] { 31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };  // 2月按29放宽
        return m >= 1 && m <= 12 && d >= 1 && d <= dm[m - 1];
    }
    public static string JoinInts(int[] a, string sep)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(sep); sb.Append(a[i]); }
        return sb.ToString();
    }
}

// ============================================================ 自检
static class SelfTest
{
    public static int Run(string outDir)
    {
        StringBuilder log = new StringBuilder();
        int ok = 0;
        try { return RunInner(log, outDir); }
        catch (Exception ex)
        {
            log.AppendLine("自检抛异常: " + ex.ToString());
            try { File.WriteAllText(Path.Combine(outDir, "自检结果_selftest.txt"), log.ToString(), new UTF8Encoding(true)); } catch { }
            try { if (Environment.GetEnvironmentVariable("XYV8_NOMSG") == null) MessageBox.Show(log.ToString()); } catch { }
            return 0;
        }
    }
    static int RunInner(StringBuilder log, string outDir)
    {
        int ok = 0;
        int t1;
        byte[] d1 = Algo.ApplyEdits(SampleCard(), E(Algo.OFF_DHI, "306F", "3070"), out t1);
        bool b1 = d1[85] == 0xA3 && d1[86] == 0xA4 && d1[64] == 0x48;
        Log(log, "[1] 示例卡 2024-03-15 -> 2024-03-16  日期字节 {0} {1} 校验 {2}  {3}",
            d1[85].ToString("X2"), d1[86].ToString("X2"), d1[64].ToString("X2"), b1 ? "OK" : "FAIL");
        ok += b1 ? 1 : 0;

        int t2;
        byte[] d2 = Algo.ApplyEdits(SampleCard(), E(Algo.OFF_DHI, "306F", "3ADE"), out t2);
        bool b2 = d2[85] == 0xF3 && d2[86] == 0xD1 && d2[64] == 0x6D;
        Log(log, "[2] 示例卡 2024-03-15 -> 2029-06-30  日期字节 {0} {1} 校验 {2}  {3}",
            d2[85].ToString("X2"), d2[86].ToString("X2"), d2[64].ToString("X2"), b2 ? "OK" : "FAIL");
        ok += b2 ? 1 : 0;

        bool b3 = Algo.Pack(2024, 3, 15) == 0x306F && Algo.Pack(2029, 6, 30) == 0x3ADE;
        Log(log, "[3] 日期打包 2024-03-15 -> 0x306F，2029-06-30 -> 0x3ADE  {0}", b3 ? "OK" : "FAIL");
        ok += b3 ? 1 : 0;

        bool b4 = Algo.Bitrev(0x0A) == 0x50 && Algo.Bitrev(0xB1) == 0x8D;
        Log(log, "[4] bit 倒序 bitrev(0A)={0} bitrev(B1)={1}  {2}",
            Algo.Bitrev(0x0A).ToString("X2"), Algo.Bitrev(0xB1).ToString("X2"), b4 ? "OK" : "FAIL");
        ok += b4 ? 1 : 0;

        // 跨年 + 改月，换一张示例卡（同样是虚构的字节）
        int t5;
        byte[] card5 = new byte[1024];
        card5[Algo.OFF_DHI] = 0x77; card5[Algo.OFF_DLO] = 0x4B; card5[Algo.OFF_CHK] = 0x21;
        byte[] d5 = Algo.ApplyEdits(card5, E(Algo.OFF_DHI, "2D9F", "30C6"), out t5);
        bool b5 = d5[85] == 0xCF && d5[86] == 0xD1 && d5[64] == 0x03;
        Log(log, "[5] 示例卡 2022-12-31 -> 2024-06-06  日期字节 {0} {1} 校验 {2}  {3}",
            d5[85].ToString("X2"), d5[86].ToString("X2"), d5[64].ToString("X2"), b5 ? "OK" : "FAIL");
        ok += b5 ? 1 : 0;

        // 扇区密钥公式自检：用虚构卡号（不是任何真实卡），期望值由公式本身给出
        string[][] ks = new string[][]{
            new string[]{"11223344","EC141887635B"}, new string[]{"A1B2C3D4","5C84E817F3AB"},
            new string[]{"0F1E2D3C","F22806FF5F45"}};
        int kok = 0;
        foreach (string[] k in ks)
        {
            string got = Algo.Hex(Algo.SectorKeyFromUidHex(k[0]));
            if (got.Replace(" ", "") == k[1]) kok++;
            Log(log, "[6] 卡号 {0} -> 密码 {1}  期望 {2}  {3}", k[0], got.Replace(" ", ""), k[1],
                got.Replace(" ", "") == k[1] ? "OK" : "FAIL");
        }
        ok += (kok == ks.Length) ? 1 : 0;

        byte[] bm = Algo.FloorsToBitmap("2-11", false);
        bool b7 = Algo.Hex(bm).Replace(" ", "") == "000000000007FE";
        Log(log, "[7] 楼层 2~11 -> 位图 {0}  期望 000000000007FE  {1}", Algo.Hex(bm), b7 ? "OK" : "FAIL");
        ok += b7 ? 1 : 0;

        byte[] card8 = new byte[1024];
        Array.Copy(Algo.ParseHex("1C935EB027846A"), 0, card8, Algo.FLOOR_ABS, 7);
        card8[Algo.OFF_CHK] = 0xB0;
        System.Collections.Generic.List<Algo.Edit> ed = new System.Collections.Generic.List<Algo.Edit>();
        ed.Add(new Algo.Edit { Off = Algo.FLOOR_ABS, OldPt = Algo.FloorsToBitmap("2-11", false), NewPt = Algo.FloorsToBitmap("1-11", false) });
        int tot;
        byte[] card8b = Algo.ApplyEdits(card8, ed, out tot);
        string flNew = Algo.Hex(Algo.Slice(card8b, Algo.FLOOR_ABS, 7)).Replace(" ", "");
        bool b8 = flNew == "1C935EB02784EA" && card8b[Algo.OFF_CHK] == 0x30;
        Log(log, "[8] 楼层 2~11 -> 1~11  新密文 {0}  校验 B0 -> {1}  {2}", Algo.Hex(Algo.Slice(card8b, Algo.FLOOR_ABS, 7)),
            card8b[Algo.OFF_CHK].ToString("X2"), b8 ? "OK" : "FAIL");
        ok += b8 ? 1 : 0;

        byte[] fake = new byte[1024];
        for (int i = 0; i < Algo.S10_LEN; i++) fake[Algo.S10_OFF + i] = (byte)(i + 1);
        byte[] trailer = Algo.ParseHex("EC141887635BFF078000EC141887635B");
        Array.Copy(trailer, 0, fake, 688, 16);
        int cl;
        byte[] fc = Algo.ClearRolling(fake, out cl);
        bool b9 = cl == 48 && fc[Algo.S10_OFF] == 0 && fc[Algo.S10_OFF + 47] == 0;
        for (int i = 0; i < 16; i++) if (fc[688 + i] != trailer[i]) b9 = false;
        Log(log, "[9] 清滚动码: 清了 {0} 个字节，trailer(密钥) 保留  {1}", cl, b9 ? "OK" : "FAIL");
        ok += b9 ? 1 : 0;

        // 月份候选：低3位=4 时 4 月/12 月都可能，用「日」筛掉不存在的日期（4月31日 不存在）
        int[] ca = Algo.MonthCandidates(4, 31), cb = Algo.MonthCandidates(2, 10), cc = Algo.MonthCandidates(0, 31);
        bool b10 = ca.Length == 1 && ca[0] == 12 && cb.Length == 2 && cc.Length == 1 && cc[0] == 8;
        Log(log, "[10] 月份候选: 低3位=4 日=31 -> {0}   低3位=2 日=10 -> {1}   低3位=0 -> {2}   {3}",
            Algo.JoinInts(ca, "/"), Algo.JoinInts(cb, "/"), Algo.JoinInts(cc, "/"), b10 ? "OK" : "FAIL");
        ok += b10 ? 1 : 0;

        Log(log, "\r\n{0}/10 通过", ok);
        string p = Path.Combine(outDir, "自检结果_selftest.txt");
        File.WriteAllText(p, log.ToString(), new UTF8Encoding(true));
        try { if (Environment.GetEnvironmentVariable("XYV8_NOMSG") == null) MessageBox.Show(log.ToString(), "自检结果（同时已写入 " + p + "）"); } catch { }
        return ok;
    }
    static System.Collections.Generic.List<Algo.Edit> E(int off, string a, string b)
    {
        System.Collections.Generic.List<Algo.Edit> l = new System.Collections.Generic.List<Algo.Edit>();
        l.Add(new Algo.Edit { Off = off, OldPt = Hex2(a), NewPt = Hex2(b) });
        return l;
    }
    static byte[] Hex2(string s) { return new byte[]{ Convert.ToByte(s.Substring(0,2),16), Convert.ToByte(s.Substring(2,2),16) }; }
    static byte[] Blank() { return new byte[1024]; }
    /// 自检用的"示例卡"的三个关键字节（虚构的，不是任何真实卡的数据）
    static byte[] SampleCard()
    {
        byte[] d = new byte[1024];
        d[Algo.OFF_DHI] = 0xA3; d[Algo.OFF_DLO] = 0x5C; d[Algo.OFF_CHK] = 0xB0;
        return d;
    }
    static void Log(StringBuilder sb, string fmt, params object[] a) { sb.AppendLine(string.Format(fmt, a)); }
}

// ============================================================ 界面
class MainForm : Form
{
    byte[] card;         // 打开的卡数据
    string cardPath = "";
    RichTextBox txtInfo, txtLog;     // 黑底绿字
    TextBox txtCurY, txtCurM, txtCurD, txtNewY, txtNewM, txtNewD;
    TextBox numYears, numMonths;   // 延时年/月（用文本框，避免 NumericUpDown 的兼容问题）
    TextBox txtCurFloors, txtNewFloors;
    Label lblFloorCt, lblDetect, lblFile;
    RadioButton radNoFloor, radDoFloor;      // ① 改不改楼层
    CheckBox chkBase0, chkBase0b, chkClear;  // chkBase0 只管懒人页，chkBase0b 是手动计算页的
    // 算密码
    TextBox txtUid, txtKey;
    // 手算
    TextBox mOldD, mNewD, mHi, mLo, mChk, mOut1;
    TextBox fOld, fNew, fCt, fChk, fOut2;
    TextBox gOldPt, gNewPt, gCt, gChk, gOut3;

    // 配色：深色科技风（黑底 + 亮绿 + 少量红/黄强调），别太花
    internal static readonly Color CL_BG = Color.FromArgb(8, 12, 10);
    internal static readonly Color CL_GRN = Color.FromArgb(0, 255, 128);
    internal static readonly Color CL_DIM = Color.FromArgb(120, 200, 160);
    internal static readonly Color CL_RED = Color.FromArgb(255, 90, 90);
    internal static readonly Color CL_YEL = Color.FromArgb(255, 220, 120);

    static RichTextBox DarkBox(int x, int y, int w, int h)
    {
        RichTextBox r = new RichTextBox();
        r.SetBounds(x, y, w, h);
        r.BackColor = CL_BG; r.ForeColor = CL_GRN;
        r.Font = new Font("Consolas", 9F);
        r.ReadOnly = true; r.BorderStyle = BorderStyle.FixedSingle;
        r.WordWrap = true; r.ScrollBars = RichTextBoxScrollBars.Vertical;
        return r;
    }
    /// 往深色框里追加一段带颜色的字
    static void Add(RichTextBox box, string text, Color c)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = c;
        box.AppendText(text);
        box.SelectionColor = box.ForeColor;
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }
    /// 按行上色：⚠ 开头 = 红，★/自动化读出 = 黄，其余 = 绿
    internal static void AddLines(RichTextBox box, string text)
    {
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            Color c = CL_GRN;
            if (line.StartsWith("⚠")) c = CL_RED;
            else if (line.StartsWith("★") || line.Contains("自动读出") || line.StartsWith("http")) c = CL_YEL;
            else if (line.StartsWith("【")) c = CL_YEL;
            Add(box, line + "\r\n", c);
        }
    }

    string autoOpen;
    public MainForm(string openPath)
    {
        autoOpen = openPath;
        Text = "鑫洋V8 电梯卡工具 (日期 / 楼层 / 卡密码) v1.0.6";
        Size = new Size(900, 900);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        TabControl tabs = new TabControl();
        tabs.Dock = DockStyle.Fill;
        tabs.TabPages.Add(BuildLazy());
        tabs.TabPages.Add(BuildKey());
        tabs.TabPages.Add(BuildManual());
        Controls.Add(tabs);
        if (!string.IsNullOrEmpty(autoOpen)) LoadCard(autoOpen);
    }

    // ---------- 页1：懒人改卡 ----------
    TabPage BuildLazy()
    {
        TabPage p = new TabPage("① 懒人改卡（推荐）");
        // ================= 顶部整行：使用说明（只说怎么用）=================
        GroupBox gHow = new GroupBox(); gHow.Text = "使用说明"; gHow.SetBounds(12, 46, 876, 80);
        RichTextBox how = DarkBox(8, 18, 860, 58);
        AddLines(how, HOW_TEXT);
        how.SelectionStart = 0; how.ScrollToCaret();
        gHow.Controls.Add(how);

        // ================= 左栏：打开卡文件 + 检测结果（竖框）=================
        Button btnOpen = new Button();
        btnOpen.Text = "打开卡文件(dump)…"; btnOpen.SetBounds(12, 130, 170, 32);
        btnOpen.Font = new Font(Font, FontStyle.Bold);
        btnOpen.Click += delegate { OpenCard(); };
        lblFile = new Label(); lblFile.SetBounds(192, 137, 300, 20); lblFile.Text = "（还没选择文件）"; lblFile.ForeColor = Color.DimGray;

        GroupBox gInfo = new GroupBox(); gInfo.Text = "检测结果"; gInfo.SetBounds(12, 170, 470, 528);
        txtInfo = DarkBox(8, 20, 454, 500);
        gInfo.Controls.Add(txtInfo);

        // ================= 右栏：所有编辑操作 =================
        GroupBox g1 = new GroupBox(); g1.Text = "改有效期"; g1.SetBounds(494, 130, 394, 150);
        Lbl(g1, "现在的有效期:", 12, 28);
        txtCurY = Tb(g1, 108, 25, 52, ""); Lbl(g1, "年", 162, 28);
        txtCurM = Tb(g1, 180, 25, 36, "10"); Lbl(g1, "月", 218, 28);
        txtCurD = Tb(g1, 236, 25, 36, "07"); Lbl(g1, "日", 274, 28);
        Label lblYearNote = new Label();
        lblYearNote.SetBounds(12, 52, 370, 18);
        lblYearNote.ForeColor = Color.FromArgb(190, 0, 0);
        lblYearNote.Text = "「年」读不出来，要你照工具/物业填；「月」只给候选。";
        lblDetect = new Label(); lblDetect.SetBounds(12, 72, 370, 18);
        lblDetect.Text = "（打开卡后这里会显示自动读出的日/月）"; lblDetect.ForeColor = Color.DimGray;
        Lbl(g1, "延时:", 12, 100);
        numYears = Tb(g1, 58, 97, 46, "1"); Lbl(g1, "年", 108, 100);
        numMonths = Tb(g1, 128, 97, 46, "0"); Lbl(g1, "个月", 178, 100);
        Lbl(g1, "或指定新日期:", 12, 126);
        txtNewY = Tb(g1, 108, 123, 52, ""); Lbl(g1, "年", 162, 126);
        txtNewM = Tb(g1, 180, 123, 36, ""); Lbl(g1, "月", 218, 126);
        txtNewD = Tb(g1, 236, 123, 36, ""); Lbl(g1, "日", 274, 126);
        g1.Controls.Add(lblYearNote);
        g1.Controls.Add(lblDetect);

        GroupBox g2 = new GroupBox(); g2.Text = "改楼层"; g2.SetBounds(494, 288, 394, 204);

        radNoFloor = new RadioButton(); radNoFloor.SetBounds(12, 20, 156, 22);
        radNoFloor.Text = "不改楼层（卡上原样）"; radNoFloor.Checked = true;
        radDoFloor = new RadioButton(); radDoFloor.SetBounds(178, 20, 90, 22);
        radDoFloor.Text = "改楼层";
        radNoFloor.CheckedChanged += delegate { UpdFloorEnable(); };
        radDoFloor.CheckedChanged += delegate { UpdFloorEnable(); };

        Lbl(g2, "现在开通:", 12, 50);
        txtCurFloors = Tb(g2, 82, 47, 84, "");
        Lbl(g2, "想改成:", 178, 50);
        txtNewFloors = Tb(g2, 238, 47, 84, "");

        Label lblFillHow = new Label();
        lblFillHow.AutoSize = false; lblFillHow.SetBounds(12, 74, 374, 36);
        lblFillHow.ForeColor = Color.DimGray;
        lblFillHow.Text = "「现在开通」请照别的软件（PCR532 等）读出来的填，别猜。\r\n" +
            "「想改成」可写 2-11 或 1,3,5，最多 56 层。";

        lblFloorCt = new Label();
        lblFloorCt.SetBounds(12, 112, 374, 18);          // 单独一行，避免被截断
        lblFloorCt.Text = "楼层密文: （打开卡后自动读出）";

        Label lblWrong = new Label();
        lblWrong.AutoSize = false; lblWrong.SetBounds(12, 132, 374, 36);
        lblWrong.ForeColor = Color.FromArgb(190, 0, 0);
        lblWrong.Text = "差量按你填的「现在开通」算，填错 → 楼层全不对、刷不了。\r\n" +
            "拿不准就别改楼层，只改日期。";

        chkBase0 = new CheckBox(); chkBase0.AutoSize = true; chkBase0.SetBounds(12, 172, 10, 24);
        chkBase0.Text = "位0 算负一层(B1)（不懂就别勾）";
        g2.Controls.Add(lblFillHow);
        g2.Controls.Add(lblFloorCt);
        g2.Controls.Add(lblWrong);
        g2.Controls.Add(chkBase0);
        g2.Controls.Add(radNoFloor);
        g2.Controls.Add(radDoFloor);

        chkClear = new CheckBox(); chkClear.SetBounds(494, 500, 394, 24);
        chkClear.Text = "清空滚动码（扇区10；密钥不动）";

        Button btnSave = new Button(); btnSave.Text = "生成新文件…"; btnSave.SetBounds(494, 530, 180, 36);
        btnSave.Font = new Font(Font, FontStyle.Bold);
        btnSave.Click += delegate { SaveCard(); };

        Button btnHelp = new Button(); btnHelp.Text = "详细说明（原理）"; btnHelp.SetBounds(494, 574, 180, 32);
        btnHelp.Click += delegate { new HelpForm().ShowDialog(this); };

        Label lblTip = new Label();
        lblTip.SetBounds(494, 614, 394, 88);
        lblTip.ForeColor = Color.DimGray;
        lblTip.Text = "提示：\r\n" +
            "· 生成后先用读卡软件读一遍新 dump 确认，再写回卡里\r\n" +
            "· 动手前先备份原始 dump\r\n" +
            "· 只支持鑫洋V8；滚动码（扇区10）默认不动，跟改日期无关";

        // ================= 底部整行：日志 =================
        GroupBox gLog = new GroupBox(); gLog.Text = "日志"; gLog.SetBounds(12, 706, 876, 146);
        txtLog = DarkBox(8, 18, 860, 120);
        gLog.Controls.Add(txtLog);

        p.Controls.AddRange(new Control[] { gHow, btnOpen, lblFile, gInfo, g1, g2, chkClear, btnSave, btnHelp, lblTip, gLog });
        return p;
    }

    // 顶部一条：只说"怎么用"
    const string HOW_TEXT =
        "① 导出卡的 .dump → ② 点下面「打开卡文件」→ ③ 右边填「现在的有效期」；要改楼层就把「改楼层」勾上再填 → ④ 点「生成新文件」，再把新的 dump 写回卡里。\r\n" +
        "      日会自动读出；「年」和「当前开通楼层」读不出来要你填；「月」可能给 1~2 个候选。不动楼层就别管它。原理见「详细说明（原理）」按钮。";

    // 详细说明窗口里的正文
    internal const string WHY_TEXT =
        "【这个工具在干什么】\r\n" +
        "鑫洋V8 电梯卡的数据都在 扇区1：日期、楼层、园区码、发卡号…都存在那 3 个块里。\r\n" +
        "本工具就是帮你改其中两样：有效期 和 开通楼层，并把校验码一起算对。\r\n" +
        "\r\n" +
        "【为什么不需要卡的密码】\r\n" +
        "卡里每个字节的真身是：\r\n" +
        "      密文 = 按位倒序(明文) ⊕ 每卡一套的密钥流\r\n" +
        "那串「密钥流」是一卡一密的，本来不知道就没法解密。\r\n" +
        "但同一张卡改数据时，它前后不变，做差就自己消掉了：\r\n" +
        "      Δ密文 = 按位倒序(Δ明文)\r\n" +
        "所以只要知道「旧值」，就能算出「新值」该写成什么 —— 全程不需要密码。\r\n" +
        "\r\n" +
        "【有效期怎么算】\r\n" +
        "打包成一个 16 位数： (年-2000)×512 + 月×32 + 日\r\n" +
        "存在 扇区1 块1 的第 6、7 字节。改日期只需动 3 个字节：\r\n" +
        "  · 日期高字节 ⊕= 倒序(Δ高)\r\n" +
        "  · 日期低字节 ⊕= 倒序(Δ低)\r\n" +
        "  · 校验码（扇区1 块0 第1字节）⊕= 倒序(Δ高) ⊕ 倒序(Δ低)\r\n" +
        "\r\n" +
        "【楼层怎么算】\r\n" +
        "扇区1 块0 第10~16字节 = 7 字节 = 56 位位图，位号 = 楼层号 - 1。\r\n" +
        "  位0=1楼、位1=2楼 … 位8=9楼、位19=20楼、位55=56楼\r\n" +
        "（做法：把已知位图的卡逐位写进去，再用另一款能识别楼层的软件读回来核对：\r\n" +
        "  只开位8→报9楼、位11→12楼、位19→20楼、位55→56楼，全部吻合）\r\n" +
        "所以本工具支持 1~56 层；57 层以上装不下，会直接拒绝。\r\n" +
        "注意：有些分析软件的「楼层」栏只解读位图的最后一个字节，只能显示 1~8 层，\r\n" +
        "      9 层以上看不到 —— 那是软件的显示限制，不是卡的限制。\r\n" +
        "\r\n" +
        "【「现在开通」填错了会怎样】→ 完全不对，卡会刷不了\r\n" +
        "楼层那 7 个字节在卡里也是密文，工具是拿「旧楼层 → 新楼层」的差量去改的：\r\n" +
        "      新密文 = 旧密文 ⊕ 倒序(新楼层位图 ⊕ 你填的旧楼层位图)\r\n" +
        "也就是说「现在开通」是算差量的基准，必须和卡上真实开通的楼层一模一样。\r\n" +
        "填错的话，卡里剩下的 = 真实楼层 ⊕ (你填的新 ⊕ 你填的旧)，整个位图都错位。\r\n" +
        "  例：卡上真的是 2~11，你把「现在开通」写成 1-11、「想改成」写 2-11，\r\n" +
        "      结果卡上会变成 1~11（多开一层），而不是你想要的 2~11。\r\n" +
        "日期不一样：日和月份的候选工具能自己核对；楼层它在密文里，工具没法核对。\r\n" +
        "所以楼层要拿读卡软件读出来的真实值填，拿不准就别改楼层，只改日期。\r\n" +
        "\r\n" +
        "【校验码】\r\n" +
        "整张卡只有一个校验码：扇区1 块0 第1字节。\r\n" +
        "楼层、日期、园区码、发卡号都共用它，所以改哪里都要同步更新它 —— 工具会自动算好。\r\n" +
        "（规则：校验字节的增量 = 所有被改动字节的密文增量异或起来）\r\n" +
        "\r\n" +
        "【为什么有些东西读不出来】\r\n" +
        "· 能自动读：日（准确）、校验码、楼层字段密文、滚动码状态\r\n" +
        "· 读不出来：年份、月份的第 4 位、当前开通的楼层\r\n" +
        "  年份和月份第4位都在「日期高字节」里，被每卡一套的密钥流加密，没法读；\r\n" +
        "  楼层在低字节里，同样被加密。这些都要你填。\r\n" +
        "  月份我给了 1~2 个候选（第4位只能取 0 或 1），并用「日」筛掉不存在的日期：\r\n" +
        "  例：低3位=4 → 月份是 4 或 12，而 4月31日 不存在 → 只能是 12 月。\r\n" +
        "\r\n" +
        "【滚动码（扇区10）】\r\n" +
        "滚动码是电梯刷卡时写进卡里的，跟改日期没有关系（实测：改期后扇区10 一点没变；\r\n" +
        "反过来电梯刷过一次，扇区10 的计数器 +1，而扇区1 一个字节都没动）。\r\n" +
        "所以默认不动它；只有在卡已经刷不动、想「重置」时才勾那个选项（效果不保证）。\r\n" +
        "\r\n" +
        "【项目地址 / 有问题联系我】\r\n" +
        "https://github.com/Potaperper/xinyang-v8-elevator-card\r\n" +
        "有问题在 Issues 里提，或直接把出问题的 dump 发我（发之前可以先打码卡号）。";
    // ---------- 页2：算卡密码 ----------
    TabPage BuildKey()
    {
        TabPage p = new TabPage("② 算卡密码（读卡用）");
        Label l1 = new Label(); l1.SetBounds(12, 20, 730, 40);
        l1.Text = "鑫洋V8 的扇区密码由卡号(UID)直接算出来：读到 UID 就能把卡读开，不用解卡、不用嗅探。\r\n（扇区1 与扇区10 用同一个密码）";
        Button b1 = new Button(); b1.Text = "从卡文件读取 UID"; b1.SetBounds(12, 70, 160, 30);
        b1.Click += delegate {
            OpenFileDialog d = new OpenFileDialog(); d.Filter = "卡数据 (*.dump;*.mfd;*.bin)|*.dump;*.mfd;*.bin|所有文件 (*.*)|*.*";
            if (d.ShowDialog() == DialogResult.OK)
            {
                byte[] b = File.ReadAllBytes(d.FileName);
                if (b.Length >= 4) txtUid.Text = string.Format("{0:X2}{1:X2}{2:X2}{3:X2}", b[0], b[1], b[2], b[3]);
            }
        };
        Lbl(p, "卡号 UID:", 190, 77);
        txtUid = Tb(p, 260, 74, 120, "11223344");
        Button b2 = new Button(); b2.Text = "算密码"; b2.SetBounds(400, 70, 90, 30);
        b2.Click += delegate { CalcKey(); };
        txtKey = new TextBox();
        txtKey.SetBounds(12, 120, 770, 34); txtKey.ReadOnly = true;
        txtKey.Font = new Font("Consolas", 16F, FontStyle.Bold);
        txtKey.BackColor = CL_BG; txtKey.ForeColor = CL_GRN; txtKey.BorderStyle = BorderStyle.FixedSingle;
        Button b3 = new Button(); b3.Text = "复制密码"; b3.SetBounds(12, 160, 100, 28);
        b3.Click += delegate { if (txtKey.Text.Length > 0) Clipboard.SetText(txtKey.Text); };
        p.Controls.AddRange(new Control[] { l1, b1, txtUid, b2, txtKey, b3 });
        return p;
    }

    // ---------- 页3：手动计算 ----------
    TabPage BuildManual()
    {
        TabPage p = new TabPage("③ 手动计算（不想改文件时用）");
        int y = 12;
        GroupBox g1 = new GroupBox(); g1.Text = "只算日期：给出新旧日期 + 卡上现在的 3 个字节"; g1.SetBounds(12, y, 736, 96); y += 104;
        Lbl(g1, "旧日期", 12, 30); mOldD = Tb(g1, 70, 27, 100, "2024-03-15");
        Lbl(g1, "新日期", 190, 30); mNewD = Tb(g1, 248, 27, 100, "2029-06-30");
        Lbl(g1, "日期高字节", 370, 30); mHi = Tb(g1, 450, 27, 44, "A3");
        Lbl(g1, "日期低字节", 505, 30); mLo = Tb(g1, 585, 27, 44, "5C");
        Lbl(g1, "校验码", 640, 30); mChk = Tb(g1, 690, 27, 40, "B0");
        Button b = new Button(); b.Text = "计算"; b.SetBounds(12, 58, 80, 26);
        b.Click += delegate { try { mOut1.Text = ManualDate(); } catch (Exception e) { Err(e); } };
        mOut1 = new TextBox(); mOut1.SetBounds(100, 58, 660, 26); mOut1.ReadOnly = true; mOut1.Font = new Font("Consolas", 10F);
        mOut1.BackColor = CL_BG; mOut1.ForeColor = CL_GRN;
        g1.Controls.AddRange(new Control[] { b, mOut1 });

        GroupBox g2 = new GroupBox(); g2.Text = "只算楼层：给出新旧楼层 + 楼层字段密文 + 校验码"; g2.SetBounds(12, y, 736, 124); y += 132;
        Lbl(g2, "现在开通", 12, 30); fOld = Tb(g2, 76, 27, 100, "2-11");
        Lbl(g2, "改成", 190, 30); fNew = Tb(g2, 232, 27, 100, "1-11");
        Lbl(g2, "楼层密文(7字节)", 350, 30); fCt = Tb(g2, 460, 27, 170, "1C 93 5E B0 27 84 6A");
        Lbl(g2, "校验码", 640, 30); fChk = Tb(g2, 690, 27, 40, "B0");
        chkBase0b = new CheckBox(); chkBase0b.AutoSize = true; chkBase0b.SetBounds(12, 86, 10, 24);
        chkBase0b.Text = "位0 算负一层(B1)（不懂就别勾）";
        Button b2 = new Button(); b2.Text = "计算"; b2.SetBounds(12, 58, 80, 26);
        b2.Click += delegate { try { fOut2.Text = ManualFloor(); } catch (Exception e) { Err(e); } };
        fOut2 = new TextBox(); fOut2.SetBounds(100, 58, 660, 26); fOut2.ReadOnly = true; fOut2.Font = new Font("Consolas", 10F);
        fOut2.BackColor = CL_BG; fOut2.ForeColor = CL_GRN;
        g2.Controls.AddRange(new Control[] { b2, fOut2, chkBase0b });

        GroupBox g3 = new GroupBox(); g3.Text = "只算某个字段（园区码 / 发卡号 / 房间号 / 梯号 / 控制位…）"; g3.SetBounds(12, y, 736, 96);
        Lbl(g3, "旧明文", 12, 30); gOldPt = Tb(g3, 66, 27, 90, "4E12");
        Lbl(g3, "新明文", 170, 30); gNewPt = Tb(g3, 224, 27, 90, "1234");
        Lbl(g3, "旧密文", 330, 30); gCt = Tb(g3, 384, 27, 90, "5E71");
        Lbl(g3, "校验码", 490, 30); gChk = Tb(g3, 540, 27, 44, "80");
        Button b3 = new Button(); b3.Text = "计算"; b3.SetBounds(12, 58, 80, 26);
        b3.Click += delegate { try { gOut3.Text = ManualField(); } catch (Exception e) { Err(e); } };
        gOut3 = new TextBox(); gOut3.SetBounds(100, 58, 660, 26); gOut3.ReadOnly = true; gOut3.Font = new Font("Consolas", 10F);
        gOut3.BackColor = CL_BG; gOut3.ForeColor = CL_GRN;
        g3.Controls.AddRange(new Control[] { b3, gOut3 });

        Label tip = new Label();
        tip.SetBounds(12, y + 8, 736, 120);
        tip.Text = "提示：\r\n" +
            "· 楼层规则：位号 = 楼层号 − 1（位0 = 1楼），这是逐位写入卡里、再用分析软件读楼层实测出来的。\r\n" +
            "· 日期打包：(年−2000)×512 + 月×32 + 日；卡里存的是「bit 倒序后的明文 ⊕ 每卡一套的密钥流」。\r\n" +
            "· 因为算的是「变化量」，同一张卡做差时那串密钥流会自己消掉 —— 所以不需要知道卡的密码。\r\n" +
            "· 任何一处改动都要同步那个校验码（扇区1 块0 第1字节），本工具会自动算好。";
        p.Controls.AddRange(new Control[] { g1, g2, g3, tip });
        return p;
    }

    // ---------------- 逻辑 ----------------
    /// 纯检测：读出这张 dump 里所有能自动得到的信息（GUI 和 --inspect 共用同一段逻辑）
    public static string DetectText(byte[] b, string path, out int m3, out int day, out int[] cand, out bool keyOk)
    {
        string uid = string.Format("{0:X2}{1:X2}{2:X2}{3:X2}", b[0], b[1], b[2], b[3]);
        string kPred = Algo.Hex(Algo.SectorKey(new byte[] { b[0], b[1], b[2], b[3] })).Replace(" ", "");
        string kReal = Algo.Hex(Algo.Slice(b, 112, 6)).Replace(" ", "");
        string k10 = Algo.Hex(Algo.Slice(b, 688, 6)).Replace(" ", "");
        Algo.ReadDayMonth(b, out m3, out day);
        cand = Algo.MonthCandidates(m3, day);
        keyOk = (kPred == kReal);
        int nz = 0; for (int i = 0; i < Algo.S10_LEN; i++) if (b[Algo.S10_OFF + i] != 0) nz++;
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("文件      : " + Path.GetFileName(path));
        sb.AppendLine("卡号 UID  : " + uid);
        sb.AppendLine("扇区密钥  : " + kPred + "   (扇区1 实际 " + kReal + (keyOk ? " 一致 OK" : " 不一致!") + ")");
        sb.AppendLine("          : 扇区10 密钥 " + k10);
        sb.AppendLine("校验码    : " + b[Algo.OFF_CHK].ToString("X2") + "    (扇区1 块0 第1字节)");
        sb.AppendLine("日期密文  : " + b[Algo.OFF_DHI].ToString("X2") + " " + b[Algo.OFF_DLO].ToString("X2") + "    (块1 第6、7字节)");
        sb.AppendLine("自动读出  : 日 = " + day.ToString("00") + "（准确）；月份候选 = " + Algo.JoinInts(cand, " 或 "));
        sb.AppendLine("楼层密文  : " + Algo.Hex(Algo.Slice(b, Algo.FLOOR_ABS, 7)) + "    (块0 第10~16字节)");
        sb.AppendLine("滚动码    : " + (nz == 0 ? "扇区10 全 0（空白，最干净）" : "扇区10 有 " + nz + " 个非零字节"));
        sb.Append("⚠ 要你填的  : 年份、当前开通楼层（读不出来，可用 PCR532 之类读卡软件读出）");
        return sb.ToString();
    }

    void OpenCard()
    {
        OpenFileDialog d = new OpenFileDialog();
        d.Filter = "卡数据 (*.dump;*.mfd;*.bin)|*.dump;*.mfd;*.bin|所有文件 (*.*)|*.*";
        if (d.ShowDialog() != DialogResult.OK) return;
        LoadCard(d.FileName);
    }

    public void LoadCard(string fileName)
    {
        try
        {
            byte[] b = File.ReadAllBytes(fileName);
            if (b.Length < 1024) { MessageBox.Show("这个文件不足 1024 字节，不像 MIFARE 1K 的 dump。"); return; }
            card = b; cardPath = fileName;
            lblFile.Text = Path.GetFileName(fileName) + "   (" + b.Length + " 字节)"; lblFile.ForeColor = Color.FromArgb(0, 130, 60);
            int m3, day; int[] cand; bool keyOk;
            string infoText = DetectText(b, fileName, out m3, out day, out cand, out keyOk);
            txtInfo.Clear(); AddLines(txtInfo, infoText);
            txtInfo.SelectionStart = 0; txtInfo.ScrollToCaret();
            txtCurD.Text = day.ToString("00");
            txtCurM.Text = (cand.Length == 1) ? cand[0].ToString() : "";   // 定不出来就留空，别瞎填
            txtCurFloors.Text = ""; txtNewFloors.Text = "";
            lblFloorCt.Text = "楼层密文: " + Algo.Hex(Algo.Slice(b, Algo.FLOOR_ABS, 7));
            if (cand.Length == 1)
                lblDetect.Text = string.Format("自动读出 → 日 = {0:00}，月份 = {1}（由合法性定出）", day, cand[0]);
            else
                lblDetect.Text = string.Format("自动读出 → 日 = {0:00}，月份只能是 {1}（请自己填，我读不出月份的第4位）",
                    day, Algo.JoinInts(cand, " 或 "));
            if (!keyOk) MessageBox.Show("警告：由 UID 推算的密钥和卡里实际的密钥不一致，\n这张卡可能是别的变种（V11 / 新鑫洋V8），请谨慎操作。");

            int nz = 0; for (int i = 0; i < Algo.S10_LEN; i++) if (b[Algo.S10_OFF + i] != 0) nz++;
            StringBuilder lg = new StringBuilder();
            lg.AppendLine("读入 " + Path.GetFileName(fileName) + "   " + b.Length + " 字节");
            lg.AppendLine(string.Format("UID {0:X2}{1:X2}{2:X2}{3:X2}   扇区1 密钥 {4}   {5}",
                b[0], b[1], b[2], b[3],
                Algo.Hex(Algo.SectorKey(new byte[] { b[0], b[1], b[2], b[3] })).Replace(" ", ""),
                keyOk ? "与卡内一致 OK" : "⚠ 与卡内不一致，可能是别的变种"));
            lg.AppendLine(string.Format("校验码 {0:X2}   日期密文 {1:X2} {2:X2}   楼层密文 {3}",
                b[Algo.OFF_CHK], b[Algo.OFF_DHI], b[Algo.OFF_DLO], Algo.Hex(Algo.Slice(b, Algo.FLOOR_ABS, 7))));
            lg.AppendLine(string.Format("自动读出 日 = {0:00}   月份候选 = {1}   → 已填进右边，年份请你自己补",
                day, Algo.JoinInts(cand, " 或 ")));
            lg.AppendLine(nz == 0 ? "滚动码 扇区10 全 0（空白）" :
                string.Format("滚动码 扇区10 有 {0} 个非零字节（默认不动）", nz));
            lg.AppendLine("等你补：年份（必填）；要改楼层就选「改楼层」再填现在开通/想改成 → 最后点「生成新文件…」");
            txtLog.Clear(); AddLines(txtLog, lg.ToString());
            txtLog.SelectionStart = 0; txtLog.ScrollToCaret();
        }
        catch (Exception e) { Err(e); }
    }

    // 选「不改楼层」时，楼层那几个输入框就灰掉（免得以为必须填）
    void UpdFloorEnable()
    {
        bool on = radDoFloor.Checked;
        txtCurFloors.Enabled = on;
        txtNewFloors.Enabled = on;
        chkBase0.Enabled = on;
    }

    void SaveCard()
    {
        if (card == null) { MessageBox.Show("先打开一个卡文件。"); return; }
        try
        {
            int cy = Int(txtCurY.Text), cm = Int(txtCurM.Text), cd = Int(txtCurD.Text);
            if (cy == 0) { MessageBox.Show("请填「现在的有效期」里的【年份】。\n年份在密文里读不出来（被每卡一套的密钥流加密），要照读卡工具或物业填；\n月/日我已经自动读出来放在下面了。"); return; }
            if (cm < 1 || cm > 12 || cd < 1 || cd > 31) { MessageBox.Show("现在的有效期 月/日 不对。"); return; }
            if (!Algo.IsRealDate(cm, cd))
            { MessageBox.Show(string.Format("{0}月{1}日 这个日期不存在，请检查月份（月份要自己填，我读不出第 4 位）。", cm, cd)); return; }

            // 核对：密文里解出来的 日 / 月的低3位（月份第 4 位读不出来，所以只核对低 3 位）
            int m3, day; Algo.ReadDayMonth(card, out m3, out day);
            if (day != cd || (cm & 7) != m3)
                if (MessageBox.Show(string.Format(
                    "核对不上：\n  密文里解出的是 日={0:00}、月的低3位={1}\n  你填的是 {2}-{3:00}-{4:00}\n\n" +
                    "以【你填的】为准就行（改动量是按你的输入算的，继续即可）：\n" +
                    "· 如果你确定有效期没错，那多半是这张卡属于新变种（新鑫洋V8），固定掩码不是 D4，自动读出的日/月不准；\n" +
                    "· 如果你只是随手填的，建议回去核对一下再继续。\n\n仍要继续吗？",
                    day, m3, cy, cm, cd), "请确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            int ty, tm, td;
            if (txtNewY.Text.Trim().Length > 0)
            {
                ty = Int(txtNewY.Text); tm = Int(txtNewM.Text); td = Int(txtNewD.Text);
                if (ty == 0 || tm < 1 || tm > 12 || td < 1 || td > 31) { MessageBox.Show("指定的新日期不合法。"); return; }
                if (!Algo.IsRealDate(tm, td)) { MessageBox.Show(string.Format("{0}月{1}日 这个日期不存在，请检查。", tm, td)); return; }
            }
            else
            {
                ty = cy + Int(numYears.Text); tm = cm + Int(numMonths.Text); td = cd;
                while (tm > 12) { tm -= 12; ty++; }
            }

            System.Collections.Generic.List<Algo.Edit> edits = new System.Collections.Generic.List<Algo.Edit>();
            int ov = Algo.Pack(cy, cm, cd), nv = Algo.Pack(ty, tm, td);
            if (ov != nv)
                edits.Add(new Algo.Edit { Off = Algo.OFF_DHI, OldPt = W(ov), NewPt = W(nv) });

            string floorNote = "";
            if (radDoFloor.Checked)
            {
                string curF = txtCurFloors.Text.Trim();
                if (curF.Length == 0)
                {
                    MessageBox.Show("你选了「改楼层」，但「现在开通」是空的。\n" +
                        "请照别的软件（PCR532 等）读出来的「当前开通楼层」填，例 2-11。\n" +
                        "（不想改楼层的话，把上面选回「不改楼层（卡上原样）」）");
                    return;
                }
                if (txtNewFloors.Text.Trim().Length == 0) { MessageBox.Show("你选了「改楼层」，但「想改成」是空的。"); return; }
                byte[] ob = Algo.FloorsToBitmap(curF, chkBase0.Checked);
                byte[] nb = Algo.FloorsToBitmap(txtNewFloors.Text, chkBase0.Checked);
                edits.Add(new Algo.Edit { Off = Algo.FLOOR_ABS, OldPt = ob, NewPt = nb });
                floorNote = string.Format("楼层: {0} -> {1}   位图 {2} -> {3}（差量按你填的「现在开通」算）\r\n",
                    curF, txtNewFloors.Text, Algo.Hex(ob), Algo.Hex(nb));
            }
            if (edits.Count == 0 && !chkClear.Checked) { MessageBox.Show("没有要改的东西。"); return; }

            int total;
            byte[] nd = Algo.ApplyEdits(card, edits, out total);
            int cleared = 0;
            if (chkClear.Checked) nd = Algo.ClearRolling(nd, out cleared);

            SaveFileDialog s = new SaveFileDialog();
            s.Filter = "卡数据 (*.dump)|*.dump|所有文件 (*.*)|*.*";
            s.FileName = Path.GetFileNameWithoutExtension(cardPath) + "_改好.dump";
            if (s.ShowDialog() != DialogResult.OK) return;
            File.WriteAllBytes(s.FileName, nd);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("到期日: {0}-{1:00}-{2:00}  ->  {3}-{4:00}-{5:00}", cy, cm, cd, ty, tm, td));
            sb.Append(floorNote);
            sb.AppendLine("改动明细（只列变了的字节）:");
            for (int i = 0; i < 1024; i++)
                if (card[i] != nd[i] && !(chkClear.Checked && i >= Algo.S10_OFF && i < Algo.S10_OFF + Algo.S10_LEN))
                {
                    int blk = i / 16;
                    sb.AppendLine(string.Format("  偏移 {0,4}  (扇区{1} 块{2} 第{3,2}字节):  {4:X2} -> {5:X2}",
                        i, blk / 4, blk % 4, i % 16 + 1, card[i], nd[i]));
                }
            sb.AppendLine(string.Format("  （校验码总增量 = {0:X2}）", total));
            if (chkClear.Checked) sb.AppendLine(string.Format("  扇区10 三个数据块已清零（原有 {0} 个非零字节），密钥/存取位保持不变", cleared));
            sb.AppendLine("已写出: " + s.FileName);
            txtLog.Clear(); AddLines(txtLog, sb.ToString());
            MessageBox.Show("改好了！文件已保存到：\n" + s.FileName, "完成");
        }
        catch (Exception e) { Err(e); }
    }

    void CalcKey()
    {
        try
        {
            txtKey.Text = Algo.Hex(Algo.SectorKeyFromUidHex(txtUid.Text)).Replace(" ", "");
        }
        catch (Exception e) { Err(e); }
    }

    string ManualDate()
    {
        int[] pa = DP(mOldD.Text), pb = DP(mNewD.Text);
        int ov = Algo.Pack(pa[0], pa[1], pa[2]), nv = Algo.Pack(pb[0], pb[1], pb[2]);
        int total;
        byte[] d = Algo.ApplyEdits(new byte[Algo.OFF_DHI + 2],
            E1(Algo.OFF_DHI, W(ov), W(nv)), out total, false);
        byte chk = (byte)(Algo.ParseHex(mChk.Text)[0] ^ total);
        return string.Format("日期高字节 {0} -> {1}    日期低字节 {2} -> {3}    校验码 {4} -> {5}",
            mHi.Text, d[Algo.OFF_DHI].ToString("X2"), mLo.Text, d[Algo.OFF_DLO].ToString("X2"),
            mChk.Text.ToUpper(), chk.ToString("X2"));
    }
    string ManualFloor()
    {
        byte[] ob = Algo.FloorsToBitmap(fOld.Text, chkBase0b.Checked);
        byte[] nb = Algo.FloorsToBitmap(fNew.Text, chkBase0b.Checked);
        byte[] ct = Algo.ParseHex(fCt.Text);
        if (ct.Length != 7) throw new Exception("楼层密文必须是 7 个字节（14 个十六进制字符）");
        int total;
        byte[] nc = Algo.ApplyEdits(ct, E1(0, ob, nb), out total, false);
        byte chk = (byte)(Algo.ParseHex(fChk.Text)[0] ^ total);
        return string.Format("楼层密文 {0} -> {1}    校验码 {2} -> {3}   （位图 {4} -> {5}）",
            Algo.Hex(ct), Algo.Hex(nc), fChk.Text.ToUpper(), chk.ToString("X2"), Algo.Hex(ob), Algo.Hex(nb));
    }
    string ManualField()
    {
        byte[] op = Algo.ParseHex(gOldPt.Text), np = Algo.ParseHex(gNewPt.Text), ct = Algo.ParseHex(gCt.Text);
        if (op.Length != np.Length || op.Length != ct.Length)
            throw new Exception("旧明文 / 新明文 / 旧密文 的字节数必须一样");
        int total;
        byte[] nc = Algo.ApplyEdits(ct, E1(0, op, np), out total, false);
        byte chk = (byte)(Algo.ParseHex(gChk.Text)[0] ^ total);
        return string.Format("新密文 {0}    新校验码 {1}", Algo.Hex(nc), chk.ToString("X2"));
    }

    static System.Collections.Generic.List<Algo.Edit> E1(int off, byte[] a, byte[] b)
    {
        System.Collections.Generic.List<Algo.Edit> l = new System.Collections.Generic.List<Algo.Edit>();
        l.Add(new Algo.Edit { Off = off, OldPt = a, NewPt = b });
        return l;
    }
    static int[] DP(string s)
    {
        s = s.Trim().Replace("/", "-").Replace(".", "-");
        string[] p = s.Split('-');
        if (p.Length != 3) throw new Exception("日期格式应为 2024-03-15");
        return new int[] { int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]) };
    }
    static byte[] W(int v) { return new byte[] { (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) }; }
    static byte[] Slice(byte[] b, int off, int len)
    {
        byte[] r = new byte[len]; Array.Copy(b, off, r, 0, len); return r;
    }
    static int Int(string s) { int v; int.TryParse(s.Trim(), out v); return v; }
    static void Lbl(Control parent, string text, int x, int y)
    {
        Label l = new Label();
        l.Text = text; l.AutoSize = true; l.SetBounds(x, y, 10, 20); parent.Controls.Add(l);   // 自适应宽度，避免短标签互相遮挡
    }
    static TextBox Tb(Control parent, int x, int y, int w, string text)
    {
        TextBox t = new TextBox(); t.SetBounds(x, y, w, 24); t.Text = text; parent.Controls.Add(t);
        return t;
    }
    static void Err(Exception e) { MessageBox.Show(e.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
}


// ============================================================ 详细说明（独立窗口，整页放正文）
class HelpForm : Form
{
    public HelpForm()
    {
        Text = "详细说明（原理）— 鑫洋V8 电梯卡工具";
        Size = new Size(860, 660);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        MinimizeBox = false;

        RichTextBox r = new RichTextBox();
        r.Dock = DockStyle.Fill;
        r.BackColor = MainForm.CL_BG;
        r.ForeColor = MainForm.CL_GRN;
        r.Font = new Font("Consolas", 10F);
        r.ReadOnly = true;
        r.BorderStyle = BorderStyle.None;
        r.WordWrap = true;
        r.ScrollBars = RichTextBoxScrollBars.Vertical;
        r.DetectUrls = true;
        r.LinkClicked += delegate(object s, LinkClickedEventArgs e)
        { try { System.Diagnostics.Process.Start(e.LinkText); } catch { } };
        MainForm.AddLines(r, MainForm.WHY_TEXT);
        r.SelectionStart = 0; r.ScrollToCaret();

        Button b = new Button();
        b.Text = "知道了"; b.Dock = DockStyle.Bottom; b.Height = 34;
        b.Click += delegate { Close(); };

        Controls.Add(r);
        Controls.Add(b);
    }
}
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            int ok = 0;
            try { ok = SelfTest.Run(AppDomain.CurrentDomain.BaseDirectory); }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "自检崩溃.txt"), ex.ToString()); } catch { }
            }
            Environment.Exit(ok == 10 ? 0 : 1);
        }
        if (args.Length > 1 && args[0] == "--inspect")
        {
            byte[] b = File.ReadAllBytes(args[1]);
            int m3, day; int[] cand; bool keyOk;
            string info = MainForm.DetectText(b, args[1], out m3, out day, out cand, out keyOk);
            string outp = (args.Length > 2) ? args[2] : (args[1] + "_检测.txt");
            File.WriteAllText(outp, info + "\r\n", new UTF8Encoding(true));
            return;
        }
        if (args.Length > 1 && args[0] == "--key")
        {
            MessageBox.Show(Algo.Hex(Algo.SectorKeyFromUidHex(args[1])).Replace(" ", ""), "扇区密码");
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 1 && args[0] == "--open") { Application.Run(new MainForm(args[1])); return; }
        Application.Run(new MainForm(null));
    }
}
