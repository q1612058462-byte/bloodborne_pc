// SPDX-License-Identifier: GPL-2.0-or-later
// Bloodborne PC Windows portable installer/launcher.
// setup.bat compiles this with the .NET Framework 4 C# compiler and embeds the complete runtime.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BbportPortable {

static class Program {
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args) {
        string root = null;
        for (int i = 0; i + 1 < args.Length; ++i)
            if (args[i] == "--install-root") root = args[++i];
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(root));
        return 0;
    }
}

sealed class Choice {
    public readonly string Text;
    public readonly string Value;
    public Choice(string text, string value) { Text = text; Value = value; }
    public override string ToString() { return Text; }
}

sealed class LaunchOptions {
    public string Root;
    public string Game;
    public string SaveRoot;
    public bool LanMode;
    public bool NativeLan;
    public bool PublicOnline;
    public bool LanHost;
    public bool LanSeamless;
    public string LanAddress;
    public string LanNpid;
    public string LanPassword;
    public string LanEmail;
    public string LanSecret;
    public bool AutoBackup;
    public bool OldHunters;
    public bool DesktopLink;
    public bool StartMenuLink;
    public readonly Dictionary<string, string> Ini = new Dictionary<string, string>();
    public readonly Dictionary<string, string> Env = new Dictionary<string, string>();
}

sealed class OnlinePlayer {
    public string Npid;
    public string Nickname;
    public string Title;
}

sealed class SaveValidation {
    public string Directory;
    public string TitleId;
    public string MetadataDirectory;
    public int DataFiles;
    public long Bytes;
    public DateTime Modified;
    public string Fingerprint;
    public readonly Dictionary<int, string> Slots = new Dictionary<int, string>();
    public string Summary {
        get {
            return string.Format("验证通过\r\n区域 ID：{0}\r\n数据文件：{1} 个\r\n总大小：{2:N1} MB\r\n最后修改：{3}\r\n指纹：{4}\r\n来源：{5}",
                TitleId.Length == 0 ? "未识别（导入时使用当前游戏 ID）" : TitleId, DataFiles,
                Bytes / 1048576.0, Modified.ToString("yyyy-MM-dd HH:mm:ss"), Fingerprint, Directory);
        }
    }
}

sealed class SaveCharacter {
    public int Index;
    public string File;
    public string Name;
    public DateTime Modified;
}

static class SaveTools {
    static readonly HashSet<string> BloodborneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "CUSA00207", "CUSA00208", "CUSA00299", "CUSA00900", "CUSA01363", "CUSA03014",
        "CUSA03023", "CUSA03173"
    };

    public static string DefaultRoot {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "BloodbornePC", "PlayerData"); }
    }
    public static string BackupRoot(string userRoot) { return Path.Combine(userRoot, "backups"); }

    static bool IsSaveDataName(string name, out int index) {
        index = -1;
        Match match = Regex.Match(name, @"^userdata[_-]?(\d{1,4})(?:\.(?:bin|sav))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out index) && index >= 0 && index <= 10;
    }

    static string CanonicalSaveName(int index) { return "userdata" + index.ToString("D4"); }

    static string FindTitleId(DirectoryInfo directory) {
        for (DirectoryInfo at = directory; at != null; at = at.Parent)
            if (at.Name.Length == 9 && at.Name.StartsWith("CUSA", StringComparison.OrdinalIgnoreCase))
                return at.Name.ToUpperInvariant();
        return "";
    }

    public static SaveValidation ValidateDirectory(string directory) {
        string candidate = LocateSaveDirectory(directory, 0);
        if (candidate == null)
            throw new InvalidDataException("没有找到 Bloodborne 存档。请选择包含 userdata0000…userdata0010（也兼容下划线／短横线命名）的 SPRJ0005 文件夹或其上级目录。");
        Dictionary<int, FileInfo> slots = new Dictionary<int, FileInfo>();
        foreach (FileInfo file in new DirectoryInfo(candidate).GetFiles()) {
            int index;
            if (!IsSaveDataName(file.Name, out index)) continue;
            long expected = index == 10 ? 262144L : 1310720L;
            if (file.Length != expected)
                throw new InvalidDataException(string.Format("{0} 大小异常：应为 {1} 字节，实际为 {2} 字节。",
                    file.Name, expected, file.Length));
            FileInfo existing;
            if (slots.TryGetValue(index, out existing))
                throw new InvalidDataException(string.Format("存档槽位 {0:D4} 同时存在 {1} 和 {2}，无法判断应使用哪一个。",
                    index, existing.Name, file.Name));
            slots[index] = file;
        }
        if (slots.Count == 0) throw new InvalidDataException("存档中没有可识别的 userdata 文件。");
        List<FileInfo> data = new List<FileInfo>(slots.Values);
        FileInfo newest = data[0];
        long bytes = 0;
        foreach (FileInfo file in data) { bytes += file.Length; if (file.LastWriteTime > newest.LastWriteTime) newest = file; }
        string fingerprint;
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = newest.OpenRead())
            fingerprint = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, 16);
        string meta = candidate + ".sce_sys";
        if (!Directory.Exists(meta)) {
            string nested = Path.Combine(candidate, "sce_sys");
            meta = Directory.Exists(nested) ? nested : "";
        }
        SaveValidation result = new SaveValidation { Directory = candidate, TitleId = FindTitleId(new DirectoryInfo(candidate)),
            MetadataDirectory = meta, DataFiles = data.Count, Bytes = bytes,
            Modified = newest.LastWriteTime, Fingerprint = fingerprint };
        foreach (KeyValuePair<int, FileInfo> slot in slots) result.Slots[slot.Key] = slot.Value.FullName;
        return result;
    }

    static string LocateSaveDirectory(string directory, int depth) {
        if (!Directory.Exists(directory) || depth > 7) return null;
        DirectoryInfo current = new DirectoryInfo(directory);
        int found = 0;
        foreach (FileInfo file in current.GetFiles()) { int index; if (IsSaveDataName(file.Name, out index)) ++found; }
        if (found > 0) return current.FullName;
        foreach (DirectoryInfo child in current.GetDirectories()) {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string result = LocateSaveDirectory(child.FullName, depth + 1);
            if (result != null) return result;
        }
        return null;
    }

    public static bool HasSave(string userRoot) {
        try { return LocateSaveDirectory(Path.Combine(userRoot, "savedata"), 0) != null; }
        catch { return false; }
    }

    public static void CopyTree(string source, string destination, bool overwrite) {
        Directory.CreateDirectory(destination);
        foreach (FileInfo file in new DirectoryInfo(source).GetFiles())
            file.CopyTo(Path.Combine(destination, file.Name), overwrite);
        foreach (DirectoryInfo child in new DirectoryInfo(source).GetDirectories()) {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            CopyTree(child.FullName, Path.Combine(destination, child.Name), overwrite);
        }
    }

    static string SafeReason(string reason) {
        StringBuilder value = new StringBuilder();
        foreach (char c in reason) if (char.IsLetterOrDigit(c) || c == '-' || c == '_') value.Append(c);
        return value.Length == 0 ? "manual" : value.ToString();
    }

    public static string CreateBackup(string userRoot, string reason) {
        string source = Path.Combine(userRoot, "savedata");
        if (!Directory.Exists(source) || LocateSaveDirectory(source, 0) == null) return null;
        string backups = BackupRoot(userRoot);
        Directory.CreateDirectory(backups);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string path = Path.Combine(backups, SafeReason(reason) + "-" + stamp + ".zip");
        string temporary = path + ".tmp";
        using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create)) {
            string root = Path.GetFullPath(userRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string item in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) {
                string full = Path.GetFullPath(item);
                string relative = full.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
                ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                using (Stream input = File.OpenRead(full)) using (Stream output = entry.Open()) input.CopyTo(output);
            }
        }
        File.Move(temporary, path);
        return path;
    }

    public static void PruneAutomaticBackups(string userRoot, int keep) {
        string directory = BackupRoot(userRoot);
        if (!Directory.Exists(directory)) return;
        List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(directory).GetFiles("auto-*.zip"));
        files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
        for (int i = keep; i < files.Count; ++i) files[i].Delete();
    }

    public static string ExtractZipSafely(string zipPath, string parent) {
        string target = Path.Combine(parent, "save-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        string root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (ZipArchive archive = ZipFile.OpenRead(zipPath)) {
            foreach (ZipArchiveEntry entry in archive.Entries) {
                string destination = Path.GetFullPath(Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ZIP 中包含不安全路径：" + entry.FullName);
                if (entry.Name.Length == 0) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using (Stream input = entry.Open())
                using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
            }
        }
        return target;
    }

    public static void ImportSave(SaveValidation source, string userRoot, string targetTitle) {
        if (!BloodborneIds.Contains(targetTitle))
            throw new InvalidDataException("当前游戏区域 ID 无效：" + targetTitle);
        DirectoryInfo rootInfo = new DirectoryInfo(userRoot);
        bool shadp2pLayout = rootInfo.Name.Equals("1000", StringComparison.OrdinalIgnoreCase) &&
            rootInfo.Parent != null && rootInfo.Parent.Name.Equals("shadp2p-home", StringComparison.OrdinalIgnoreCase);
        string titleRoot = shadp2pLayout ? Path.Combine(userRoot, "savedata", targetTitle) :
            Path.Combine(userRoot, "savedata", "1", targetTitle);
        string target = Path.Combine(titleRoot, "SPRJ0005");
        string targetMeta = target + ".sce_sys";
        string token = Guid.NewGuid().ToString("N");
        string staged = target + ".import-" + token, stagedMeta = staged + ".sce_sys";
        string rollback = target + ".rollback-" + token, rollbackMeta = rollback + ".sce_sys";
        Directory.CreateDirectory(titleRoot);
        CopyTree(source.Directory, staged, true);
        // Other launchers sometimes use userdata_0000, userdata-0000 or userdata0000.bin.
        // The game itself expects the canonical userdata0000 spelling, so normalise only the
        // validated slot files and leave unrelated metadata untouched.
        foreach (KeyValuePair<int, string> slot in source.Slots) {
            string canonical = Path.Combine(staged, CanonicalSaveName(slot.Key));
            File.Copy(slot.Value, canonical, true);
        }
        foreach (FileInfo file in new DirectoryInfo(staged).GetFiles()) {
            int index;
            if (IsSaveDataName(file.Name, out index) &&
                !file.Name.Equals(CanonicalSaveName(index), StringComparison.OrdinalIgnoreCase)) file.Delete();
        }
        if (source.MetadataDirectory.Length > 0) CopyTree(source.MetadataDirectory, stagedMeta, true);
        ValidateDirectory(staged);
        try {
            if (Directory.Exists(target)) Directory.Move(target, rollback);
            if (Directory.Exists(targetMeta)) Directory.Move(targetMeta, rollbackMeta);
            Directory.Move(staged, target);
            if (Directory.Exists(stagedMeta)) Directory.Move(stagedMeta, targetMeta);
            if (Directory.Exists(rollback)) Directory.Delete(rollback, true);
            if (Directory.Exists(rollbackMeta)) Directory.Delete(rollbackMeta, true);
        } catch {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            if (Directory.Exists(targetMeta)) Directory.Delete(targetMeta, true);
            if (Directory.Exists(rollback)) Directory.Move(rollback, target);
            if (Directory.Exists(rollbackMeta)) Directory.Move(rollbackMeta, targetMeta);
            throw;
        } finally {
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            if (Directory.Exists(stagedMeta)) Directory.Delete(stagedMeta, true);
        }
    }

    public static void RestoreBackup(string zipPath, string userRoot) {
        Directory.CreateDirectory(userRoot);
        string extracted = ExtractZipSafely(zipPath, userRoot);
        string staged = Path.Combine(extracted, "savedata");
        if (!Directory.Exists(staged)) staged = Path.GetDirectoryName(ValidateDirectory(extracted).Directory);
        ValidateDirectory(staged);
        string current = Path.Combine(userRoot, "savedata");
        string rollback = Path.Combine(userRoot, "savedata-rollback-" + Guid.NewGuid().ToString("N"));
        try {
            if (Directory.Exists(current)) Directory.Move(current, rollback);
            Directory.Move(staged, current);
            if (Directory.Exists(rollback)) Directory.Delete(rollback, true);
        } catch {
            if (!Directory.Exists(current) && Directory.Exists(rollback)) Directory.Move(rollback, current);
            throw;
        } finally { if (Directory.Exists(extracted)) Directory.Delete(extracted, true); }
    }

    public static string ReadTitleId(string game) {
        try {
            byte[] data = File.ReadAllBytes(Path.Combine(game, "sce_sys", "param.sfo"));
            if (data.Length < 20 || data[1] != 0x50 || data[2] != 0x53 || data[3] != 0x46) return "";
            int keys = BitConverter.ToInt32(data, 8), values = BitConverter.ToInt32(data, 12);
            int count = BitConverter.ToInt32(data, 16);
            for (int i = 0; i < count; ++i) {
                int entry = 20 + i * 16;
                int keyAt = keys + BitConverter.ToUInt16(data, entry);
                int valueAt = values + BitConverter.ToInt32(data, entry + 12);
                string key = ReadZeroString(data, keyAt);
                if (key == "INSTALL_DIR_SAVEDATA" || key == "TITLE_ID") {
                    string id = ReadZeroString(data, valueAt).ToUpperInvariant();
                    if (BloodborneIds.Contains(id)) return id;
                }
            }
        } catch { }
        return "";
    }

    static string ReadZeroString(byte[] data, int offset) {
        if (offset < 0 || offset >= data.Length) return "";
        int end = offset;
        while (end < data.Length && data[end] != 0) ++end;
        return Encoding.UTF8.GetString(data, offset, end - offset);
    }

    static int FindUsernameOffset(byte[] data) {
        // Bloodborne save editor derives the character block from the first FACE marker:
        // inventory = FACE + 4 - 4 - 34028; username = inventory - 469.
        for (int face = Math.Min(0xF000, Math.Max(0, data.Length - 4)); face + 4 <= data.Length; ++face) {
            if (data[face] != (byte)'F' || data[face + 1] != (byte)'A' ||
                data[face + 2] != (byte)'C' || data[face + 3] != (byte)'E') continue;
            int username = face - 34028 - 469;
            if (username >= 0 && username + 33 <= data.Length) return username;
        }
        return -1;
    }

    static string ReadCharacterName(string path) {
        byte[] data = File.ReadAllBytes(path);
        int username = FindUsernameOffset(data);
        if (username < 0) return null;
        StringBuilder value = new StringBuilder(16);
        for (int i = 0; i < 16; ++i) {
            byte c = data[username + 1 + i * 2];
            if (c == 0) break;
            if (c < 0x20 || c > 0x7e) return null;
            value.Append((char)c);
        }
        return value.Length == 0 ? null : value.ToString();
    }

    public static List<SaveCharacter> ListCharacters(string userRoot) {
        SaveValidation save = ValidateDirectory(Path.Combine(userRoot, "savedata"));
        List<SaveCharacter> result = new List<SaveCharacter>();
        foreach (KeyValuePair<int, string> slot in save.Slots) {
            if (slot.Key == 10) continue;
            string name = ReadCharacterName(slot.Value);
            if (name == null) continue;
            FileInfo file = new FileInfo(slot.Value);
            result.Add(new SaveCharacter { Index = slot.Key, File = slot.Value, Name = name,
                Modified = file.LastWriteTime });
        }
        result.Sort(delegate(SaveCharacter a, SaveCharacter b) { return a.Index.CompareTo(b.Index); });
        return result;
    }

    public static void RenameCharacter(SaveCharacter character, string newName) {
        if (character == null || !File.Exists(character.File)) throw new FileNotFoundException("角色存档不存在。");
        if (newName == null || newName.Length < 1 || newName.Length > 16 ||
            !Regex.IsMatch(newName, @"^[\x20-\x7E]+$"))
            throw new InvalidDataException("角色名必须是 1–16 个英文字符、数字或英文符号。");
        byte[] data = File.ReadAllBytes(character.File);
        int username = FindUsernameOffset(data);
        if (username < 0) throw new InvalidDataException("无法定位角色名；文件可能不是已解密的 Bloodborne 角色存档。");
        byte[] name = Encoding.ASCII.GetBytes(newName);
        for (int i = 0; i < 16; ++i) data[username + 1 + i * 2] = i < name.Length ? name[i] : (byte)0;
        string temporary = character.File + ".rename-" + Guid.NewGuid().ToString("N") + ".tmp";
        string recovery = character.File + ".before-rename";
        File.WriteAllBytes(temporary, data);
        try {
            if (File.Exists(recovery)) File.Delete(recovery);
            try { File.Replace(temporary, character.File, recovery, true); }
            catch (IOException) {
                // Some removable/network/compatibility filesystems do not implement ReplaceFile.
                // Keep the recovery copy first, then overwrite the original from the validated temp.
                File.Copy(character.File, recovery, true);
                File.Copy(temporary, character.File, true);
            }
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (!string.Equals(ReadCharacterName(character.File), newName, StringComparison.Ordinal)) {
            if (File.Exists(recovery)) File.Copy(recovery, character.File, true);
            throw new InvalidDataException("写入后校验失败；原文件副本保存在 " + recovery);
        }
    }
}

sealed class MainForm : Form {
    const string TitleText = "Bloodborne PC 中文安装与启动器";
    const string PayloadResource = "bbport_payload.zip";
    const string InstalledExeName = "Bloodborne-PC-中文启动器.exe";
    static readonly string PackageVersion = PayloadText("APP_VERSION.txt").Trim();
    static readonly string ReleaseNotes = PayloadText("RELEASE_NOTES_zh-CN.txt").Trim();

    static readonly Choice[] Resolutions = {
        new Choice("1280 × 720", "1280x720"), new Choice("1920 × 1080", "1920x1080"),
        new Choice("2560 × 1440", "2560x1440"), new Choice("3840 × 2160", "3840x2160"),
        new Choice("640 × 360（16:9）", "640x360"), new Choice("960 × 540（16:9）", "960x540"),
        new Choice("1440 × 810（16:9）", "1440x810"), new Choice("1600 × 900（16:9）", "1600x900"),
        new Choice("1280 × 800（16:10）", "1280x800"), new Choice("1920 × 1200（16:10）", "1920x1200"),
        new Choice("2560 × 1600（16:10）", "2560x1600"),
        new Choice("2560 × 1080（宽屏）", "2560x1080"), new Choice("3440 × 1440（宽屏）", "3440x1440"),
        new Choice("5120 × 2160（宽屏）", "5120x2160"),
        new Choice("3840 × 1080（双宽）", "3840x1080"), new Choice("5120 × 1440（双宽）", "5120x1440"),
        new Choice("1280 × 960（4:3）", "1280x960"), new Choice("1440 × 1080（4:3）", "1440x1080") };
    static readonly Choice[] FrameRates = {
        new Choice("不限制（跟随显示器）", "uncap"), new Choice("90 FPS", "90"),
        new Choice("60 FPS", "60"), new Choice("30 FPS（原版）", "30") };
    static readonly Choice[] Upscalers = {
        new Choice("FSR 3.1（通用，已内置）", "fsr3"),
        new Choice("FSR 4 v07 INT8（AMD／Intel，模型已内置）", "fsr4"),
        new Choice("FSR 4.1.1 INT8（需自备提取模型）", "fsr411"),
        new Choice("DLSS（NVIDIA RTX，模型已内置）", "dlss"),
        new Choice("TAA（原生抗锯齿）", "taa"),
        new Choice("关闭", "off") };
    static readonly Choice[] Presets = {
        new Choice("原生抗锯齿（1.0×）", "0"), new Choice("质量（1.5×）", "1"),
        new Choice("平衡（1.7×）", "2"), new Choice("性能（2.0×）", "3"),
        new Choice("超高性能（3.0×）", "4") };
    static readonly Choice[] LiveModes = {
        new Choice("关闭（速度更快）", "0"), new Choice("自动（按显卡判断）", "auto"),
        new Choice("开启", "1") };
    static readonly Choice[] ModelDetail = {
        new Choice("最高（-2）", "-2"), new Choice("游戏默认", "0"),
        new Choice("较低（1）", "1"), new Choice("最低（2）", "2") };
    static readonly Choice[] Languages = {
        new Choice("英语", "1"), new Choice("法语", "2"), new Choice("西班牙语", "3"),
        new Choice("德语", "4"), new Choice("意大利语", "5"), new Choice("俄语", "8"),
        new Choice("日语", "0") };
    static readonly Choice[] PresentModes = {
        new Choice("Mailbox（默认）", ""), new Choice("FIFO（垂直同步）", "Fifo"),
        new Choice("FIFO Relaxed", "FifoRelaxed"), new Choice("Immediate（允许撕裂）", "Immediate") };
    static readonly Choice[] QualityProfiles = {
        new Choice("极低（720p／性能优先）", "verylow"), new Choice("低（1080p／较低画质）", "low"),
        new Choice("中（1080p／均衡）", "medium"), new Choice("高（1440p／质量优先）", "high") };
    static readonly Choice[] CoreModes = {
        new Choice("原生单机核心（bbport）", "native"),
        new Choice("局域网原生联机（bbport + shadNet）", "native_lan"),
        new Choice("在线联机（内置服务，测试版）", "public_online"),
        new Choice("局域网联机核心（shadp2p，实验性）", "shadp2p") };
    static readonly string[][] CheatDefinitions = {
        new string[] { "infinite_health", "无限血（受击后立即恢复）", "Trainer Infinite Health (CUSA03173 1.09)", "experimental" },
        new string[] { "infinite_echoes", "无限血之回响（固定为 666,666,666）", "Trainer Infinite Echoes (CUSA03173 1.09)", "experimental" },
        new string[] { "upgrade_materials", "随意强化武器（材料／物品不减并变为最大）", "Trainer Upgrade Materials (CUSA03173 1.09)", "experimental" },
        new string[] { "no_dead", "不死：生命不会低于 1", "Player No Dead (Read note)", "safe" },
        new string[] { "stealth", "隐身：敌人不会主动发现你", "Player Stealth (Read note)", "safe" },
        new string[] { "silent", "静音：敌人听不到你的行动", "Player Silent (Read note)", "safe" },
        new string[] { "no_rally_decay", "斗争值不会自动衰减", "No Rally Decay", "safe" },
        new string[] { "sensitive_analog", "更灵敏的摇杆跑动阈值", "Sensitive Analog Input (easier to run)", "safe" },
        new string[] { "ds1_physics", "娱乐物理：尸体可被大幅推动", "DS1-like physics", "experimental" },
        new string[] { "enemy_control", "敌人控制：R3 控制锁定敌人，L3 返回", "Enemy Control", "experimental" },
        new string[] { "debug_camera", "调试相机：按住 × 再按 L3 切换模式", "Restore Debug Camera", "experimental" }
    };
    static readonly Choice[] KeyChoices = BuildKeyChoices();
    static readonly Choice[] MouseActions = {
        new Choice("无", "none"), new Choice("轻攻击 R1", "r1"), new Choice("重攻击 R2", "r2"),
        new Choice("防御／左手武器 L1", "l1"), new Choice("左手重攻击 L2", "l2"),
        new Choice("确定／互动 ×", "cross"), new Choice("取消／闪避 ○", "circle"),
        new Choice("使用物品 □", "square"), new Choice("切换武器 △", "triangle"),
        new Choice("锁定目标 R3", "r3"), new Choice("蹲下／功能 L3", "l3"),
        new Choice("Options", "options"), new Choice("触摸板", "touchpad"),
        new Choice("十字键上", "up"), new Choice("十字键下", "down"),
        new Choice("十字键左", "left"), new Choice("十字键右", "right") };
    static readonly Choice[] GamepadActions = {
        new Choice("无", "none"), new Choice("确定／互动 ×", "cross"),
        new Choice("取消／闪避 ○", "circle"), new Choice("使用物品 □", "square"),
        new Choice("切换武器 △", "triangle"), new Choice("L1", "l1"), new Choice("R1", "r1"),
        new Choice("L2", "l2"), new Choice("R2", "r2"), new Choice("L3", "l3"), new Choice("R3", "r3"),
        new Choice("Options", "options"), new Choice("姿势菜单（触摸板左侧）", "touch_left"),
        new Choice("触摸板右侧", "touch_right"), new Choice("触摸板（保留触摸位置）", "touchpad"),
        new Choice("十字键上", "up"), new Choice("十字键下", "down"),
        new Choice("十字键左", "left"), new Choice("十字键右", "right") };
    static readonly string[][] GamepadDefinitions = {
        new string[] { "SOUTH", "A／×（下方按键）", "cross" },
        new string[] { "EAST", "B／○（右侧按键）", "circle" },
        new string[] { "WEST", "X／□（左侧按键）", "square" },
        new string[] { "NORTH", "Y／△（上方按键）", "triangle" },
        new string[] { "LEFT_SHOULDER", "LB／L1", "l1" },
        new string[] { "RIGHT_SHOULDER", "RB／R1", "r1" },
        new string[] { "LEFT_TRIGGER", "LT／L2", "l2" },
        new string[] { "RIGHT_TRIGGER", "RT／R2", "r2" },
        new string[] { "LEFT_STICK", "LS／L3（左摇杆按下）", "l3" },
        new string[] { "RIGHT_STICK", "RS／R3（右摇杆按下）", "r3" },
        new string[] { "START", "Menu／Start／Options", "options" },
        new string[] { "BACK", "View／Select", "touch_left" },
        new string[] { "TOUCHPAD", "触摸板按下", "touchpad" },
        new string[] { "DPAD_UP", "十字键上", "up" },
        new string[] { "DPAD_DOWN", "十字键下", "down" },
        new string[] { "DPAD_LEFT", "十字键左", "left" },
        new string[] { "DPAD_RIGHT", "十字键右", "right" }
    };

    static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>();
    static readonly string[][] EffectDefinitions = LoadEffects();
    static readonly Dictionary<string, string> EffectChinese = new Dictionary<string, string> {
        { "effect_chromatic_aberration", "色差" }, { "effect_dof", "景深（DoF）" },
        { "effect_motion_blur", "动态模糊" }, { "effect_ssao", "SSAO 环境光遮蔽" },
        { "effect_game_aa", "游戏原生抗锯齿" }, { "effect_dynamic_shadows", "动态光源阴影" },
        { "effect_ssr", "SSR 反射（原版游戏没有）" }, { "skip_intro", "启动时跳过片头视频" }
    };

    static string PayloadText(string name) {
        try {
            Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource);
            if (resource == null) return "";
            using (resource)
            using (ZipArchive archive = new ZipArchive(resource, ZipArchiveMode.Read)) {
                foreach (ZipArchiveEntry entry in archive.Entries) {
                    if (!entry.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8)) return reader.ReadToEnd();
                }
            }
        } catch { }
        return "";
    }

    readonly float scale;
    readonly string stateFile;
    TextBox rootBox, gameBox, logBox;
    Label gameStatus, statusLabel;
    ComboBox coreMode;
    ComboBox resolution, frameRate, upscaler, preset, live, modelLod, language, presentMode;
    ComboBox qualityProfile;
    CheckBox fullscreen, showFps, sharpen, desktopLink, startMenuLink, oldHunters;
    CheckBox mouseEnabled, mouseInvert;
    NumericUpDown mouseSensitivity;
    ComboBox mouseLeft, mouseRight, mouseMiddle, mouseX1, mouseX2, mouseWheelUp, mouseWheelDown;
    readonly Dictionary<string, ComboBox> keyBindings = new Dictionary<string, ComboBox>();
    readonly Dictionary<string, ComboBox> gamepadBindings = new Dictionary<string, ComboBox>();
    readonly List<KeyValuePair<string, CheckBox>> effects = new List<KeyValuePair<string, CheckBox>>();
    readonly Dictionary<string, CheckBox> cheats = new Dictionary<string, CheckBox>();
    TextBox saveRootBox;
    CheckBox autoBackup;
    TextBox lanAddressBox, lanNpidBox, lanPasswordBox, lanEmailBox, lanSecretBox;
    TextBox onlineNpidBox, onlinePasswordBox, onlineQqBox, onlineNicknameBox;
    CheckBox onlineShowInList;
    Label onlineServiceStatus, onlineCountStatus, onlineLoginStatus, onlineApplicationStatus,
        onlineTailscaleStatus, onlineHeadscaleStatus;
    ListView onlineUsersList;
    Button onlineRefreshButton, onlineApplyButton, onlineQueryButton, onlineRegisterButton,
        onlineTailscaleButton, onlineHeadscaleButton;
    string onlineApplicationId = "", onlineApplicationToken = "", onlineRegistrationKey = "";
    CheckBox lanHost, lanSeamless;
    Label lanStatus;
    Button lanStartServerButton, lanStopServerButton, lanTestButton, lanRegisterButton;
    Label saveStatus;
    ListView backupList;
    ListView characterList;
    Button renameCharacterButton;
    Button backupButton, restoreButton, importFolderButton, importZipButton, exportButton, refreshSaveButton;
    Button installButton, saveButton, playButton;
    TabControl tabs;
    TabPage logPage;
    ProgressBar progress;
    volatile bool busy;
    Process launcherProcess;
    Process shadNetProcess;
    Process embeddedNetProcess;
    volatile string embeddedNetIp = "";
    string lastEmbeddedAuthUrl = "";
    readonly object shadNetLogLock = new object();
    string lastRunLog;
    string expectedProbeHash;
    readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();
    readonly System.Windows.Forms.Timer pump = new System.Windows.Forms.Timer();
    readonly string welcomeFile;
    bool currentRunPublic;
    volatile bool publicLoginSucceeded;

    static Choice[] BuildKeyChoices() {
        List<Choice> keys = new List<Choice>();
        keys.Add(new Choice("不绑定", "0"));
        string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        for (int i = 0; i < letters.Length; ++i) keys.Add(new Choice(letters[i].ToString(), (4 + i).ToString()));
        for (int i = 1; i <= 9; ++i) keys.Add(new Choice(i.ToString(), (29 + i).ToString()));
        keys.Add(new Choice("0", "39"));
        keys.AddRange(new Choice[] {
            new Choice("Enter", "40"), new Choice("Esc", "41"), new Choice("Backspace", "42"),
            new Choice("Tab", "43"), new Choice("Space", "44"), new Choice("-", "45"),
            new Choice("=", "46"), new Choice("[", "47"), new Choice("]", "48"),
            new Choice("\\", "49"), new Choice("非美式 #", "50"), new Choice(";", "51"),
            new Choice("'", "52"), new Choice("`", "53"), new Choice(",", "54"),
            new Choice(".", "55"), new Choice("/", "56"), new Choice("Caps Lock", "57"),
            new Choice("F1", "58"), new Choice("F2", "59"), new Choice("F3", "60"),
            new Choice("F4", "61"), new Choice("F5", "62"), new Choice("F6", "63"),
            new Choice("F7", "64"), new Choice("F8", "65"), new Choice("F9", "66"),
            new Choice("F10（同时切换鼠标锁定）", "67"), new Choice("F11（同时切换全屏）", "68"),
            new Choice("F12", "69"), new Choice("Print Screen", "70"),
            new Choice("Scroll Lock", "71"), new Choice("Pause", "72"),
            new Choice("Insert", "73"), new Choice("Home", "74"), new Choice("Page Up", "75"),
            new Choice("Delete", "76"), new Choice("End", "77"), new Choice("Page Down", "78"),
            new Choice("右方向键", "79"), new Choice("左方向键", "80"),
            new Choice("下方向键", "81"), new Choice("上方向键", "82"),
            new Choice("Num Lock", "83"), new Choice("小键盘 /", "84"),
            new Choice("小键盘 *", "85"), new Choice("小键盘 -", "86"),
            new Choice("小键盘 +", "87"), new Choice("小键盘 Enter", "88"),
            new Choice("小键盘 1", "89"), new Choice("小键盘 2", "90"),
            new Choice("小键盘 3", "91"), new Choice("小键盘 4", "92"),
            new Choice("小键盘 5", "93"), new Choice("小键盘 6", "94"),
            new Choice("小键盘 7", "95"), new Choice("小键盘 8", "96"),
            new Choice("小键盘 9", "97"), new Choice("小键盘 0", "98"),
            new Choice("小键盘 .", "99"), new Choice("非美式 \\" , "100"),
            new Choice("Application/Menu", "101"), new Choice("小键盘 =", "103"),
            new Choice("F13", "104"), new Choice("F14", "105"), new Choice("F15", "106"),
            new Choice("F16", "107"), new Choice("F17", "108"), new Choice("F18", "109"),
            new Choice("F19", "110"), new Choice("F20", "111"), new Choice("F21", "112"),
            new Choice("F22", "113"), new Choice("F23", "114"), new Choice("F24", "115"),
            new Choice("菜单键", "118"), new Choice("静音", "127"),
            new Choice("音量加", "128"), new Choice("音量减", "129"),
            new Choice("左 Ctrl", "224"), new Choice("左 Shift", "225"), new Choice("左 Alt", "226"),
            new Choice("左 Win", "227"), new Choice("右 Ctrl", "228"),
            new Choice("右 Shift", "229"), new Choice("右 Alt", "230"), new Choice("右 Win", "231"),
            new Choice("睡眠", "258"), new Choice("唤醒", "259"),
            new Choice("媒体播放", "262"), new Choice("媒体暂停", "263"),
            new Choice("媒体下一首", "267"), new Choice("媒体上一首", "268"),
            new Choice("媒体停止", "269"), new Choice("媒体播放/暂停", "271"),
            new Choice("浏览器搜索", "280"), new Choice("浏览器主页", "281"),
            new Choice("浏览器后退", "282"), new Choice("浏览器前进", "283"),
            new Choice("浏览器刷新", "285") });
        return keys.ToArray();
    }

    static string[][] LoadEffects() {
        string text = "";
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("bbport_settings_table.inc");
        if (stream != null) using (stream) using (StreamReader reader = new StreamReader(stream)) text = reader.ReadToEnd();
        foreach (Match m in Regex.Matches(text, @"^BB_SETTING\(""(\w+)"", ""([^""]*)""\)", RegexOptions.Multiline))
            Defaults[m.Groups[1].Value] = m.Groups[2].Value;
        List<string[]> result = new List<string[]>();
        foreach (Match m in Regex.Matches(text, @"^BB_EFFECT\(""(\w+)"", ""((?:[^""\\]|\\.)*)"", ([01])\)", RegexOptions.Multiline)) {
            if (!m.Groups[1].Value.StartsWith("debug_"))
                result.Add(new string[] { m.Groups[1].Value, Regex.Unescape(m.Groups[2].Value), m.Groups[3].Value });
        }
        return result.ToArray();
    }

    public MainForm(string rootArgument) {
        using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
        string stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BloodbornePC");
        stateFile = Path.Combine(stateDir, "launcher.ini");
        welcomeFile = Path.Combine(stateDir, "welcome-v1.txt");
        Text = TitleText + (PackageVersion.Length == 0 ? "" : "  v" + PackageVersion);
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(S(800), S(730));
        MinimumSize = new Size(S(680), S(580));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BuildUi();
        LoadSettings(rootArgument);
        pump.Interval = 100;
        pump.Tick += delegate { DrainLog(); };
        pump.Start();
        FormClosing += OnClosing;
        Shown += delegate { ShowWelcomeOnce(); };
    }

    static Image LauncherBackground() {
        try {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("bbport_launcher_background.png"))
            using (Image source = stream == null ? null : Image.FromStream(stream))
                return source == null ? null : new Bitmap(source);
        } catch { return null; }
    }

    static bool UpscalerUsesPreset(string value) {
        return value == "fsr3" || value == "fsr4" || value == "fsr411" || value == "dlss";
    }

    int S(int value) { return (int)Math.Round(value * scale); }

    void BuildUi() {
        Image background = LauncherBackground();
        tabs = new TabControl { Dock = DockStyle.Fill };
        TabPage settingsPage = new TabPage("设置") { AutoScroll = true, Padding = new Padding(S(8)) };
        TabPage lanPage = new TabPage("局域网联机") { AutoScroll = true, Padding = new Padding(S(8)) };
        TabPage onlinePage = new TabPage("在线联机") { AutoScroll = true, Padding = new Padding(S(8)) };
        TabPage inputPage = new TabPage("输入与手柄") { AutoScroll = true, Padding = new Padding(S(8)) };
        TabPage savePage = new TabPage("存档管理") { AutoScroll = true, Padding = new Padding(S(8)) };
        TabPage cheatPage = new TabPage("金手指") { AutoScroll = true, Padding = new Padding(S(8)) };
        TabPage versionPage = new TabPage("版本与更新") { Padding = new Padding(S(8)) };
        TabPage helpPage = new TabPage("帮助") { AutoScroll = true, Padding = new Padding(S(8)) };
        logPage = new TabPage("运行日志") { Padding = new Padding(S(4)) };
        tabs.TabPages.Add(settingsPage);
        tabs.TabPages.Add(lanPage);
        tabs.TabPages.Add(onlinePage);
        tabs.TabPages.Add(inputPage);
        tabs.TabPages.Add(savePage);
        tabs.TabPages.Add(cheatPage);
        tabs.TabPages.Add(versionPage);
        tabs.TabPages.Add(helpPage);
        tabs.TabPages.Add(logPage);
        tabs.SelectedIndexChanged += delegate {
            if (tabs.SelectedTab == onlinePage) RefreshPublicStatus();
        };
        if (background != null) {
            foreach (TabPage page in tabs.TabPages) {
                page.BackgroundImage = background;
                page.BackgroundImageLayout = ImageLayout.Stretch;
            }
        }
        TableLayoutPanel column = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink };
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsPage.Controls.Add(column);

        TableLayoutPanel folders = Group("位置", 3);
        rootBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(folders, "运行环境目录", rootBox, Browse(rootBox, "选择运行环境的安装位置"));
        AddSpan(folders, Note("程序、运行库、存档、模组和配置保存在这里；不会复制或修改游戏本体。建议至少预留 450 MB。"));
        gameBox = new TextBox { Dock = DockStyle.Fill };
        gameBox.TextChanged += delegate { CheckGame(); };
        AddRow(folders, "游戏本体目录", gameBox, Browse(gameBox, "选择包含 eboot.bin 和 sce_sys 的 Bloodborne v1.09 游戏目录"));
        gameStatus = Note("");
        AddSpan(folders, gameStatus);
        column.Controls.Add(folders.Parent);

        TableLayoutPanel core = Group("启动核心", 2);
        coreMode = Combo(core, "运行模式", CoreModes);
        coreMode.SelectedIndexChanged += delegate {
            string selected = Value(coreMode);
            playButton.Text = selected == "public_online" ? "启动在线联机" :
                selected == "native_lan" ? "启动局域网联机" :
                selected == "shadp2p" ? "启动 shadp2p 联机" : "启动游戏";
            if (saveRootBox != null) RefreshSaveManager();
        };
        AddSpan(core, Note("“局域网原生联机”连接自己填写的 shadNet；“在线联机”使用安装包内预配置的服务。" +
            (PublicServiceIsLoopback() ? "当前为本机 Docker 测试，仅供同机使用。" : "") +
            "两者都基于 bbport 的网络协议栈；独立 shadp2p 核心仍可用于兼容测试。"));
        column.Controls.Add(core.Parent);

        TableLayoutPanel recommended = Group("推荐配置", 2);
        qualityProfile = Combo(recommended, "方案", QualityProfiles);
        Button applyProfile = new Button { Text = "应用推荐配置", AutoSize = true };
        applyProfile.Click += delegate { ApplyQualityProfile(Value(qualityProfile)); };
        AddSpan(recommended, Flow(applyProfile));
        AddSpan(recommended, Note("推荐方案会一次设置分辨率、升频质量、模型细节和特效；应用后仍可逐项修改。"));
        column.Controls.Add(recommended.Parent);

        TableLayoutPanel display = Group("显示", 2);
        resolution = Combo(display, "输出分辨率", Resolutions);
        frameRate = Combo(display, "帧率", FrameRates);
        presentMode = Combo(display, "呈现模式", PresentModes);
        fullscreen = Check("无边框全屏（游戏中按 F11 切换）");
        showFps = Check("在画面角落显示 FPS");
        AddSpan(display, Flow(fullscreen, showFps));
        column.Controls.Add(display.Parent);

        TableLayoutPanel image = Group("画质", 2);
        upscaler = Combo(image, "升频方式", Upscalers);
        upscaler.SelectedIndexChanged += delegate { preset.Enabled = UpscalerUsesPreset(Value(upscaler)); };
        preset = Combo(image, "质量预设", Presets);
        live = Combo(image, "实时切换分辨率", LiveModes);
        modelLod = Combo(image, "模型细节", ModelDetail);
        sharpen = Check("锐化（RCAS）");
        AddSpan(image, sharpen);
        AddSpan(image, Note("关闭实时切换通常性能更好；改变预设或输出分辨率后需要重启游戏。FSR 4 v07 和 DLSS 依赖已随包安装；FSR 4.1.1 因授权限制只能读取用户自行从 AMD DLL 提取的 fsr4_411 模型。"));
        column.Controls.Add(image.Parent);

        TableLayoutPanel effectGroup = Group("游戏效果", 2);
        TableLayoutPanel effectGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        effectGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        effectGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (string[] definition in EffectDefinitions) {
            string label;
            if (!EffectChinese.TryGetValue(definition[0], out label)) label = definition[1];
            CheckBox box = Check(label);
            effects.Add(new KeyValuePair<string, CheckBox>(definition[0], box));
            effectGrid.Controls.Add(box);
        }
        AddSpan(effectGroup, effectGrid);
        column.Controls.Add(effectGroup.Parent);

        TableLayoutPanel content = Group("游戏内容", 2);
        oldHunters = Check("启用《老猎人》DLC 授权（需要自己的 DLC／年度版游戏）");
        AddSpan(content, oldHunters);
        AddSpan(content, Note("CUSA03173 1.09 的 DLC 数据已包含在更新中；该选项只创建游戏识别授权所需的兼容目录，不下载或提供任何游戏文件。"));
        column.Controls.Add(content.Parent);

        TableLayoutPanel extras = Group("语言与快捷方式", 2);
        language = Combo(extras, "游戏语言", Languages);
        desktopLink = Check("创建桌面快捷方式");
        startMenuLink = Check("创建开始菜单快捷方式");
        AddSpan(extras, Flow(desktopLink, startMenuLink));
        AddSpan(extras, Note("启动器界面为中文；当前上游游戏内设置菜单只提供英语和俄语。"));
        column.Controls.Add(extras.Parent);

        TableLayoutPanel lanColumn = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink };
        lanColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lanPage.Controls.Add(lanColumn);

        TableLayoutPanel lanConnection = Group("shadNet 联机连接", 2);
        lanAddressBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(lanConnection, "服务器地址", lanAddressBox, null);
        lanNpidBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 16 };
        AddRow(lanConnection, "联机 ID（NPID）", lanNpidBox, null);
        lanPasswordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        AddRow(lanConnection, "联机密码", lanPasswordBox, null);
        lanEmailBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(lanConnection, "注册邮箱", lanEmailBox, null);
        lanSeamless = Check("实验性无缝联机（所有玩家和服务端必须同时开启）");
        AddSpan(lanConnection, lanSeamless);
        AddSpan(lanConnection, Note("服务器地址填写开服电脑的局域网 IPv4，例如 192.168.1.20；不要填写 0.0.0.0。传统联机仍按游戏原有区域、钟和 Boss 限制运行。"));
        AddSpan(lanConnection, Note("局域网原生模式会实际登录这里填写的 shadNet 账号，并连接 TCP 31313、UDP 31314 和 WebAPI TCP 31315；联机 ID 与密码必须是该服务器已经注册的账号。"));
        AddSpan(lanConnection, Note("传统模式匹配要求：双方游戏内联机密码一致、区域匹配设为全球、位于可联机且相同的区域、主机 Boss 尚未击杀，并满足等级与距离限制。诊断时可让服务端和所有客户端同时开启实验性无缝联机，以排除区域、等级和距离过滤。"));
        lanColumn.Controls.Add(lanConnection.Parent);

        TableLayoutPanel lanServer = Group("本机 shadNet 服务端", 2);
        lanHost = Check("本机同时作为局域网服务器（启动游戏时自动启动）");
        AddSpan(lanServer, lanHost);
        lanSecretBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        AddRow(lanServer, "注册密钥", lanSecretBox, null);
        FlowLayoutPanel lanButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        lanStartServerButton = new Button { Text = "启动本机服务器", AutoSize = true };
        lanStopServerButton = new Button { Text = "停止本机服务器", AutoSize = true, Enabled = false };
        lanTestButton = new Button { Text = "检测服务器", AutoSize = true };
        lanRegisterButton = new Button { Text = "注册此账号", AutoSize = true };
        lanStartServerButton.Click += delegate { StartLanServerFromUi(); };
        lanStopServerButton.Click += delegate { StopLanServer(); };
        lanTestButton.Click += delegate { TestLanServerFromUi(); };
        lanRegisterButton.Click += delegate { RegisterLanAccountFromUi(); };
        lanButtons.Controls.AddRange(new Control[] { lanStartServerButton, lanStopServerButton,
            lanTestButton, lanRegisterButton });
        AddSpan(lanServer, lanButtons);
        lanStatus = Note("尚未检测服务器。");
        AddSpan(lanServer, lanStatus);
        AddSpan(lanServer, Note("仅限可信局域网：TCP 31313、UDP 31314、TCP 31315。shadNet 的 TCP 游戏协议本身不加密，请勿在路由器上做公网端口转发。每位玩家必须使用不同的联机 ID。"));
        lanColumn.Controls.Add(lanServer.Parent);

        TableLayoutPanel lanCompatibility = Group("兼容性与存档", 2);
        AddSpan(lanCompatibility, Note("原生 bbport 在线层移植自 shadp2p/shadNet 的完整协议实现，已测试目标仅为 CUSA03173 1.09。其他区域版本可尝试，但启动器会先警告。"));
        AddSpan(lanCompatibility, Note("联机核心存档固定保存在“玩家数据目录\\shadp2p-home\\1000\\savedata”。选择联机模式后，存档管理页会自动管理和备份这一套存档，不会覆盖原生单机存档。"));
        lanColumn.Controls.Add(lanCompatibility.Parent);

        TableLayoutPanel onlineColumn = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        onlineColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        onlinePage.Controls.Add(onlineColumn);
        TableLayoutPanel onlineOverview = Group(PublicServiceIsLoopback() ? "本机 Docker 测试服务" : "公共在线服务", 2);
        if (PublicServiceIsLoopback()) {
            Label localOnly = Note("当前是本机 Docker 测试包：127.0.0.1 指这台电脑。另一台电脑运行同一个包，只会连接它自己，不能用于异地联机。");
            localOnly.ForeColor = Color.Firebrick;
            AddSpan(onlineOverview, localOnly);
        }
        onlineServiceStatus = Note("服务状态：尚未检测");
        onlineCountStatus = Note("在线人数：暂不可用");
        onlineLoginStatus = Note("登录状态：未登录");
        AddSpan(onlineOverview, onlineServiceStatus);
        AddSpan(onlineOverview, onlineCountStatus);
        AddSpan(onlineOverview, onlineLoginStatus);
        onlineRefreshButton = new Button { Text = "刷新服务状态", AutoSize = true };
        onlineRefreshButton.Click += delegate { RefreshPublicStatus(); };
        AddSpan(onlineOverview, Flow(onlineRefreshButton));
        onlineUsersList = new ListView { View = View.Details, FullRowSelect = true, Height = S(190),
            Width = S(690), HeaderStyle = ColumnHeaderStyle.Nonclickable };
        onlineUsersList.Columns.Add("联机 ID", S(150));
        onlineUsersList.Columns.Add("公开昵称", S(170));
        onlineUsersList.Columns.Add("当前游戏", S(300));
        AddSpan(onlineOverview, onlineUsersList);
        AddSpan(onlineOverview, Note("在线人数以服务端实际返回为准；服务端尚未提供人数接口时显示“暂不可用”，不会把连通性当作已登录。"));
        AddSpan(onlineOverview, Note("公开列表只展示同意公开的在线玩家；QQ 号不会出现在列表中。服务器统计包含未公开名单的在线人数。"));
        onlineColumn.Controls.Add(onlineOverview.Parent);
        TableLayoutPanel onlineAccount = Group("我的在线账号", 2);
        onlineNpidBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 16 };
        onlinePasswordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        onlineQqBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 12 };
        onlineNicknameBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 24 };
        AddRow(onlineAccount, "联机 ID（NPID）", onlineNpidBox, null);
        AddRow(onlineAccount, "账号密码", onlinePasswordBox, null);
        AddRow(onlineAccount, "QQ 号（申请时填写）", onlineQqBox, null);
        AddRow(onlineAccount, "昵称（申请时填写）", onlineNicknameBox, null);
        onlineShowInList = Check("同意在在线玩家列表中展示我的联机 ID、昵称和当前游戏");
        AddSpan(onlineAccount, onlineShowInList);
        FlowLayoutPanel onlineAccountButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill,
            WrapContents = true };
        onlineApplyButton = new Button { Text = "申请一次性注册码", AutoSize = true };
        onlineQueryButton = new Button { Text = "查询申请进度", AutoSize = true };
        onlineRegisterButton = new Button { Text = "完成账号注册", AutoSize = true };
        onlineApplyButton.Click += delegate { ApplyOnlineAccount(); };
        onlineQueryButton.Click += delegate { QueryOnlineApplication(); };
        onlineRegisterButton.Click += delegate { RegisterOnlineAccount(); };
        onlineAccountButtons.Controls.AddRange(new Control[] { onlineApplyButton, onlineQueryButton,
            onlineRegisterButton });
        AddSpan(onlineAccount, onlineAccountButtons);
        onlineApplicationStatus = Note("申请状态：尚未申请");
        AddSpan(onlineAccount, onlineApplicationStatus);
        Button useOnline = new Button { Text = "使用在线联机模式", AutoSize = true };
        useOnline.Click += delegate { SelectValue(coreMode, "public_online"); tabs.SelectedTab = onlinePage; };
        AddSpan(onlineAccount, Flow(useOnline));
        AddSpan(onlineAccount, Note("注册方法：填写 QQ 号、昵称和联机 ID，申请个人一次性注册码。正式服务默认由管理员审核；获批后点“查询申请进度”，再输入密码完成注册。服务器只保存 QQ 的不可逆摘要及末四位，用于去重和频率统计，不公开 QQ 号。"));
        AddSpan(onlineAccount, Link("打开作者主页，查看互助与账号申请方式", "https://space.bilibili.com/18955882?spm_id_from=333.40164.0.0"));
        AddSpan(onlineAccount, Note("交流 QQ 群：945992968（一群，已满）；892729104（二群，可加千人群讨论）。入群问题答案：细雨寒言。"));
        AddSpan(onlineAccount, Note("启动器只显示连接状态；真正的登录状态在启动游戏后依据核心认证结果更新。" +
            (PublicServiceIsLoopback() ? "本机 Docker 测试包不能供异地玩家直接联机。" : "")));
        onlineColumn.Controls.Add(onlineAccount.Parent);
        TableLayoutPanel onlineGuide = Group("联机步骤", 2);
        AddSpan(onlineGuide, Note("1. 与朋友安装同一版本并分别领取账号。\r\n2. 确认两台电脑具备互相可达的安全网络连接。\r\n3. 游戏内使用相同联机密码，在可联机区域由房主摇召唤钟，另一人摇小共鸣钟。\r\n4. 灰色铃铛通常意味着当前区域或游戏状态不允许召唤。"));
        AddSpan(onlineGuide, Note("跨互联网测试需要每位玩家加入同一私网；Headscale 负责设备入网，shadNet 仍负责游戏账号与召唤。只连得上控制服务器而玩家之间不通，仍可能无法召唤。"));
        onlineHeadscaleStatus = Note("自建私网控制服务：尚未检测");
        AddSpan(onlineGuide, onlineHeadscaleStatus);
        onlineTailscaleStatus = Note("内置私网：尚未连接（无需安装系统级 Tailscale）");
        AddSpan(onlineGuide, onlineTailscaleStatus);
        onlineHeadscaleButton = new Button { Text = "连接内置私网", AutoSize = true };
        onlineHeadscaleButton.Click += delegate { ConnectHeadscale(); };
        onlineTailscaleButton = new Button { Text = "检测内置私网", AutoSize = true };
        onlineTailscaleButton.Click += delegate { RefreshTailscaleStatus(); };
        AddSpan(onlineGuide, Flow(onlineHeadscaleButton, onlineTailscaleButton));
        AddSpan(onlineGuide, Note("启动器附带独立的 userspace 私网节点，不安装虚拟网卡，也不切换电脑现有的 Tailscale 网络。首次连接需在浏览器完成设备授权，并由管理员审批；不会保存共享入网密钥。当前仅限本地测试，玩家互通仍需双机验证。"));
        onlineColumn.Controls.Add(onlineGuide.Parent);

        TableLayoutPanel helpColumn = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        helpColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        helpPage.Controls.Add(helpColumn);
        TableLayoutPanel helpAbout = Group("关于本项目", 2);
        AddSpan(helpAbout, Note("这是非盈利性质的爱好者项目，不销售游戏、账号或联机资格，也不提供游戏本体。请使用自己合法取得的游戏文件；联机和存档修改仍处于实验阶段，请先备份存档。"));
        AddSpan(helpAbout, Note("启动器整合与维护：哔哩哔哩 细雨寒言。底层 bbport、shadp2p/shadNet 由各自上游作者开发；许可与第三方说明请见安装包内的 LICENSE 文件。"));
        AddSpan(helpAbout, Link("作者主页：哔哩哔哩 细雨寒言", "https://space.bilibili.com/18955882?spm_id_from=333.40164.0.0"));
        AddSpan(helpAbout, Link("互帮互助群入口（作者主页）", "https://space.bilibili.com/18955882?spm_id_from=333.1387.0.0"));
        AddSpan(helpAbout, Note("QQ 交流：945992968（一群已满）；892729104（二群，千人群）。入群问题答案是作者昵称：细雨寒言。"));
        helpColumn.Controls.Add(helpAbout.Parent);

        TableLayoutPanel inputColumn = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink };
        inputColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputPage.Controls.Add(inputColumn);

        TableLayoutPanel movement = Group("移动与视角键", 2);
        AddKey(movement, "向前", "MOVE_UP", "26");
        AddKey(movement, "向后", "MOVE_DOWN", "22");
        AddKey(movement, "向左", "MOVE_LEFT", "4");
        AddKey(movement, "向右", "MOVE_RIGHT", "7");
        AddKey(movement, "视角上", "LOOK_UP", "82");
        AddKey(movement, "视角下", "LOOK_DOWN", "81");
        AddKey(movement, "视角左", "LOOK_LEFT", "80");
        AddKey(movement, "视角右", "LOOK_RIGHT", "79");
        inputColumn.Controls.Add(movement.Parent);

        TableLayoutPanel actions = Group("动作键", 2);
        AddKey(actions, "确定／互动 ×", "CROSS", "44");
        AddKey(actions, "取消／闪避 ○", "CIRCLE", "225");
        AddKey(actions, "使用物品 □", "SQUARE", "8");
        AddKey(actions, "切换武器 △", "TRIANGLE", "20");
        AddKey(actions, "L1", "L1", "30");
        AddKey(actions, "R1", "R1", "32");
        AddKey(actions, "L2", "L2", "21");
        AddKey(actions, "R2", "R2", "9");
        AddKey(actions, "L3", "L3", "29");
        AddKey(actions, "R3／锁定目标", "R3", "6");
        AddKey(actions, "Options", "OPTIONS", "40");
        AddKey(actions, "十字键上", "DPAD_UP", "12");
        AddKey(actions, "十字键下", "DPAD_DOWN", "14");
        AddKey(actions, "十字键左", "DPAD_LEFT", "13");
        AddKey(actions, "十字键右", "DPAD_RIGHT", "15");
        AddKey(actions, "Select／姿势菜单（触摸板左侧）", "TOUCH_LEFT", "43");
        AddKey(actions, "触摸板右侧", "TOUCH_RIGHT", "42");
        AddSpan(actions, Note("Xbox 的 View／Select、DualShock／DualSense 的触摸板左侧会打开姿势菜单；支持陀螺仪的手柄也会传递动作传感器数据。"));
        inputColumn.Controls.Add(actions.Parent);

        TableLayoutPanel gamepad = Group("手柄映射", 2);
        foreach (string[] definition in GamepadDefinitions) AddGamepad(gamepad, definition);
        Button resetGamepad = new Button { Text = "恢复默认手柄映射", AutoSize = true };
        resetGamepad.Click += delegate { ResetGamepadMappings(); };
        AddSpan(gamepad, Flow(resetGamepad));
        AddSpan(gamepad, Note("支持 Xbox、DualShock／DualSense 和 SDL 兼容手柄。扳机可映射到任意动作；View／Select 默认对应触摸板左侧姿势菜单。改键后保存设置并重新启动游戏。"));
        inputColumn.Controls.Add(gamepad.Parent);

        TableLayoutPanel mouse = Group("鼠标", 2);
        mouseEnabled = Check("启用鼠标控制视角并在游戏窗口中锁定指针");
        mouseInvert = Check("反转鼠标 Y 轴");
        AddSpan(mouse, Flow(mouseEnabled, mouseInvert));
        mouseSensitivity = new NumericUpDown { DecimalPlaces = 1, Increment = 0.1M, Minimum = 0.1M,
            Maximum = 20M, Value = 3M, Width = S(100) };
        AddRow(mouse, "鼠标灵敏度", mouseSensitivity, null);
        mouseLeft = Combo(mouse, "鼠标左键", MouseActions);
        mouseRight = Combo(mouse, "鼠标右键", MouseActions);
        mouseMiddle = Combo(mouse, "鼠标中键", MouseActions);
        mouseX1 = Combo(mouse, "鼠标侧键 1", MouseActions);
        mouseX2 = Combo(mouse, "鼠标侧键 2", MouseActions);
        mouseWheelUp = Combo(mouse, "滚轮向上", MouseActions);
        mouseWheelDown = Combo(mouse, "滚轮向下", MouseActions);
        AddSpan(mouse, Note("F10 可随时释放或重新锁定鼠标；打开游戏内设置菜单、文字输入或切出窗口时也会自动释放。键盘、鼠标和手柄可以同时使用。"));
        inputColumn.Controls.Add(mouse.Parent);

        TableLayoutPanel saveColumn = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink };
        saveColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        savePage.Controls.Add(saveColumn);
        TableLayoutPanel saveLocation = Group("独立存档目录", 3);
        saveRootBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(saveLocation, "玩家数据目录", saveRootBox, Browse(saveRootBox, "选择独立的玩家数据和存档目录"));
        autoBackup = Check("启动前和退出后自动备份存档（最多保留 20 份自动备份）");
        AddSpan(saveLocation, autoBackup);
        AddSpan(saveLocation, Note("存档不再跟随运行环境目录。首次启动新版时，会把旧运行环境 user 目录中的存档安全复制到这里。"));
        saveStatus = Note("尚未读取存档。");
        AddSpan(saveLocation, saveStatus);
        saveColumn.Controls.Add(saveLocation.Parent);

        TableLayoutPanel characterGroup = Group("角色存档", 2);
        characterList = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false,
            Height = S(145), Dock = DockStyle.Fill };
        characterList.Columns.Add("槽位", S(70));
        characterList.Columns.Add("文件", S(170));
        characterList.Columns.Add("角色名", S(210));
        characterList.Columns.Add("最后修改", S(160));
        characterList.SelectedIndexChanged += delegate {
            renameCharacterButton.Enabled = !busy && characterList.SelectedItems.Count > 0;
        };
        AddSpan(characterGroup, characterList);
        renameCharacterButton = new Button { Text = "修改选中角色名…", AutoSize = true, Enabled = false };
        renameCharacterButton.Click += delegate { RenameSelectedCharacter(); };
        AddSpan(characterGroup, Flow(renameCharacterButton));
        AddSpan(characterGroup, Note("改名前自动生成 ZIP 备份；角色名按游戏格式限制为 1–16 个英文字符。兼容 userdata0000、userdata_0000、userdata-0000 等常见文件名，导入时会自动规范化。"));
        saveColumn.Controls.Add(characterGroup.Parent);

        TableLayoutPanel saveActions = Group("备份与外部存档", 2);
        backupList = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false,
            Height = S(230), Dock = DockStyle.Fill };
        backupList.Columns.Add("备份文件", S(360));
        backupList.Columns.Add("时间", S(150));
        backupList.Columns.Add("大小", S(100));
        backupList.SelectedIndexChanged += delegate { restoreButton.Enabled = !busy && backupList.SelectedItems.Count > 0; };
        AddSpan(saveActions, backupList);
        FlowLayoutPanel saveButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        refreshSaveButton = new Button { Text = "刷新", AutoSize = true };
        backupButton = new Button { Text = "立即备份", AutoSize = true };
        restoreButton = new Button { Text = "恢复选中备份", AutoSize = true, Enabled = false };
        exportButton = new Button { Text = "导出当前存档…", AutoSize = true };
        importFolderButton = new Button { Text = "验证／导入外部文件夹…", AutoSize = true };
        importZipButton = new Button { Text = "验证／导入 ZIP…", AutoSize = true };
        refreshSaveButton.Click += delegate { RefreshSaveManager(); };
        backupButton.Click += delegate { ManualBackup(); };
        restoreButton.Click += delegate { RestoreSelectedBackup(); };
        exportButton.Click += delegate { ExportCurrentSave(); };
        importFolderButton.Click += delegate { ImportExternalFolder(); };
        importZipButton.Click += delegate { ImportExternalZip(); };
        saveButtons.Controls.AddRange(new Control[] { refreshSaveButton, backupButton, restoreButton, exportButton,
            importFolderButton, importZipButton });
        AddSpan(saveActions, saveButtons);
        AddSpan(saveActions, Note("导入前会校验 userdata 文件数量和固定大小，并自动备份当前进度。不同 CUSA 区域的存档会放入当前游戏的区域目录。"));
        saveColumn.Controls.Add(saveActions.Parent);

        TableLayoutPanel cheatColumn = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink };
        cheatColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cheatPage.Controls.Add(cheatColumn);
        TableLayoutPanel cheatGroup = Group("内置金手指", 2);
        foreach (string[] definition in CheatDefinitions) {
            CheckBox box = Check(definition[1]);
            cheats[definition[0]] = box;
            AddSpan(cheatGroup, box);
        }
        cheats["infinite_health"].CheckedChanged += delegate {
            if (cheats["infinite_health"].Checked) cheats["no_dead"].Checked = false;
        };
        cheats["no_dead"].CheckedChanged += delegate {
            if (cheats["no_dead"].Checked) cheats["infinite_health"].Checked = false;
        };
        cheats["enemy_control"].CheckedChanged += delegate {
            if (cheats["enemy_control"].Checked) {
                cheats["debug_camera"].Checked = false;
                statusLabel.Text = "已关闭“调试相机”：它与“敌人控制”不能同时启用。";
            }
        };
        cheats["debug_camera"].CheckedChanged += delegate {
            if (cheats["debug_camera"].Checked) {
                cheats["enemy_control"].Checked = false;
                statusLabel.Text = "已关闭“敌人控制”：它与“调试相机”不能同时启用。";
            }
        };
        Button commonCheats = new Button { Text = "启用常用辅助", AutoSize = true };
        Button clearCheats = new Button { Text = "全部关闭", AutoSize = true };
        commonCheats.Click += delegate {
            foreach (string[] definition in CheatDefinitions) cheats[definition[0]].Checked = definition[3] == "safe";
        };
        clearCheats.Click += delegate { foreach (CheckBox box in cheats.Values) box.Checked = false; };
        AddSpan(cheatGroup, Flow(commonCheats, clearCheats));
        AddSpan(cheatGroup, Note("金手指通过 v1.09 内置补丁实现，只建议单机使用。无限血、无限回响和强化材料已在 CUSA03173 v1.09 验证，其他区域视为实验功能；“强化材料”会同时影响其他物品数量。“敌人控制”和“调试相机”会自动互斥。启用任意项目时，启动前都会额外创建存档备份。"));
        cheatColumn.Controls.Add(cheatGroup.Parent);

        TableLayoutPanel versionColumn = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill,
            Padding = new Padding(S(4)) };
        versionColumn.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        versionColumn.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        versionColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label versionLabel = new Label { AutoSize = true, Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold), Padding = new Padding(0, 0, 0, S(4)),
            Text = "当前安装包版本：v" + (PackageVersion.Length == 0 ? "未知" : PackageVersion) };
        Label versionHint = new Label { AutoSize = true, Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText, Padding = new Padding(0, 0, 0, S(8)),
            Text = "最新版本置顶；每条改动单独显示，向下滚动可查看历次更新。" };
        RichTextBox notes = new RichTextBox { ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill,
            BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, DetectUrls = false,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Regular) };
        FormatReleaseNotes(notes);
        versionColumn.Controls.Add(versionLabel, 0, 0);
        versionColumn.Controls.Add(versionHint, 0, 1);
        versionColumn.Controls.Add(notes, 0, 2);
        versionPage.Controls.Add(versionColumn);

        logBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
        logPage.Controls.Add(logBox);

        TableLayoutPanel bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 1, AutoSize = true,
            Padding = new Padding(S(8), S(4), S(8), S(8)) };
        statusLabel = new Label { AutoSize = true, Text = "请选择游戏本体目录，然后安装运行环境。" };
        progress = new ProgressBar { Dock = DockStyle.Fill, Height = S(14), Style = ProgressBarStyle.Continuous };
        FlowLayoutPanel buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        Button closeButton = new Button { Text = "关闭", AutoSize = true };
        closeButton.Click += delegate { Close(); };
        playButton = new Button { Text = "启动游戏", AutoSize = true };
        playButton.Click += delegate { StartGame(); };
        saveButton = new Button { Text = "保存设置", AutoSize = true };
        saveButton.Click += delegate { SaveSettingsOnly(); };
        installButton = new Button { Text = "安装 / 更新运行环境", AutoSize = true };
        installButton.Click += delegate { StartInstall(); };
        buttons.Controls.AddRange(new Control[] { closeButton, playButton, saveButton, installButton });
        bottom.Controls.Add(statusLabel);
        bottom.Controls.Add(progress);
        bottom.Controls.Add(buttons);
        Controls.Add(tabs);
        Controls.Add(bottom);
        if (background != null) {
            Panel hero = new Panel { Dock = DockStyle.Top, Height = S(112), BackgroundImage = background,
                BackgroundImageLayout = ImageLayout.Zoom, BackColor = Color.FromArgb(15, 18, 24) };
            Label heroText = new Label { AutoSize = true, BackColor = Color.FromArgb(170, 8, 10, 14),
                ForeColor = Color.WhiteSmoke, Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
                Text = "  猎人之夜 · Bloodborne PC  ", Location = new Point(S(18), S(68)),
                Padding = new Padding(S(6), S(4), S(6), S(4)) };
            hero.Controls.Add(heroText);
            Controls.Add(hero);
        }
        AcceptButton = installButton;
        UpdateButtons();
    }

    void ApplyQualityProfile(string profile) {
        if (profile == "verylow") {
            SelectValue(resolution, "1280x720"); SelectValue(frameRate, "60");
            SelectValue(upscaler, "fsr3"); SelectValue(preset, "4"); SelectValue(live, "0");
            SelectValue(modelLod, "2");
            SetEffect("effect_chromatic_aberration", false); SetEffect("effect_dof", false);
            SetEffect("effect_motion_blur", false); SetEffect("effect_ssao", false);
            SetEffect("effect_game_aa", false); SetEffect("effect_dynamic_shadows", false);
            SetEffect("effect_ssr", false);
        } else if (profile == "low") {
            SelectValue(resolution, "1920x1080"); SelectValue(frameRate, "60");
            SelectValue(upscaler, "fsr3"); SelectValue(preset, "3"); SelectValue(live, "0");
            SelectValue(modelLod, "1");
            SetEffect("effect_chromatic_aberration", false); SetEffect("effect_dof", false);
            SetEffect("effect_motion_blur", false); SetEffect("effect_ssao", true);
            SetEffect("effect_game_aa", false); SetEffect("effect_dynamic_shadows", false);
            SetEffect("effect_ssr", false);
        } else if (profile == "medium") {
            SelectValue(resolution, "1920x1080"); SelectValue(frameRate, "60");
            SelectValue(upscaler, "fsr3"); SelectValue(preset, "2"); SelectValue(live, "0");
            SelectValue(modelLod, "0");
            SetEffect("effect_chromatic_aberration", true); SetEffect("effect_dof", true);
            SetEffect("effect_motion_blur", false); SetEffect("effect_ssao", true);
            SetEffect("effect_game_aa", true); SetEffect("effect_dynamic_shadows", true);
            SetEffect("effect_ssr", false);
        } else {
            SelectValue(resolution, "2560x1440"); SelectValue(frameRate, "uncap");
            SelectValue(upscaler, "fsr3"); SelectValue(preset, "1"); SelectValue(live, "auto");
            SelectValue(modelLod, "-2");
            SetEffect("effect_chromatic_aberration", true); SetEffect("effect_dof", true);
            SetEffect("effect_motion_blur", true); SetEffect("effect_ssao", true);
            SetEffect("effect_game_aa", true); SetEffect("effect_dynamic_shadows", true);
            SetEffect("effect_ssr", true);
        }
        sharpen.Checked = true;
        preset.Enabled = UpscalerUsesPreset(Value(upscaler));
        statusLabel.Text = "已应用“" + ((Choice)qualityProfile.SelectedItem).Text + "”，请保存设置或启动游戏。";
    }

    void SetEffect(string key, bool value) {
        foreach (KeyValuePair<string, CheckBox> effect in effects)
            if (effect.Key == key) { effect.Value.Checked = value; return; }
    }

    TableLayoutPanel Group(string title, int columns) {
        GroupBox box = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true,
            Padding = new Padding(S(8)), AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, S(8)) };
        TableLayoutPanel grid = new TableLayoutPanel { ColumnCount = columns, Dock = DockStyle.Fill, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (columns > 2) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        box.Controls.Add(grid);
        return grid;
    }

    void AddRow(TableLayoutPanel grid, string label, Control control, Control extra) {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(0, S(6), S(8), 0) });
        grid.Controls.Add(control);
        if (grid.ColumnCount > 2) grid.Controls.Add(extra ?? new Label { AutoSize = true });
    }

    static void AddSpan(TableLayoutPanel grid, Control control) {
        grid.Controls.Add(control);
        grid.SetColumnSpan(control, grid.ColumnCount);
    }

    ComboBox Combo(TableLayoutPanel grid, string label, Choice[] choices) {
        ComboBox combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(320), Anchor = AnchorStyles.Left };
        combo.Items.AddRange(choices);
        combo.SelectedIndex = 0;
        AddRow(grid, label, combo, null);
        return combo;
    }

    void AddKey(TableLayoutPanel grid, string label, string name, string defaultValue) {
        ComboBox combo = Combo(grid, label, KeyChoices);
        keyBindings[name] = combo;
        SelectValue(combo, defaultValue);
    }

    void AddGamepad(TableLayoutPanel grid, string[] definition) {
        ComboBox combo = Combo(grid, definition[1], GamepadActions);
        gamepadBindings[definition[0]] = combo;
        SelectValue(combo, definition[2]);
    }

    void ResetGamepadMappings() {
        foreach (string[] definition in GamepadDefinitions)
            SelectValue(gamepadBindings[definition[0]], definition[2]);
        statusLabel.Text = "手柄映射已恢复默认；请点击“保存设置”使其生效。";
    }

    static CheckBox Check(string text) { return new CheckBox { Text = text, AutoSize = true }; }
    Label Note(string text) { return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(S(710), 0), Margin = new Padding(0, S(2), 0, S(6)) }; }
    LinkLabel Link(string title, string url) {
        LinkLabel link = new LinkLabel { Text = title, AutoSize = true,
            MaximumSize = new Size(S(710), 0), Margin = new Padding(0, S(2), 0, S(6)) };
        link.LinkClicked += delegate {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Warn("无法打开链接：" + e.Message); }
        };
        return link;
    }
    static FlowLayoutPanel Flow(params Control[] controls) {
        FlowLayoutPanel flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, WrapContents = false };
        flow.Controls.AddRange(controls);
        return flow;
    }

    Button Browse(TextBox target, string description) {
        Button button = new Button { Text = "浏览…", AutoSize = true };
        button.Click += delegate {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = description, ShowNewFolderButton = true }) {
                if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
            }
        };
        return button;
    }

    static string Value(ComboBox combo) { return combo.SelectedItem == null ? "" : ((Choice)combo.SelectedItem).Value; }
    static void SelectValue(ComboBox combo, string value) {
        for (int i = 0; i < combo.Items.Count; ++i)
            if (((Choice)combo.Items[i]).Value == value) { combo.SelectedIndex = i; return; }
    }

    static Dictionary<string, string> ReadValues(string path) {
        Dictionary<string, string> result = new Dictionary<string, string>();
        if (!File.Exists(path)) return result;
        foreach (string raw in File.ReadAllLines(path)) {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int equals = line.IndexOf('=');
            if (equals > 0) result[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
        }
        return result;
    }

    void LoadSettings(string rootArgument) {
        Dictionary<string, string> state = ReadValues(stateFile);
        string defaultRoot = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Bloodborne-PC-运行环境");
        rootBox.Text = !string.IsNullOrEmpty(rootArgument) ? Path.GetFullPath(rootArgument) : Get(state, "install_root", defaultRoot);
        gameBox.Text = Get(state, "game_dir", "");
        saveRootBox.Text = Get(state, "save_root", SaveTools.DefaultRoot);
        autoBackup.Checked = Get(state, "auto_backup", "1") == "1";
        string oldHuntersPath = Path.Combine(saveRootBox.Text, "addcont", "CUSA03173", "SPEXPANSIONDLC03");
        oldHunters.Checked = Directory.Exists(oldHuntersPath) || Get(state, "old_hunters", "0") == "1";
        SelectValue(coreMode, Get(state, "core_mode", "native"));
        lanAddressBox.Text = Get(state, "lan_address", LocalLanAddress());
        lanNpidBox.Text = Get(state, "lan_npid", "");
        lanPasswordBox.Text = UnprotectSecret(Get(state, "lan_password_dpapi", ""));
        lanEmailBox.Text = Get(state, "lan_email", "");
        string savedLanSecret = UnprotectSecret(Get(state, "lan_secret_dpapi", ""));
        if (savedLanSecret.Length == 0) savedLanSecret = Get(state, "lan_secret", CreateRegistrationSecret());
        lanSecretBox.Text = savedLanSecret;
        lanHost.Checked = Get(state, "lan_host", "0") == "1";
        lanSeamless.Checked = Get(state, "lan_seamless", "0") == "1";
        onlineNpidBox.Text = Get(state, "online_npid", "");
        onlinePasswordBox.Text = UnprotectSecret(Get(state, "online_password_dpapi", ""));
        onlineNicknameBox.Text = Get(state, "online_nickname", "");
        onlineShowInList.Checked = Get(state, "online_show_in_list", "0") == "1";
        onlineApplicationId = Get(state, "online_application_id", "");
        onlineApplicationToken = UnprotectSecret(Get(state, "online_application_token_dpapi", ""));
        if (onlineApplicationId.Length > 0 && onlineApplicationToken.Length > 0)
            onlineApplicationStatus.Text = "申请状态：已有待查询申请，请点击“查询申请进度”。";
        onlineServiceStatus.Text = PublicServerAddress().Length == 0
            ? "服务状态：尚未开放（此安装包未配置公共服务）" : "服务状态：尚未检测";
        onlineLoginStatus.Text = "登录状态：未登录";
        SelectValue(qualityProfile, Get(state, "quality_profile", "medium"));
        Dictionary<string, string> ini = ReadValues(Path.Combine(rootBox.Text, "bbport.ini"));
        SelectValue(resolution, Get(ini, "output_res", DefaultResolution()));
        SelectValue(frameRate, Get(state, "fps", "uncap"));
        SelectValue(presentMode, Get(state, "present_mode", ""));
        SelectValue(upscaler, Get(ini, "upscaler", Default("upscaler")));
        SelectValue(preset, Get(ini, "preset", Default("preset")));
        SelectValue(live, Get(ini, "live_resolution", Default("live_resolution")));
        SelectValue(modelLod, Get(ini, "model_lod", Default("model_lod")));
        SelectValue(language, Get(state, "language", "1"));
        fullscreen.Checked = Get(ini, "fullscreen", "1") == "1";
        showFps.Checked = Get(ini, "show_fps", Default("show_fps")) == "1";
        sharpen.Checked = Get(ini, "sharpen", Default("sharpen")) == "1";
        foreach (KeyValuePair<string, CheckBox> effect in effects) {
            string fallback = "0";
            foreach (string[] definition in EffectDefinitions) if (definition[0] == effect.Key) fallback = definition[2];
            effect.Value.Checked = Get(ini, effect.Key, fallback) == "1";
        }
        desktopLink.Checked = Get(state, "desktop_link", "1") == "1";
        startMenuLink.Checked = Get(state, "start_menu_link", "1") == "1";
        foreach (KeyValuePair<string, ComboBox> binding in keyBindings)
            SelectValue(binding.Value, Get(state, "BB_KEY_" + binding.Key, Value(binding.Value)));
        foreach (KeyValuePair<string, ComboBox> binding in gamepadBindings)
            SelectValue(binding.Value, Get(state, "BB_PAD_" + binding.Key, Value(binding.Value)));
        mouseEnabled.Checked = Get(state, "BB_MOUSE_ENABLED", "1") == "1";
        mouseInvert.Checked = Get(state, "BB_MOUSE_INVERT_Y", "0") == "1";
        decimal sensitivity;
        if (decimal.TryParse(Get(state, "BB_MOUSE_SENSITIVITY", "3.0"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out sensitivity))
            mouseSensitivity.Value = Math.Max(mouseSensitivity.Minimum, Math.Min(mouseSensitivity.Maximum, sensitivity));
        SelectValue(mouseLeft, Get(state, "BB_MOUSE_LEFT", "r1"));
        SelectValue(mouseRight, Get(state, "BB_MOUSE_RIGHT", "l1"));
        SelectValue(mouseMiddle, Get(state, "BB_MOUSE_MIDDLE", "r3"));
        SelectValue(mouseX1, Get(state, "BB_MOUSE_X1", "square"));
        SelectValue(mouseX2, Get(state, "BB_MOUSE_X2", "triangle"));
        SelectValue(mouseWheelUp, Get(state, "BB_MOUSE_WHEEL_UP", "up"));
        SelectValue(mouseWheelDown, Get(state, "BB_MOUSE_WHEEL_DOWN", "down"));
        foreach (string[] definition in CheatDefinitions)
            cheats[definition[0]].Checked = Get(state, "cheat_" + definition[0], "0") == "1";
        preset.Enabled = UpscalerUsesPreset(Value(upscaler));
        CheckGame();
        RefreshSaveManager();
    }

    void ShowWelcomeOnce() {
        if (File.Exists(welcomeFile)) return;
        MessageBox.Show(this,
            "欢迎使用 Bloodborne PC 中文安装与启动器。\r\n\r\n" +
            "这是非盈利性质的爱好者项目，不销售游戏、账号或联机资格，也不提供游戏本体。" +
            "请使用自己合法取得的游戏文件，并注意备份存档。\r\n\r\n" +
            "启动器整合与维护：哔哩哔哩 细雨寒言\r\n" +
            "更多说明请查看“帮助”页。",
            "首次使用说明 · 哔哩哔哩 细雨寒言", MessageBoxButtons.OK, MessageBoxIcon.Information);
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(welcomeFile));
            File.WriteAllText(welcomeFile, "shown", Encoding.UTF8);
        } catch { }
    }

    static string OnlinePackageSetting(string key) {
        foreach (string line in PayloadText("ONLINE_SERVICE.txt").Split(new char[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries)) {
            string trimmed = line.Trim();
            string prefix = key + "=";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring(prefix.Length).Trim();
        }
        return "";
    }

    static string PublicServerAddress() { return OnlinePackageSetting("server"); }
    static string OverlayServerIp() {
        string value = OnlinePackageSetting("overlay_server");
        IPAddress ip;
        if (!IPAddress.TryParse(value, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return "";
        byte[] bytes = ip.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127 ? value : "";
    }
    static bool PublicServiceIsLoopback() {
        if (OverlayServerIp().Length != 0) return false;
        string host;
        int port;
        IPAddress address;
        return TryParseLanEndpoint(PublicServerAddress(), out host, out port) &&
            (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             (IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address)));
    }
    static string PublicPortalUrl() {
        string value = OnlinePackageSetting("portal").TrimEnd('/');
        Uri uri;
        return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https") && uri.UserInfo.Length == 0 &&
            uri.PathAndQuery == "/" ? value : "";
    }
    bool EmbeddedNetReady() {
        return embeddedNetIp.Length != 0 && embeddedNetProcess != null && !embeddedNetProcess.HasExited;
    }
    string EffectivePublicPortalUrl() {
        if (OverlayServerIp().Length != 0)
            return EmbeddedNetReady() ? "http://127.0.0.2:18080" : "";
        return PublicPortalUrl();
    }

    static string HeadscaleControlUrl() {
        string value = OnlinePackageSetting("control").TrimEnd('/');
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.UserInfo.Length != 0 ||
            uri.PathAndQuery != "/" || uri.Fragment.Length != 0 ||
            (uri.Scheme != "http" && uri.Scheme != "https")) return "";
        if (uri.Scheme == "http" && !IPAddress.IsLoopback(ResolveAddress(uri.Host))) return "";
        return uri.AbsoluteUri.TrimEnd('/');
    }

    static IPAddress ResolveAddress(string host) {
        IPAddress address;
        return IPAddress.TryParse(host, out address) ? address :
            (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.None);
    }

    static string Get(Dictionary<string, string> map, string key, string fallback) {
        string value;
        return map.TryGetValue(key, out value) ? value : fallback;
    }

    static string ProtectSecret(string value) {
        if (string.IsNullOrEmpty(value)) return "";
        try {
            byte[] plain = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        } catch { return ""; }
    }

    static string UnprotectSecret(string value) {
        if (string.IsNullOrEmpty(value)) return "";
        try {
            byte[] cipher = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser));
        } catch { return ""; }
    }

    static string CreateRegistrationSecret() {
        byte[] bytes = new byte[18];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    static string LocalLanAddress() {
        try {
            foreach (IPAddress address in Dns.GetHostEntry(Dns.GetHostName()).AddressList) {
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                byte[] octets = address.GetAddressBytes();
                if (octets[0] == 169 && octets[1] == 254) continue;
                return address.ToString();
            }
        } catch { }
        return "127.0.0.1";
    }

    static bool IsLocalLanEndpoint(string value) {
        string host;
        int ignoredPort;
        if (!TryParseLanEndpoint(value, out host, out ignoredPort)) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        try {
            IPAddress target;
            if (IPAddress.TryParse(host, out target) && IPAddress.IsLoopback(target)) return true;
            IPAddress[] targets = IPAddress.TryParse(host, out target)
                ? new IPAddress[] { target } : Dns.GetHostAddresses(host);
            IPAddress[] local = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
            foreach (IPAddress candidate in targets) {
                if (IPAddress.IsLoopback(candidate)) return true;
                foreach (IPAddress address in local)
                    if (candidate.Equals(address)) return true;
            }
        } catch { }
        return false;
    }

    static bool TryParseLanEndpoint(string value, out string host, out int port) {
        host = "";
        port = 31313;
        string text = (value ?? "").Trim();
        if (text.Length == 0) return false;
        if (text.IndexOf("://", StringComparison.Ordinal) >= 0) {
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return false;
            host = uri.Host;
            if (!uri.IsDefaultPort) port = uri.Port;
        } else {
            int colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon) {
                int parsed;
                if (int.TryParse(text.Substring(colon + 1), out parsed) && parsed > 0 && parsed <= 65535) {
                    port = parsed;
                    text = text.Substring(0, colon);
                }
            }
            host = text.Trim();
        }
        if (host == "0.0.0.0" || host.Length == 0 || host.IndexOfAny(new char[] { '/', '\\', ' ' }) >= 0)
            return false;
        IPAddress parsedAddress;
        return IPAddress.TryParse(host, out parsedAddress) || Regex.IsMatch(host, @"^[A-Za-z0-9.-]+$");
    }

    static JavaScriptSerializer JsonSerializer() {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;
        serializer.RecursionLimit = 100;
        return serializer;
    }

    static Dictionary<string, object> ReadJsonObject(string path) {
        try {
            if (File.Exists(path)) {
                object value = JsonSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                Dictionary<string, object> dictionary = value as Dictionary<string, object>;
                if (dictionary != null) return dictionary;
            }
        } catch { }
        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    static Dictionary<string, object> ChildObject(Dictionary<string, object> parent, string key) {
        object value;
        Dictionary<string, object> child = parent.TryGetValue(key, out value) ?
            value as Dictionary<string, object> : null;
        if (child == null) {
            child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            parent[key] = child;
        }
        return child;
    }

    static void WriteJson(string path, object value) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonSerializer().Serialize(value), new UTF8Encoding(false));
    }

    void ConfigureLanClient(LaunchOptions options) {
        string client = Path.Combine(options.Root, "shadp2p", "client");
        string portableUser = Path.Combine(client, "user");
        string home = Path.Combine(options.SaveRoot, "shadp2p-home");
        Directory.CreateDirectory(portableUser);
        Directory.CreateDirectory(home);

        string host;
        int port;
        if (!TryParseLanEndpoint(options.LanAddress, out host, out port))
            throw new InvalidDataException("局域网服务器地址无效。");

        string configPath = Path.Combine(portableUser, "config.json");
        Dictionary<string, object> config = ReadJsonObject(configPath);
        Dictionary<string, object> general = ChildObject(config, "General");
        general["home_dir"] = home;
        general["connected_to_network"] = true;
        general["shad_net_enabled"] = true;
        general["shadnet_server"] = host + ":" + port;
        general["shadnet_webapi_server"] = "http://" + host + ":31315";
        general["signaling_info"] = "";
        general["enable_upnp"] = false;
        WriteJson(configPath, config);

        string usersPath = Path.Combine(portableUser, "users.json");
        Dictionary<string, object> usersRoot = ReadJsonObject(usersPath);
        Dictionary<string, object> users = ChildObject(usersRoot, "Users");
        List<object> list = new List<object>();
        object existingUsers;
        object[] oldUsers = users.TryGetValue("user", out existingUsers) ? existingUsers as object[] : null;
        if (oldUsers != null) list.AddRange(oldUsers);
        Dictionary<string, object> primary = null;
        foreach (object item in list) {
            Dictionary<string, object> candidate = item as Dictionary<string, object>;
            object id;
            if (candidate != null && candidate.TryGetValue("user_id", out id) && Convert.ToInt32(id) == 1000) {
                primary = candidate; break;
            }
        }
        if (primary == null) {
            primary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            list.Insert(0, primary);
        }
        primary["user_id"] = 1000;
        primary["user_name"] = options.LanNpid;
        primary["user_color"] = 1;
        primary["player_index"] = 1;
        primary["shadnet_npid"] = options.LanNpid;
        primary["shadnet_password"] = options.LanPassword;
        primary["shadnet_token"] = "";
        primary["shadnet_email"] = options.LanEmail;
        primary["shadnet_enabled"] = true;
        primary["np_country"] = "cn";
        primary["np_language"] = "zh";
        primary["np_age"] = 30;
        primary["np_date_of_birth"] = "1994-01-01";
        users["user"] = list;
        if (!users.ContainsKey("commit_hash")) users["commit_hash"] = "";
        WriteJson(usersPath, usersRoot);

        Dictionary<string, object> overrides = new Dictionary<string, object>();
        overrides["https://ss4.scej-network.jp:20443"] = "http://thehuntersdream.com";
        overrides["http://thehuntersdream.com:18671/summon_messenger"] =
            "http://" + host + ":31315";
        WriteJson(Path.Combine(portableUser, "host_overrides.json"), overrides);
        Log("shadp2p 客户端配置已写入：" + portableUser);
    }

    static string LanPlayerRoot(LaunchOptions options) {
        return Path.Combine(options.SaveRoot, "shadp2p-home", "1000");
    }

    LaunchOptions CollectLanOnly() {
        LaunchOptions options = new LaunchOptions();
        options.Root = Path.GetFullPath(rootBox.Text.Trim().Trim('"'));
        options.SaveRoot = Path.GetFullPath(saveRootBox.Text.Trim().Trim('"'));
        options.LanMode = true;
        options.LanHost = lanHost.Checked;
        options.LanSeamless = lanSeamless.Checked;
        options.LanAddress = lanAddressBox.Text.Trim();
        options.LanNpid = lanNpidBox.Text.Trim();
        options.LanPassword = lanPasswordBox.Text;
        options.LanEmail = lanEmailBox.Text.Trim();
        options.LanSecret = lanSecretBox.Text;
        return options;
    }

    static void WriteShadNetConfig(string path, LaunchOptions options) {
        StringBuilder text = new StringBuilder();
        text.AppendLine("[General]");
        text.AppendLine("Host=0.0.0.0");
        text.AppendLine("UnsecuredPort=31313");
        text.AppendLine("MatchingUdpPort=31314");
        text.AppendLine("WebApiPort=31315");
        text.AppendLine("StatsEnabled=false");
        text.AppendLine("Matching2Enabled=true");
        text.AppendLine("BloodborneSeamlessCoop=" + (options.LanSeamless ? "true" : "false"));
        text.AppendLine("StatsPort=31320");
        text.AppendLine("StatsPath=stats");
        text.AppendLine("StatsCacheLife=30");
        text.AppendLine("EmailValidated=false");
        text.AppendLine("AdminsList=");
        text.AppendLine("RegistrationSecretKey=" + options.LanSecret.Replace("\r", "").Replace("\n", "").Trim());
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    string StartLanServerProcess(LaunchOptions options) {
        try {
            if (shadNetProcess != null && !shadNetProcess.HasExited) return null;
            string directory = Path.Combine(options.Root, "shadp2p", "server");
            string executable = Path.Combine(directory, "shadnet.exe");
            if (!File.Exists(executable)) return "没有找到 shadnet.exe，请先安装／更新运行环境。";
            if (options.LanSecret.Length == 0) return "请填写服务器注册密钥。";
            Directory.CreateDirectory(directory);
            WriteShadNetConfig(Path.Combine(directory, "shadnet.cfg"), options);
            string logDirectory = Path.Combine(options.Root, "logs");
            Directory.CreateDirectory(logDirectory);
            string serverLogPath = Path.Combine(logDirectory,
                "shadnet-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            File.WriteAllText(serverLogPath,
                "shadNet server log " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "\r\n" +
                "Mode: " + (options.LanSeamless ? "seamless" : "traditional") + "\r\n",
                new UTF8Encoding(false));
            ProcessStartInfo info = new ProcessStartInfo(executable) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            if (options.LanSeamless) info.EnvironmentVariables["SHADNET_BLOODBORNE_SEAMLESS_COOP"] = "1";
            else info.EnvironmentVariables.Remove("SHADNET_BLOODBORNE_SEAMLESS_COOP");
            Process process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) {
                    Log("[shadNet] " + e.Data);
                    AppendShadNetLog(serverLogPath, e.Data);
                }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) {
                    Log("[shadNet] " + e.Data);
                    AppendShadNetLog(serverLogPath, e.Data);
                }
            };
            process.Exited += delegate {
                if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate {
                    if (IsDisposed) return;
                    lanStartServerButton.Enabled = !busy;
                    lanStopServerButton.Enabled = false;
                    lanStatus.Text = "本机 shadNet 已停止。";
                });
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            shadNetProcess = process;
            BeginInvoke((Action)delegate {
                lanStartServerButton.Enabled = false;
                lanStopServerButton.Enabled = true;
            });
            Log("本机 shadNet 已启动，监听 TCP 31313、UDP 31314、TCP 31315。");
            Log("shadNet 服务端日志：" + serverLogPath);
            return null;
        } catch (Exception e) { return e.Message; }
    }

    void AppendShadNetLog(string path, string line) {
        try {
            lock (shadNetLogLock)
                File.AppendAllText(path, line.Replace("\r", "") + "\r\n", new UTF8Encoding(false));
        } catch { }
    }

    void StartLanServerFromUi() {
        try {
            LaunchOptions options = CollectLanOnly();
            string error = StartLanServerProcess(options);
            if (error != null) { Warn("启动 shadNet 失败：" + error); return; }
            lanHost.Checked = true;
            lanStatus.Text = "本机服务器正在启动……";
        } catch (Exception e) { Warn("启动 shadNet 失败：" + e.Message); }
    }

    void StopLanServer() {
        try {
            if (shadNetProcess != null && !shadNetProcess.HasExited) {
                shadNetProcess.Kill();
                shadNetProcess.WaitForExit(3000);
            }
        } catch (Exception e) { Log("停止 shadNet 时出现问题：" + e.Message); }
        try {
            string expected = Path.GetFullPath(Path.Combine(rootBox.Text.Trim().Trim('"'),
                "shadp2p", "server", "shadnet.exe"));
            foreach (Process process in Process.GetProcessesByName("shadnet")) {
                try {
                    string actual = process.MainModule == null ? "" : process.MainModule.FileName;
                    if (!string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                        continue;
                    process.Kill();
                    process.WaitForExit(3000);
                } catch { }
                finally { process.Dispose(); }
            }
        } catch (Exception e) { Log("清理 shadNet 子进程时出现问题：" + e.Message); }
        shadNetProcess = null;
        lanStartServerButton.Enabled = !busy;
        lanStopServerButton.Enabled = false;
        lanStatus.Text = "本机 shadNet 已停止。";
    }

    static bool CheckLanStatus(string address, out string message) {
        string host;
        int ignoredPort;
        if (!TryParseLanEndpoint(address, out host, out ignoredPort)) {
            message = "服务器地址无效。"; return false;
        }
        try {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://" + host + ":31315/status");
            request.Timeout = 4000;
            request.ReadWriteTimeout = 4000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                string body = reader.ReadToEnd();
                bool ok = response.StatusCode == HttpStatusCode.OK && body.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) >= 0;
                message = ok ? "服务器在线：" + host + ":31315" : "服务器响应异常：" + body;
                return ok;
            }
        } catch (Exception e) { message = "无法连接服务器：" + e.Message; return false; }
    }

    bool CheckPublicStatus(out string message, out int onlinePlayers,
                                  out List<OnlinePlayer> players) {
        onlinePlayers = -1;
        players = new List<OnlinePlayer>();
        string portal = EffectivePublicPortalUrl();
        if (portal.Length == 0) {
            message = "公共服务尚未配置，暂不能使用在线联机。"; return false;
        }
        try {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(portal + "/v1/status");
            request.Timeout = 4000;
            request.ReadWriteTimeout = 4000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                if (response.StatusCode != HttpStatusCode.OK ||
                    !ParsePublicStatusDetails(reader.ReadToEnd(), out onlinePlayers, out players)) {
                    message = "服务端响应异常。"; return false;
                }
                message = "服务在线";
                return true;
            }
        } catch { message = "服务暂时不可用，请稍后重试。"; return false; }
    }

    static bool ParsePublicStatus(string body, out int onlinePlayers) {
        List<OnlinePlayer> ignored;
        return ParsePublicStatusDetails(body, out onlinePlayers, out ignored);
    }

    static string PublicField(Dictionary<string, object> map, string key) {
        object value;
        if (!map.TryGetValue(key, out value) || value == null) return "";
        string result = Convert.ToString(value, CultureInfo.InvariantCulture);
        return result.Length > 100 ? result.Substring(0, 100) : result;
    }

    static bool ParsePublicStatusDetails(string body, out int onlinePlayers,
                                         out List<OnlinePlayer> players) {
        onlinePlayers = -1;
        players = new List<OnlinePlayer>();
        Dictionary<string, object> status = JsonSerializer().DeserializeObject(body)
            as Dictionary<string, object>;
        object okValue;
        if (status == null || !status.TryGetValue("ok", out okValue) ||
            !(okValue is bool) || !(bool)okValue) return false;
        object count;
        if (status.TryGetValue("online_players", out count) && count != null) {
            int parsed;
            if (int.TryParse(Convert.ToString(count, CultureInfo.InvariantCulture), out parsed) && parsed >= 0)
                onlinePlayers = parsed;
        }
        object listValue;
        object[] list = status.TryGetValue("users", out listValue) ? listValue as object[] : null;
        if (list != null) foreach (object item in list) {
            Dictionary<string, object> user = item as Dictionary<string, object>;
            if (user == null || players.Count >= 500) continue;
            string npid = PublicField(user, "npid");
            if (npid.Length == 0) continue;
            players.Add(new OnlinePlayer { Npid = npid, Nickname = PublicField(user, "nickname"),
                Title = PublicField(user, "title_name") });
        }
        return true;
    }

    void RefreshPublicStatus() {
        if (onlineRefreshButton == null || !onlineRefreshButton.Enabled) return;
        onlineRefreshButton.Enabled = false;
        onlineServiceStatus.Text = "服务状态：正在检测……";
        Thread worker = new Thread(new ThreadStart(delegate {
            string message;
            int count;
            List<OnlinePlayer> players;
            bool ok = CheckPublicStatus(out message, out count, out players);
            if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate {
                onlineRefreshButton.Enabled = true;
                onlineServiceStatus.Text = "服务状态：" + message;
                onlineServiceStatus.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
                onlineCountStatus.Text = "在线人数：" + (count < 0 ? "暂不可用" : count.ToString()) +
                    "　公开列表：" + players.Count;
                onlineUsersList.BeginUpdate();
                onlineUsersList.Items.Clear();
                foreach (OnlinePlayer player in players) {
                    ListViewItem row = new ListViewItem(player.Npid);
                    row.SubItems.Add(player.Nickname);
                    row.SubItems.Add(player.Title);
                    onlineUsersList.Items.Add(row);
                }
                onlineUsersList.EndUpdate();
            });
        }));
        worker.IsBackground = true;
        worker.Start();
    }

    static void CheckHeadscaleHealth(string control) {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(control + "/health");
        request.Method = "GET";
        request.Timeout = 3000;
        request.ReadWriteTimeout = 3000;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new IOException("Headscale 健康检查失败：HTTP " + (int)response.StatusCode);
        }
    }

    void ConnectHeadscale() {
        string control = HeadscaleControlUrl();
        if (control.Length == 0) {
            MessageBox.Show(this, "未配置有效的 Headscale 控制地址。正式服务须使用 HTTPS；HTTP 仅允许本机回环地址。",
                "自建私网", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try {
            if (embeddedNetProcess != null && !embeddedNetProcess.HasExited) {
                RefreshTailscaleStatus();
                return;
            }
            string root = Path.GetFullPath(rootBox.Text.Trim().Trim('"'));
            string executable = Path.Combine(root, "net", "bbport-net.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("未找到内置私网组件。请先安装／更新运行环境。", executable);
            CheckHeadscaleHealth(control);
            onlineHeadscaleStatus.Text = "自建私网控制服务：可连接（仅确认控制端健康）";
            onlineHeadscaleStatus.ForeColor = Color.DarkGreen;
            DialogResult choice = MessageBox.Show(this,
                "将启动随启动器附带的独立私网节点。首次入网将使用已注册的在线账号自动申请一次性设备凭据；" +
                "只有控制端健康不代表两台玩家电脑已互通。\r\n\r\n继续吗？",
                "连接自建私网", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;
            string state = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "BloodbornePC", "embedded-net");
            Directory.CreateDirectory(state);
            string identityDir = Path.Combine(state, "identity");
            string enrolledMarker = Path.Combine(state, "enrolled-control.txt");
            string deviceKey = "";
            string enrolledControl = File.Exists(enrolledMarker) ? File.ReadAllText(enrolledMarker).Trim() : "";
            bool hasIdentity = File.Exists(Path.Combine(identityDir, "tailscaled.state"));
            bool knownEnrolled = hasIdentity && String.Equals(enrolledControl, control,
                StringComparison.OrdinalIgnoreCase);
            // Older builds discarded the account token after registration. Let their already
            // authorized identity reconnect; a new or failed identity needs a fresh key.
            if (!knownEnrolled && (!hasIdentity ||
                (onlineApplicationId.Length > 0 && onlineApplicationToken.Length > 0))) {
                if (onlineApplicationId.Length == 0 || onlineApplicationToken.Length == 0)
                    throw new InvalidOperationException("请先在在线联机页完成账号申请和注册，才能自动加入私网。");
                Dictionary<string, object> request = new Dictionary<string, object>();
                request["application_id"] = onlineApplicationId;
                request["application_token"] = onlineApplicationToken;
                deviceKey = PublicField(OnlinePortalRequest("/v1/device-key", request), "auth_key");
                if (!deviceKey.StartsWith("hskey-auth-", StringComparison.Ordinal))
                    throw new InvalidDataException("服务器未返回有效的设备入网凭据。");
            }
            Dictionary<string, object> config = new Dictionary<string, object>();
            config["control_url"] = control;
            config["state_dir"] = identityDir;
            config["hostname"] = "bbport-" + Environment.MachineName.ToLowerInvariant();
            config["game_udp_port"] = 31316;
            config["udp_bridge_port"] = 31416;
            string serverIp = OverlayServerIp();
            List<Dictionary<string, object>> tcp = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> udp = new List<Dictionary<string, object>>();
            if (serverIp.Length != 0) {
                foreach (int port in new int[] { 31313, 31315, 18080 })
                    tcp.Add(new Dictionary<string, object> { { "listen", "127.0.0.2:" + port },
                        { "remote", serverIp + ":" + port } });
                udp.Add(new Dictionary<string, object> { { "listen", "127.0.0.2:31314" },
                    { "remote", serverIp + ":31314" } });
            }
            config["tcp_proxies"] = tcp;
            config["udp_proxies"] = udp;
            string configFile = Path.Combine(state, "client.json");
            File.WriteAllText(configFile, JsonSerializer().Serialize(config), new UTF8Encoding(false));
            string arguments = "--config \"" + configFile + "\"" +
                (deviceKey.Length > 0 ? " --auth-key-stdin" : "");
            ProcessStartInfo info = new ProcessStartInfo(executable, arguments) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = deviceKey.Length > 0,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };
            embeddedNetIp = "";
            lastEmbeddedAuthUrl = "";
            Process process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) {
                if (args.Data != null) HandleEmbeddedNetEvent(args.Data, control);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) {
                if (args.Data != null) Log("内置私网诊断：" + args.Data);
            };
            process.Exited += delegate {
                embeddedNetIp = "";
                if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { RefreshTailscaleStatus(); });
            };
            process.Start();
            if (deviceKey.Length > 0) {
                process.StandardInput.WriteLine(deviceKey);
                process.StandardInput.Close();
                deviceKey = "";
            }
            embeddedNetProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            onlineHeadscaleStatus.Text = "自建私网：正在自动验证设备。";
            onlineHeadscaleStatus.ForeColor = Color.DarkOrange;
        } catch (Exception e) {
            onlineHeadscaleStatus.Text = "自建私网：连接准备失败（" + e.Message + "）";
            onlineHeadscaleStatus.ForeColor = Color.Firebrick;
            MessageBox.Show(this, "无法启动内置私网连接：" + e.Message,
                "自建私网", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void HandleEmbeddedNetEvent(string line, string control) {
        Dictionary<string, object> item;
        try { item = JsonSerializer().DeserializeObject(line) as Dictionary<string, object>; }
        catch { return; }
        if (item == null) return;
        string state = PublicField(item, "state");
        string ip = PublicField(item, "ip");
        string auth = PublicField(item, "auth_url");
        string error = PublicField(item, "error");
        if (state == "ready") {
            IPAddress parsed;
            if (IPAddress.TryParse(ip, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork) {
                embeddedNetIp = ip;
                try {
                    string marker = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "BloodbornePC",
                        "embedded-net", "enrolled-control.txt");
                    File.WriteAllText(marker, control, new UTF8Encoding(false));
                } catch (Exception e) { Log("无法保存私网入网状态：" + e.Message); }
            }
        }
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)delegate {
            if (state == "auth" && auth != lastEmbeddedAuthUrl) {
                Uri login, server;
                if (Uri.TryCreate(auth, UriKind.Absolute, out login) &&
                    Uri.TryCreate(control, UriKind.Absolute, out server) &&
                    login.Scheme == server.Scheme && login.Authority == server.Authority) {
                    lastEmbeddedAuthUrl = auth;
                    if (MessageBox.Show(this, "设备需要重新验证。请检查在线账号或联系管理员；不要分享此地址。\r\n\r\n" + auth +
                        "\r\n\r\n现在打开授权页面吗？", "内置私网设备授权",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        Process.Start(new ProcessStartInfo(auth) { UseShellExecute = true });
                }
            } else if (state == "error") {
                onlineTailscaleStatus.Text = "内置私网：连接失败（" + error + "）";
                onlineTailscaleStatus.ForeColor = Color.Firebrick;
            } else RefreshTailscaleStatus();
        });
    }

    void RefreshTailscaleStatus() {
        bool ready = EmbeddedNetReady();
        onlineTailscaleStatus.Text = ready ? "内置私网：已连接，节点 " + embeddedNetIp :
            embeddedNetProcess != null && !embeddedNetProcess.HasExited ? "内置私网：等待授权或分配地址" :
            "内置私网：未连接";
        onlineTailscaleStatus.ForeColor = ready ? Color.DarkGreen : Color.DarkOrange;
    }

    Dictionary<string, object> OnlinePortalRequest(string route, Dictionary<string, object> body) {
        string portal = EffectivePublicPortalUrl();
        if (portal.Length == 0) throw new InvalidOperationException("公共在线服务尚未配置。");
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer().Serialize(body));
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(portal + route);
        request.Method = "POST";
        request.ContentType = "application/json; charset=utf-8";
        request.ContentLength = payload.Length;
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        try {
            using (Stream stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                Dictionary<string, object> result = JsonSerializer().DeserializeObject(reader.ReadToEnd())
                    as Dictionary<string, object>;
                if (result == null) throw new InvalidDataException("服务端响应格式错误。");
                return result;
            }
        } catch (WebException error) {
            HttpWebResponse response = error.Response as HttpWebResponse;
            if (response != null) using (response) using (StreamReader reader = new StreamReader(
                response.GetResponseStream(), Encoding.UTF8)) {
                try {
                    Dictionary<string, object> result = JsonSerializer().DeserializeObject(reader.ReadToEnd())
                        as Dictionary<string, object>;
                    if (result != null && PublicField(result, "error").Length > 0)
                        throw new InvalidOperationException(PublicField(result, "error"));
                } catch (ArgumentException) { }
            }
            throw new IOException("在线服务请求失败，请稍后重试。");
        }
    }

    void PersistOnlineApplication() {
        Dictionary<string, string> state = ReadValues(stateFile);
        state["online_application_id"] = onlineApplicationId;
        state["online_application_token_dpapi"] = ProtectSecret(onlineApplicationToken);
        state["online_npid"] = onlineNpidBox.Text.Trim();
        state["online_nickname"] = onlineNicknameBox.Text.Trim();
        state["online_show_in_list"] = onlineShowInList.Checked ? "1" : "0";
        state["online_password_dpapi"] = ProtectSecret(onlinePasswordBox.Text);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile));
        WriteSimple(stateFile, state);
    }

    void ApplyOnlineAccount() {
        string qq = onlineQqBox.Text.Trim();
        string nickname = onlineNicknameBox.Text.Trim();
        string npid = onlineNpidBox.Text.Trim();
        if (!Regex.IsMatch(qq, @"^[1-9][0-9]{4,11}$") || nickname.Length < 1 || nickname.Length > 24 ||
            !Regex.IsMatch(npid, @"^[A-Za-z0-9_-]{3,16}$")) {
            Warn("请填写有效的 QQ 号、1–24 字昵称和 3–16 位联机 ID。"); return;
        }
        onlineApplyButton.Enabled = false;
        onlineApplicationStatus.Text = "申请状态：正在提交……";
        bool show = onlineShowInList.Checked;
        Thread worker = new Thread(new ThreadStart(delegate {
            try {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["qq"] = qq; body["nickname"] = nickname; body["npid"] = npid;
                body["show_in_list"] = show;
                Dictionary<string, object> result = OnlinePortalRequest("/v1/apply", body);
                string id = PublicField(result, "application_id");
                string token = PublicField(result, "application_token");
                if (id.Length == 0 || token.Length == 0) throw new InvalidDataException("申请响应不完整。");
                BeginInvoke((Action)delegate {
                    onlineApplicationId = id;
                    onlineApplicationToken = token;
                    onlineRegistrationKey = PublicField(result, "registration_key");
                    onlineApplicationStatus.Text = onlineRegistrationKey.Length > 0
                        ? "申请状态：已获一次性注册码，请输入账号密码并完成注册。"
                        : "申请状态：等待管理员审核，稍后点击“查询申请进度”。";
                    onlineApplyButton.Enabled = true;
                    try { PersistOnlineApplication(); }
                    catch (Exception e) { Warn("无法保存申请凭据：" + e.Message); }
                });
            } catch (Exception e) {
                BeginInvoke((Action)delegate {
                    onlineApplyButton.Enabled = true;
                    onlineApplicationStatus.Text = "申请状态：失败——" + e.Message;
                });
            }
        }));
        worker.IsBackground = true;
        worker.Start();
    }

    void QueryOnlineApplication() {
        if (onlineApplicationId.Length == 0 || onlineApplicationToken.Length == 0) {
            Warn("请先申请一次性注册码。"); return;
        }
        string id = onlineApplicationId, token = onlineApplicationToken;
        onlineQueryButton.Enabled = false;
        Thread worker = new Thread(new ThreadStart(delegate {
            try {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["application_id"] = id; body["application_token"] = token;
                Dictionary<string, object> result = OnlinePortalRequest("/v1/application", body);
                string state = PublicField(result, "state");
                string key = PublicField(result, "registration_key");
                BeginInvoke((Action)delegate {
                    onlineRegistrationKey = key;
                    onlineApplicationStatus.Text = state == "approved" ?
                        "申请状态：已获一次性注册码，请输入账号密码并完成注册。" :
                        state == "registered" ? "申请状态：账号已注册。" :
                        state == "expired" ? "申请状态：已过期，请重新申请。" :
                        "申请状态：等待管理员审核。";
                    onlineQueryButton.Enabled = true;
                });
            } catch (Exception e) { BeginInvoke((Action)delegate {
                onlineQueryButton.Enabled = true;
                onlineApplicationStatus.Text = "查询失败：" + e.Message;
            }); }
        }));
        worker.IsBackground = true;
        worker.Start();
    }

    void RegisterOnlineAccount() {
        if (onlineApplicationId.Length == 0 || onlineApplicationToken.Length == 0 ||
            onlineRegistrationKey.Length == 0) {
            Warn("尚未获批一次性注册码。请先申请并查询申请进度。"); return;
        }
        string password = onlinePasswordBox.Text;
        if (password.Length < 8 || password.Length > 128) {
            Warn("在线账号密码须为 8–128 个字符。"); return;
        }
        string id = onlineApplicationId, token = onlineApplicationToken, key = onlineRegistrationKey;
        onlineRegisterButton.Enabled = false;
        onlineApplicationStatus.Text = "申请状态：正在注册……";
        Thread worker = new Thread(new ThreadStart(delegate {
            try {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["application_id"] = id; body["application_token"] = token;
                body["registration_key"] = key; body["password"] = password;
                Dictionary<string, object> result = OnlinePortalRequest("/v1/register", body);
                object ok;
                if (!result.TryGetValue("ok", out ok) || !(ok is bool) || !(bool)ok)
                    throw new InvalidOperationException("服务端未完成注册，请检查联机 ID 是否已被占用。");
                BeginInvoke((Action)delegate {
                    onlineRegistrationKey = "";
                    onlineApplicationStatus.Text = "申请状态：注册成功！设备入网将使用本机加密保存的申请凭据自动完成。";
                    onlineRegisterButton.Enabled = true;
                    try { PersistOnlineApplication(); }
                    catch (Exception e) { Warn("注册成功，但本地设置保存失败：" + e.Message); }
                });
            } catch (Exception e) { BeginInvoke((Action)delegate {
                onlineRegisterButton.Enabled = true;
                onlineApplicationStatus.Text = "注册失败：" + e.Message;
            }); }
        }));
        worker.IsBackground = true;
        worker.Start();
    }

    void TestLanServerFromUi() {
        lanTestButton.Enabled = false;
        lanStatus.Text = "正在检测服务器……";
        string address = lanAddressBox.Text;
        Thread worker = new Thread(new ThreadStart(delegate {
            string message;
            bool ok = CheckLanStatus(address, out message);
            BeginInvoke((Action)delegate {
                lanTestButton.Enabled = !busy;
                lanStatus.Text = message;
                lanStatus.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
            });
        }));
        worker.IsBackground = true;
        worker.Start();
    }

    static void WriteVarint(Stream stream, uint value) {
        while (value >= 0x80) { stream.WriteByte((byte)((value & 0x7F) | 0x80)); value >>= 7; }
        stream.WriteByte((byte)value);
    }

    static void WriteProtoString(Stream stream, int field, string value) {
        if (string.IsNullOrEmpty(value)) return;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(stream, (uint)((field << 3) | 2));
        WriteVarint(stream, (uint)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    static byte[] ReadExactly(NetworkStream stream, int count) {
        byte[] data = new byte[count];
        int offset = 0;
        while (offset < count) {
            int read = stream.Read(data, offset, count - offset);
            if (read <= 0) throw new IOException("服务器提前关闭了连接。");
            offset += read;
        }
        return data;
    }

    static int ReadInt32LE(byte[] data, int offset) {
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }

    static string ShadNetError(byte code) {
        switch (code) {
        case 0: return "注册成功。";
        case 3: return "注册资料无效；请检查 ID、密码和邮箱。";
        case 10: return "服务端创建账号失败。";
        case 11: return "此联机 ID 已存在。";
        case 12: return "此邮箱域名被服务端拒绝。";
        case 13: return "此邮箱已注册。";
        case 23: return "注册密钥不正确。";
        case 24: return "服务端数据库错误。";
        default: return "服务端返回错误代码 " + code + "。";
        }
    }

    static bool RegisterShadNet(LaunchOptions options, out string message, out byte resultCode) {
        resultCode = 255;
        string host;
        int port;
        if (!TryParseLanEndpoint(options.LanAddress, out host, out port)) {
            message = "服务器地址无效。"; return false;
        }
        try {
            using (TcpClient client = new TcpClient()) {
                IAsyncResult pending = client.BeginConnect(host, port, null, null);
                if (!pending.AsyncWaitHandle.WaitOne(5000)) throw new IOException("连接超时。");
                client.EndConnect(pending);
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using (NetworkStream network = client.GetStream()) {
                    byte[] serverHeader = ReadExactly(network, 15);
                    int serverSize = ReadInt32LE(serverHeader, 3);
                    if (serverHeader[0] != 3 || serverSize < 15) throw new InvalidDataException("不是兼容的 shadNet 服务端。");
                    byte[] serverPayload = ReadExactly(network, serverSize - 15);
                    if (serverPayload.Length >= 4 && ReadInt32LE(serverPayload, 0) != 1)
                        throw new InvalidDataException("客户端与服务端协议版本不匹配。");

                    byte[] protobuf;
                    using (MemoryStream proto = new MemoryStream()) {
                        WriteProtoString(proto, 1, options.LanNpid);
                        WriteProtoString(proto, 2, options.LanPassword);
                        WriteProtoString(proto, 4, options.LanEmail);
                        WriteProtoString(proto, 5, options.LanSecret);
                        protobuf = proto.ToArray();
                    }
                    int payloadSize = 4 + protobuf.Length;
                    int totalSize = 15 + payloadSize;
                    byte[] packet = new byte[totalSize];
                    packet[0] = 0;
                    packet[1] = 2;
                    packet[2] = 0;
                    Buffer.BlockCopy(BitConverter.GetBytes(totalSize), 0, packet, 3, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes((ulong)1), 0, packet, 7, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes(protobuf.Length), 0, packet, 15, 4);
                    Buffer.BlockCopy(protobuf, 0, packet, 19, protobuf.Length);
                    network.Write(packet, 0, packet.Length);
                    byte[] replyHeader = ReadExactly(network, 15);
                    int replySize = ReadInt32LE(replyHeader, 3);
                    if (replyHeader[0] != 1 || replySize < 16) throw new InvalidDataException("服务端注册响应格式错误。");
                    byte[] reply = ReadExactly(network, replySize - 15);
                    resultCode = reply[0];
                    message = ShadNetError(resultCode);
                    return resultCode == 0;
                }
            }
        } catch (Exception e) { message = "注册失败：" + e.Message; return false; }
    }

    void RegisterLanAccountFromUi() {
        LaunchOptions options;
        try { options = CollectLanOnly(); }
        catch (Exception e) { Warn("联机设置无效：" + e.Message); return; }
        if (!Regex.IsMatch(options.LanNpid, @"^[A-Za-z0-9_-]{3,16}$") ||
            options.LanPassword.Length == 0 || options.LanEmail.IndexOf('@') <= 0) {
            Warn("请填写有效的 3–16 位联机 ID、密码和邮箱。"); return;
        }
        lanRegisterButton.Enabled = false;
        lanStatus.Text = "正在注册账号……";
        Thread worker = new Thread(new ThreadStart(delegate {
            string message;
            byte resultCode;
            bool ok = RegisterShadNet(options, out message, out resultCode);
            BeginInvoke((Action)delegate {
                lanRegisterButton.Enabled = !busy;
                lanStatus.Text = message;
                lanStatus.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
                if (ok) statusLabel.Text = "shadNet 账号已注册，请保存设置后启动局域网游戏。";
            });
        }));
        worker.IsBackground = true;
        worker.Start();
    }

    static string Default(string key) { return Get(Defaults, key, ""); }
    static string DefaultResolution() {
        Rectangle screen = Screen.PrimaryScreen.Bounds;
        string native = screen.Width.ToString() + "x" + screen.Height.ToString();
        foreach (Choice choice in Resolutions)
            if (choice.Value == native) return native;
        if (screen.Width >= 3840 && screen.Height >= 2160) return "3840x2160";
        if (screen.Width >= 2560 && screen.Height >= 1440) return "2560x1440";
        return "1920x1080";
    }

    bool CheckGame() {
        string dir = gameBox.Text.Trim().Trim('"');
        if (dir.Length == 0) { SetGameStatus("请选择包含 eboot.bin 的 Bloodborne v1.09 游戏目录。", false); return false; }
        if (!File.Exists(Path.Combine(dir, "eboot.bin"))) { SetGameStatus("该目录中没有 eboot.bin。", true); return false; }
        if (!File.Exists(Path.Combine(dir, "sce_sys", "param.sfo"))) {
            SetGameStatus("已找到 eboot.bin，但缺少 sce_sys\\param.sfo，无法确认是否为 v1.09。", true);
            return true;
        }
        SetGameStatus("已找到游戏文件。请确认游戏本体已更新到 v1.09。", false);
        return true;
    }

    void SetGameStatus(string text, bool warning) {
        gameStatus.Text = text;
        gameStatus.ForeColor = warning ? Color.Firebrick : SystemColors.GrayText;
    }

    LaunchOptions Collect() {
        LaunchOptions options = new LaunchOptions();
        options.Root = Path.GetFullPath(rootBox.Text.Trim().Trim('"'));
        options.Game = Path.GetFullPath(gameBox.Text.Trim().Trim('"'));
        options.SaveRoot = Path.GetFullPath(saveRootBox.Text.Trim().Trim('"'));
        options.LanMode = Value(coreMode) == "shadp2p";
        options.PublicOnline = Value(coreMode) == "public_online";
        options.NativeLan = Value(coreMode) == "native_lan" || options.PublicOnline;
        options.LanHost = !options.PublicOnline && lanHost.Checked;
        options.LanSeamless = !options.PublicOnline && lanSeamless.Checked;
        options.LanAddress = options.PublicOnline ?
            (OverlayServerIp().Length != 0 && EmbeddedNetReady() ? "127.0.0.2:31313" : PublicServerAddress()) :
            lanAddressBox.Text.Trim();
        options.LanNpid = options.PublicOnline ? onlineNpidBox.Text.Trim() : lanNpidBox.Text.Trim();
        options.LanPassword = options.PublicOnline ? onlinePasswordBox.Text : lanPasswordBox.Text;
        options.LanEmail = options.PublicOnline ? "" : lanEmailBox.Text.Trim();
        options.LanSecret = options.PublicOnline ? "" : lanSecretBox.Text;
        options.AutoBackup = autoBackup.Checked;
        options.OldHunters = oldHunters.Checked;
        options.DesktopLink = desktopLink.Checked;
        options.StartMenuLink = startMenuLink.Checked;
        options.Ini["upscaler"] = Value(upscaler);
        options.Ini["preset"] = Value(preset);
        options.Ini["sharpen"] = sharpen.Checked ? "1" : "0";
        options.Ini["show_fps"] = showFps.Checked ? "1" : "0";
        options.Ini["output_res"] = Value(resolution);
        options.Ini["fullscreen"] = fullscreen.Checked ? "1" : "0";
        options.Ini["live_resolution"] = Value(live);
        options.Ini["model_lod"] = Value(modelLod);
        foreach (KeyValuePair<string, CheckBox> effect in effects) options.Ini[effect.Key] = effect.Value.Checked ? "1" : "0";
        options.Env["BB_FPS"] = Value(frameRate);
        options.Env["BB_LANGUAGE"] = Value(language);
        options.Env["BB_PRESENT_MODE"] = Value(presentMode);
        foreach (KeyValuePair<string, ComboBox> binding in keyBindings)
            options.Env["BB_KEY_" + binding.Key] = Value(binding.Value);
        foreach (KeyValuePair<string, ComboBox> binding in gamepadBindings)
            options.Env["BB_PAD_" + binding.Key] = Value(binding.Value);
        options.Env["BB_MOUSE_ENABLED"] = mouseEnabled.Checked ? "1" : "0";
        options.Env["BB_MOUSE_INVERT_Y"] = mouseInvert.Checked ? "1" : "0";
        options.Env["BB_MOUSE_SENSITIVITY"] = mouseSensitivity.Value.ToString("0.0", CultureInfo.InvariantCulture);
        options.Env["BB_MOUSE_LEFT"] = Value(mouseLeft);
        options.Env["BB_MOUSE_RIGHT"] = Value(mouseRight);
        options.Env["BB_MOUSE_MIDDLE"] = Value(mouseMiddle);
        options.Env["BB_MOUSE_X1"] = Value(mouseX1);
        options.Env["BB_MOUSE_X2"] = Value(mouseX2);
        options.Env["BB_MOUSE_WHEEL_UP"] = Value(mouseWheelUp);
        options.Env["BB_MOUSE_WHEEL_DOWN"] = Value(mouseWheelDown);
        string onlineHost = "";
        int onlinePort = 31313;
        bool validOnlineEndpoint = options.NativeLan &&
            TryParseLanEndpoint(options.LanAddress, out onlineHost, out onlinePort);
        options.Env["BB_NETWORK_MODE"] = "";
        options.Env["BB_ONLINE"] = validOnlineEndpoint ? "1" : "";
        options.Env["BB_SHADNET_SERVER"] = validOnlineEndpoint ? onlineHost + ":" + onlinePort : "";
        options.Env["BB_SHADNET_WEBAPI"] = validOnlineEndpoint ? "http://" + onlineHost + ":31315" : "";
        options.Env["BB_SHADNET_NPID"] = validOnlineEndpoint ? options.LanNpid : "";
        options.Env["BB_SHADNET_PASSWORD"] = validOnlineEndpoint ? options.LanPassword : "";
        options.Env["BB_UPNP"] = validOnlineEndpoint ? "0" : "";
        bool overlayGame = options.PublicOnline && OverlayServerIp().Length != 0 && EmbeddedNetReady();
        options.Env["SHADPS4_P2P_PORT"] = overlayGame ? "31316" : "";
        options.Env["BB_TSNET_BRIDGE_PORT"] = overlayGame ? "31416" : "";
        options.Env["BB_TSNET_NODE_IP"] = overlayGame ? embeddedNetIp : "";
        options.Env["SHADPS4_BLOODBORNE_SEAMLESS_COOP"] =
            validOnlineEndpoint && options.LanSeamless ? "1" : "";
        List<string> enabledCheats = new List<string>();
        foreach (string[] definition in CheatDefinitions)
            if (cheats[definition[0]].Checked) enabledCheats.Add(definition[2]);
        options.Env["BB_PATCHES"] = string.Join(";", enabledCheats.ToArray());
        return options;
    }

    bool CheckInputs(bool requireInstalled) {
        if (rootBox.Text.Trim().Length == 0) { Warn("请选择运行环境目录。"); return false; }
        if (saveRootBox.Text.Trim().Length == 0) { Warn("请选择独立的玩家数据目录。"); return false; }
        if (!CheckGame()) { Warn("请选择正确的游戏本体目录。"); return false; }
        string root;
        string playerRoot;
        try { root = Path.GetFullPath(rootBox.Text.Trim().Trim('"')); }
        catch (Exception e) { Warn("运行环境目录无效：" + e.Message); return false; }
        try { playerRoot = Path.GetFullPath(saveRootBox.Text.Trim().Trim('"')); }
        catch (Exception e) { Warn("玩家数据目录无效：" + e.Message); return false; }
        if (Path.GetPathRoot(root).Equals(root, StringComparison.OrdinalIgnoreCase)) {
            Warn("请不要把运行环境直接安装到磁盘根目录。"); return false;
        }
        if (Path.GetPathRoot(playerRoot).Equals(playerRoot, StringComparison.OrdinalIgnoreCase)) {
            Warn("请不要把玩家数据直接放在磁盘根目录。"); return false;
        }
        bool lanMode = Value(coreMode) == "shadp2p";
        bool publicOnline = Value(coreMode) == "public_online";
        bool nativeLan = Value(coreMode) == "native_lan" || publicOnline;
        string requiredCore = lanMode ? Path.Combine(root, "shadp2p", "client", "shadPS4.exe") :
            Path.Combine(root, "out", "bb-probe.exe");
        if (requireInstalled && !File.Exists(requiredCore)) {
            Warn("运行环境尚未安装，请先点击“安装 / 更新运行环境”。"); return false;
        }
        if (requireInstalled && !lanMode) {
            string expected = ExpectedProbeHash();
            string installed = FileSha256(Path.Combine(root, "out", "bb-probe.exe"));
            if (expected.Length == 0 || !installed.Equals(expected, StringComparison.OrdinalIgnoreCase)) {
                Warn("运行环境版本过旧或文件不完整，动作键等新功能不会生效。\r\n\r\n" +
                     "请先点击“安装 / 更新运行环境”，完成后再启动游戏。\r\n\r\n" +
                     "当前核心：" + (installed.Length > 12 ? installed.Substring(0, 12) : "无法读取") +
                     "\r\n所需核心：" + (expected.Length > 12 ? expected.Substring(0, 12) : "无法读取"));
                return false;
            }
        }
        if (lanMode || nativeLan) {
            if (publicOnline && OverlayServerIp().Length != 0 && !EmbeddedNetReady()) {
                Warn("请先在“在线联机”页连接内置私网，待显示节点地址后再启动游戏。"); return false;
            }
            if (!Regex.IsMatch(publicOnline ? onlineNpidBox.Text.Trim() : lanNpidBox.Text.Trim(), @"^[A-Za-z0-9_-]{3,16}$")) {
                Warn("联机 ID 必须为 3–16 位英文、数字、下划线或短横线。"); return false;
            }
        }
        if (lanMode || nativeLan) {
            string host;
            int port;
            string address = publicOnline ?
                (OverlayServerIp().Length != 0 && EmbeddedNetReady() ? "127.0.0.2:31313" : PublicServerAddress()) :
                lanAddressBox.Text;
            if (!TryParseLanEndpoint(address, out host, out port)) {
                Warn(publicOnline ? "公共在线服务尚未配置，当前安装包暂不能使用在线联机。" :
                    "shadNet 服务器地址无效。请填写 IPv4 或主机名，例如 192.168.1.20。"); return false;
            }
            if (publicOnline && EffectivePublicPortalUrl().Length == 0) {
                Warn("公共在线账号服务尚未配置，当前安装包暂不能使用在线联机。"); return false;
            }
            if ((publicOnline ? onlinePasswordBox.Text : lanPasswordBox.Text).Length == 0) { Warn("请输入联机账号密码。"); return false; }
            if (!publicOnline && lanHost.Checked && !IsLocalLanEndpoint(lanAddressBox.Text)) {
                Warn("已勾选“本机同时作为局域网服务器”，但服务器地址不是这台电脑。\r\n\r\n" +
                     "远程客户端请取消该勾选，只填写开服电脑的地址；只有开服电脑才需要勾选。\r\n" +
                     "当前服务器地址：" + lanAddressBox.Text.Trim());
                return false;
            }
        }
        return true;
    }

    string ExpectedProbeHash() {
        if (expectedProbeHash != null) return expectedProbeHash;
        expectedProbeHash = "";
        try {
            Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource);
            if (resource == null) return expectedProbeHash;
            using (resource)
            using (ZipArchive archive = new ZipArchive(resource, ZipArchiveMode.Read)) {
                foreach (ZipArchiveEntry entry in archive.Entries) {
                    if (entry.FullName.Replace('\\', '/').Equals("BUILD_INFO.txt", StringComparison.OrdinalIgnoreCase)) {
                        using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8)) {
                            foreach (string line in reader.ReadToEnd().Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None))
                                if (line.StartsWith("bb-probe SHA256: ", StringComparison.Ordinal))
                                    expectedProbeHash = line.Substring("bb-probe SHA256: ".Length).Trim();
                        }
                        break;
                    }
                }
            }
        } catch { expectedProbeHash = ""; }
        return expectedProbeHash;
    }

    static string FileSha256(string path) {
        try {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        } catch { return ""; }
    }

    void Warn(string text) { MessageBox.Show(this, text, TitleText, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

    string SaveRootPath() {
        string value = saveRootBox.Text.Trim().Trim('"');
        string root = value.Length == 0 ? SaveTools.DefaultRoot : Path.GetFullPath(value);
        return coreMode != null && Value(coreMode) == "shadp2p" ?
            Path.Combine(root, "shadp2p-home", "1000") : root;
    }

    void RefreshSaveManager() {
        if (backupList == null) return;
        backupList.Items.Clear();
        if (characterList != null) characterList.Items.Clear();
        try {
            string root = SaveRootPath();
            string savedata = Path.Combine(root, "savedata");
            if (Directory.Exists(savedata)) {
                try {
                    SaveValidation current = SaveTools.ValidateDirectory(savedata);
                    saveStatus.Text = string.Format("当前存档：{0}，{1} 个数据文件，{2:N1} MB，最后修改 {3}",
                        current.TitleId.Length == 0 ? "区域未知" : current.TitleId, current.DataFiles,
                        current.Bytes / 1048576.0, current.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                    saveStatus.ForeColor = SystemColors.GrayText;
                    if (characterList != null) {
                        foreach (SaveCharacter character in SaveTools.ListCharacters(root)) {
                            ListViewItem row = new ListViewItem((character.Index + 1).ToString());
                            row.SubItems.Add(Path.GetFileName(character.File));
                            row.SubItems.Add(character.Name);
                            row.SubItems.Add(character.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                            row.Tag = character;
                            characterList.Items.Add(row);
                        }
                    }
                } catch (Exception e) {
                    saveStatus.Text = "存档目录存在，但校验未通过：" + e.Message;
                    saveStatus.ForeColor = Color.Firebrick;
                }
            } else {
                saveStatus.Text = "独立目录中还没有存档；首次启动会自动迁移旧运行环境的存档。";
                saveStatus.ForeColor = SystemColors.GrayText;
            }
            string backups = SaveTools.BackupRoot(root);
            if (Directory.Exists(backups)) {
                List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(backups).GetFiles("*.zip"));
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                foreach (FileInfo file in files) {
                    ListViewItem item = new ListViewItem(file.Name);
                    item.SubItems.Add(file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                    item.SubItems.Add(string.Format("{0:N1} MB", file.Length / 1048576.0));
                    item.Tag = file.FullName;
                    backupList.Items.Add(item);
                }
            }
        } catch (Exception e) {
            saveStatus.Text = "读取存档失败：" + e.Message;
            saveStatus.ForeColor = Color.Firebrick;
        }
        restoreButton.Enabled = !busy && backupList.SelectedItems.Count > 0;
        if (renameCharacterButton != null)
            renameCharacterButton.Enabled = !busy && characterList.SelectedItems.Count > 0;
    }

    string AskCharacterName(string current) {
        using (Form dialog = new Form()) {
            dialog.Text = "修改角色名";
            dialog.Font = Font;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowInTaskbar = false;
            dialog.ClientSize = new Size(S(390), S(145));
            Label prompt = new Label { AutoSize = true, Text = "新角色名（1–16 个英文字符）：",
                Location = new Point(S(16), S(16)) };
            TextBox inputBox = new TextBox { Text = current, MaxLength = 16,
                Location = new Point(S(16), S(44)), Width = S(355) };
            Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK,
                AutoSize = true, Location = new Point(S(214), S(94)) };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel,
                AutoSize = true, Location = new Point(S(298), S(94)) };
            dialog.Controls.AddRange(new Control[] { prompt, inputBox, ok, cancel });
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            dialog.Shown += delegate { inputBox.Focus(); inputBox.SelectAll(); };
            return dialog.ShowDialog(this) == DialogResult.OK ? inputBox.Text : null;
        }
    }

    void RenameSelectedCharacter() {
        if (characterList.SelectedItems.Count == 0) return;
        SaveCharacter character = (SaveCharacter)characterList.SelectedItems[0].Tag;
        string name = AskCharacterName(character.Name);
        if (name == null || name == character.Name) return;
        try {
            string backup = SaveTools.CreateBackup(SaveRootPath(), "before-rename");
            if (backup == null) throw new InvalidDataException("没有找到可备份的当前存档。");
            SaveTools.RenameCharacter(character, name);
            statusLabel.Text = "角色名已从“" + character.Name + "”修改为“" + name + "”；原存档已备份。";
            RefreshSaveManager();
        } catch (Exception e) { Warn("修改角色名失败：" + e.Message); }
    }

    void ManualBackup() {
        try {
            string path = SaveTools.CreateBackup(SaveRootPath(), "manual");
            if (path == null) { Warn("当前目录中没有可备份的 Bloodborne 存档。"); return; }
            statusLabel.Text = "已创建存档备份：" + Path.GetFileName(path);
            RefreshSaveManager();
        } catch (Exception e) { Warn("备份存档失败：" + e.Message); }
    }

    void RestoreSelectedBackup() {
        if (backupList.SelectedItems.Count == 0) return;
        string path = (string)backupList.SelectedItems[0].Tag;
        if (MessageBox.Show(this, "确定恢复此备份吗？\n\n" + Path.GetFileName(path) +
            "\n\n当前存档会先自动备份，然后再替换。", TitleText,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try {
            string root = SaveRootPath();
            SaveTools.CreateBackup(root, "before-restore");
            SaveTools.RestoreBackup(path, root);
            statusLabel.Text = "存档已恢复。";
            RefreshSaveManager();
        } catch (Exception e) { Warn("恢复存档失败，原存档已尽量保留：" + e.Message); }
    }

    void ExportCurrentSave() {
        try {
            string backup = SaveTools.CreateBackup(SaveRootPath(), "export");
            if (backup == null) { Warn("当前目录中没有可导出的 Bloodborne 存档。"); return; }
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "ZIP 存档包 (*.zip)|*.zip",
                FileName = "Bloodborne-save-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                File.Copy(backup, dialog.FileName, true);
                statusLabel.Text = "存档已导出到：" + dialog.FileName;
            }
            RefreshSaveManager();
        } catch (Exception e) { Warn("导出存档失败：" + e.Message); }
    }

    void ImportExternalFolder() {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择外部 Bloodborne 存档文件夹",
            ShowNewFolderButton = false }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            ValidateAndImport(dialog.SelectedPath, null);
        }
    }

    void ImportExternalZip() {
        using (OpenFileDialog dialog = new OpenFileDialog { Filter = "ZIP 存档包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            Title = "选择外部 Bloodborne 存档包" }) {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string root = SaveRootPath();
            Directory.CreateDirectory(root);
            string extracted = null;
            try {
                extracted = SaveTools.ExtractZipSafely(dialog.FileName, root);
                ValidateAndImport(extracted, extracted);
            } catch (Exception e) { Warn("读取 ZIP 存档失败：" + e.Message); }
            finally { if (extracted != null && Directory.Exists(extracted)) Directory.Delete(extracted, true); }
        }
    }

    void ValidateAndImport(string sourcePath, string temporaryRoot) {
        try {
            SaveValidation source = SaveTools.ValidateDirectory(sourcePath);
            string targetId = SaveTools.ReadTitleId(gameBox.Text.Trim().Trim('"'));
            if (targetId.Length == 0) targetId = source.TitleId;
            string message = source.Summary + "\r\n\r\n目标区域 ID：" +
                (targetId.Length == 0 ? "无法识别" : targetId) + "\r\n\r\n是否导入？";
            if (MessageBox.Show(this, message, "外部存档验证结果", MessageBoxButtons.YesNo,
                MessageBoxIcon.Information) != DialogResult.Yes) return;
            if (targetId.Length == 0) throw new InvalidDataException("请选择正确的游戏本体，以便读取区域 ID。");
            string root = SaveRootPath();
            Directory.CreateDirectory(root);
            SaveTools.CreateBackup(root, "before-import");
            SaveTools.ImportSave(source, root, targetId);
            statusLabel.Text = "外部存档已验证并导入到 " + targetId + "。";
            RefreshSaveManager();
        } catch (Exception e) { Warn("外部存档验证／导入失败：" + e.Message); }
    }

    void MigrateLegacySaveIfNeeded(LaunchOptions options) {
        Directory.CreateDirectory(options.SaveRoot);
        if (SaveTools.HasSave(options.SaveRoot)) return;
        string legacy = Path.Combine(options.Root, "user");
        if (Path.GetFullPath(legacy).Equals(Path.GetFullPath(options.SaveRoot), StringComparison.OrdinalIgnoreCase) ||
            !SaveTools.HasSave(legacy)) return;
        string oldSave = Path.Combine(legacy, "savedata");
        string newSave = Path.Combine(options.SaveRoot, "savedata");
        SaveTools.CopyTree(oldSave, newSave, false);
        string oldTrophy = Path.Combine(legacy, "custom_trophy");
        if (Directory.Exists(oldTrophy)) SaveTools.CopyTree(oldTrophy, Path.Combine(options.SaveRoot, "custom_trophy"), false);
        SaveTools.ValidateDirectory(newSave);
        Log("已把旧运行环境的存档复制到独立玩家数据目录：" + options.SaveRoot);
    }

    void FormatReleaseNotes(RichTextBox notes) {
        string source = ReleaseNotes.Length == 0 ? "内置更新说明无法读取。" : ReleaseNotes;
        notes.Text = source.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        Font titleFont = new Font(Font.FontFamily, 13f, FontStyle.Bold);
        Font headingFont = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
        Font bodyFont = new Font(Font.FontFamily, 9.5f, FontStyle.Regular);
        string[] lines = notes.Lines;
        for (int i = 0; i < lines.Length; ++i) {
            int start = notes.GetFirstCharIndexFromLine(i);
            if (start < 0) continue;
            int length = lines[i].Length;
            notes.Select(start, length);
            notes.SelectionFont = bodyFont;
            notes.SelectionColor = SystemColors.WindowText;
            notes.SelectionBackColor = SystemColors.Window;
            notes.SelectionIndent = 0;
            notes.SelectionRightIndent = 0;

            if (i == 0) {
                notes.SelectionFont = titleFont;
                notes.SelectionColor = Color.FromArgb(88, 24, 24);
            } else if (Regex.IsMatch(lines[i], @"^v\d+\.\d+\.\d+")) {
                notes.SelectionFont = headingFont;
                bool current = PackageVersion.Length > 0 && lines[i].StartsWith("v" + PackageVersion,
                    StringComparison.OrdinalIgnoreCase);
                notes.SelectionColor = current ? Color.FromArgb(142, 42, 34) : Color.FromArgb(45, 75, 110);
                if (current) notes.SelectionBackColor = Color.FromArgb(255, 248, 225);
            } else if (lines[i].StartsWith("- ", StringComparison.Ordinal)) {
                notes.SelectionIndent = S(14);
                notes.SelectionRightIndent = S(8);
            } else if (lines[i].StartsWith("版本规则", StringComparison.Ordinal)) {
                notes.SelectionFont = headingFont;
            }
        }
        notes.Select(0, 0);
        notes.ScrollToCaret();
    }

    bool ConfirmInstallRequirements() {
        string message =
            "玩家版建议最低配置（低于此配置仍可能运行，但不保证稳定）：\r\n\r\n" +
            "• 显卡：至少 4 GB 显存\r\n" +
            "• 内存：至少 16 GB\r\n" +
            "• Windows 虚拟内存：至少 16 GB\r\n\r\n" +
            "Windows 虚拟内存设置方法：\r\n" +
            "1. 按 Win + R，输入 sysdm.cpl 并回车。\r\n" +
            "2. 打开“高级”→“性能”下的“设置”→“高级”。\r\n" +
            "3. 在“虚拟内存”处点“更改”。\r\n" +
            "4. 推荐勾选“自动管理所有驱动器的分页文件大小”；也可自定义为初始值 16384 MB、最大值 32768 MB。\r\n" +
            "5. 保存设置并重启 Windows。\r\n\r\n" +
            "是否继续安装／更新运行环境？";
        return MessageBox.Show(this, message, "安装前配置提示", MessageBoxButtons.YesNo,
            MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    void StartInstall() {
        if (!ConfirmInstallRequirements()) return;
        if (!CheckInputs(false)) return;
        LaunchOptions options = Collect();
        busy = true;
        UpdateButtons();
        tabs.SelectedTab = logPage;
        progress.Style = ProgressBarStyle.Continuous;
        progress.Value = 0;
        Thread worker = new Thread(new ThreadStart(delegate { Install(options); }));
        worker.IsBackground = true;
        worker.Start();
    }

    void Install(LaunchOptions options) {
        string error = null;
        try {
            Log("正在安装到：" + options.Root);
            Directory.CreateDirectory(options.Root);
            ExtractPayload(options.Root);
            MigrateLegacySaveIfNeeded(options);
            Configure(options, true);
            string installedLauncher = Path.Combine(options.Root, InstalledExeName);
            string current = Path.GetFullPath(Application.ExecutablePath);
            if (!current.Equals(Path.GetFullPath(installedLauncher), StringComparison.OrdinalIgnoreCase)) {
                Log("复制中文启动器……");
                File.Copy(current, installedLauncher, true);
            }
            if (options.DesktopLink) MakeShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), installedLauncher, options);
            if (options.StartMenuLink) MakeShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs), installedLauncher, options);
        } catch (Exception e) { error = e.Message; Log("安装失败：" + e); }
        BeginInvoke((Action)delegate { InstallFinished(error); });
    }

    void ExtractPayload(string destination) {
        Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource);
        if (resource == null) throw new InvalidOperationException("EXE 中没有找到内置运行环境。请重新生成或重新下载完整文件。");
        string rootFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (resource)
        using (ZipArchive archive = new ZipArchive(resource, ZipArchiveMode.Read)) {
            int count = archive.Entries.Count;
            BeginInvoke((Action)delegate { progress.Maximum = Math.Max(1, count); });
            for (int i = 0; i < count; ++i) {
                ZipArchiveEntry entry = archive.Entries[i];
                string target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("内置文件路径不安全：" + entry.FullName);
                if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream input = entry.Open())
                using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                if ((i % 20) == 0 || i + 1 == count) {
                    int shown = i + 1;
                    BeginInvoke((Action)delegate {
                        progress.Value = Math.Min(progress.Maximum, shown);
                        statusLabel.Text = string.Format("正在解压运行环境：{0}/{1}", shown, count);
                    });
                }
            }
        }
        Log("运行环境解压完成。");
    }

    void InstallFinished(string error) {
        busy = false;
        UpdateButtons();
        if (error == null) {
            progress.Value = progress.Maximum;
            statusLabel.Text = "安装完成，可以启动游戏。";
            Log("安装完成。");
            if (MessageBox.Show(this, "运行环境已安装完成。现在启动游戏吗？", TitleText,
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) StartGame();
        } else {
            statusLabel.Text = "安装失败：" + error;
            MessageBox.Show(this, "安装失败：" + error + "\n\n详细信息见“运行日志”。", TitleText,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SaveSettingsOnly() {
        if (!CheckInputs(true)) return;
        try {
            Configure(Collect(), false);
            statusLabel.Text = "设置已保存。";
        } catch (Exception e) { Warn("保存设置失败：" + e.Message); }
    }

    void Configure(LaunchOptions options, bool shortcuts) {
        Directory.CreateDirectory(Path.Combine(options.Root, "out"));
        ConfigureAddons(options);
        WriteIni(Path.Combine(options.Root, "bbport.ini"), options.Ini);
        File.WriteAllText(Path.Combine(options.Root, "out", "game_dir.txt"), options.Game, new UTF8Encoding(false));
        Dictionary<string, string> state = new Dictionary<string, string>();
        state["install_root"] = options.Root;
        state["game_dir"] = options.Game;
        state["save_root"] = options.SaveRoot;
        state["auto_backup"] = options.AutoBackup ? "1" : "0";
        state["old_hunters"] = options.OldHunters ? "1" : "0";
        state["core_mode"] = options.LanMode ? "shadp2p" : options.PublicOnline ? "public_online" :
            options.NativeLan ? "native_lan" : "native";
        state["lan_address"] = lanAddressBox.Text.Trim();
        state["lan_npid"] = lanNpidBox.Text.Trim();
        state["lan_password_dpapi"] = ProtectSecret(lanPasswordBox.Text);
        state["lan_email"] = lanEmailBox.Text.Trim();
        state["lan_secret_dpapi"] = ProtectSecret(lanSecretBox.Text);
        state["online_npid"] = onlineNpidBox.Text.Trim();
        state["online_password_dpapi"] = ProtectSecret(onlinePasswordBox.Text);
        state["online_nickname"] = onlineNicknameBox.Text.Trim();
        state["online_show_in_list"] = onlineShowInList.Checked ? "1" : "0";
        state["online_application_id"] = onlineApplicationId;
        state["online_application_token_dpapi"] = ProtectSecret(onlineApplicationToken);
        state["lan_host"] = options.LanHost ? "1" : "0";
        state["lan_seamless"] = options.LanSeamless ? "1" : "0";
        state["quality_profile"] = Value(qualityProfile);
        state["fps"] = options.Env["BB_FPS"];
        state["language"] = options.Env["BB_LANGUAGE"];
        state["present_mode"] = options.Env["BB_PRESENT_MODE"];
        state["desktop_link"] = options.DesktopLink ? "1" : "0";
        state["start_menu_link"] = options.StartMenuLink ? "1" : "0";
        foreach (KeyValuePair<string, string> item in options.Env)
            if (item.Key.StartsWith("BB_KEY_") || item.Key.StartsWith("BB_MOUSE_") ||
                item.Key.StartsWith("BB_PAD_")) state[item.Key] = item.Value;
        foreach (string[] definition in CheatDefinitions)
            state["cheat_" + definition[0]] = cheats[definition[0]].Checked ? "1" : "0";
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile));
        WriteSimple(stateFile, state);
        if (options.LanMode && File.Exists(Path.Combine(options.Root, "shadp2p", "client", "shadPS4.exe")))
            ConfigureLanClient(options);
        Log("设置已保存：" + Path.Combine(options.Root, "bbport.ini"));
    }

    void ConfigureAddons(LaunchOptions options) {
        string path = Path.Combine(options.SaveRoot, "addcont", "CUSA03173", "SPEXPANSIONDLC03");
        if (options.OldHunters) {
            Directory.CreateDirectory(path);
            Log("已启用《老猎人》DLC 授权目录：" + path);
        } else if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) {
            // Only remove the empty marker created by this launcher. Never remove a user's dump.
            Directory.Delete(path, false);
        }
    }

    static void WriteIni(string path, Dictionary<string, string> values) {
        List<string> lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
        HashSet<string> written = new HashSet<string>();
        for (int i = 0; i < lines.Count; ++i) {
            string line = lines[i];
            int equals = line.IndexOf('=');
            if (equals <= 0 || line.TrimStart().StartsWith("#")) continue;
            string key = line.Substring(0, equals).Trim();
            if (values.ContainsKey(key)) { lines[i] = key + "=" + values[key]; written.Add(key); }
        }
        if (lines.Count == 0) lines.Add("# bbport settings (in-game menu: Insert / L3+R3)");
        foreach (KeyValuePair<string, string> item in values)
            if (!written.Contains(item.Key)) lines.Add(item.Key + "=" + item.Value);
        File.WriteAllText(path, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
    }

    static void WriteSimple(string path, Dictionary<string, string> values) {
        StringBuilder text = new StringBuilder();
        foreach (KeyValuePair<string, string> item in values) text.Append(item.Key).Append('=').Append(item.Value).Append("\r\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    void MakeShortcut(string folder, string launcher, LaunchOptions options) {
        try {
            string icon = MakeIcon(options);
            string path = Path.Combine(folder, "Bloodborne PC.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type linkType = link.GetType();
            Action<string, object> set = delegate(string name, object value) {
                linkType.InvokeMember(name, BindingFlags.SetProperty, null, link, new object[] { value });
            };
            set("TargetPath", launcher);
            set("Arguments", "--install-root \"" + options.Root + "\"");
            set("WorkingDirectory", options.Root);
            set("Description", "Bloodborne PC 中文启动器");
            if (icon != null) set("IconLocation", icon + ",0");
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Log("已创建快捷方式：" + path);
        } catch (Exception e) { Log("创建快捷方式失败：" + e.Message); }
    }

    string MakeIcon(LaunchOptions options) {
        string png = Path.Combine(options.Game, "sce_sys", "icon0.png");
        string ico = Path.Combine(options.Root, "out", "bloodborne.ico");
        try {
            if (!File.Exists(png)) return null;
            byte[] image;
            using (Bitmap source = new Bitmap(png))
            using (Bitmap small = new Bitmap(source, new Size(256, 256)))
            using (MemoryStream stream = new MemoryStream()) {
                small.Save(stream, ImageFormat.Png);
                image = stream.ToArray();
            }
            using (BinaryWriter writer = new BinaryWriter(File.Create(ico))) {
                writer.Write((short)0); writer.Write((short)1); writer.Write((short)1);
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((short)1); writer.Write((short)32); writer.Write(image.Length); writer.Write(22);
                writer.Write(image);
            }
            return ico;
        } catch (Exception e) { Log("生成游戏图标失败：" + e.Message); return null; }
    }

    void StartGame() {
        if (!CheckInputs(true)) return;
        LaunchOptions options = Collect();
        if (options.LanMode || options.NativeLan) {
            string titleId = SaveTools.ReadTitleId(options.Game);
            if (!titleId.Equals("CUSA03173", StringComparison.OrdinalIgnoreCase)) {
                string shown = titleId.Length == 0 ? "无法识别" : titleId;
                if (MessageBox.Show(this, "当前游戏区域为“" + shown + "”。\r\n\r\n" +
                    "当前联机实现只以 CUSA03173 1.09 为验证目标；其他区域可能无法登录、匹配或进入游戏。仍要继续吗？",
                    "联机兼容性提示", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
        }
        try {
            if (!options.LanMode) MigrateLegacySaveIfNeeded(options);
            Configure(options, false);
        }
        catch (Exception e) { Warn("保存设置失败：" + e.Message); return; }
        currentRunPublic = options.PublicOnline;
        if (options.PublicOnline) {
            publicLoginSucceeded = false;
            onlineLoginStatus.Text = "登录状态：准备连接（尚未认证）";
        }
        if (options.LanMode) {
            StartLanGame(options);
            return;
        }
        string python = Path.Combine(options.Root, "python", "python.exe");
        string script = Path.Combine(options.Root, "scripts", "run_game.py");
        if (!File.Exists(python) || !File.Exists(script)) { Warn("运行环境不完整，请重新点击“安装 / 更新运行环境”。"); return; }
        busy = true;
        UpdateButtons();
        tabs.SelectedTab = logPage;
        progress.Style = ProgressBarStyle.Marquee;
        statusLabel.Text = "正在准备并启动游戏……";
        Log("");
        Log("正在启动游戏：" + options.Game);
        Thread worker = new Thread(new ThreadStart(delegate { RunGame(options, python, script); }));
        worker.IsBackground = true;
        worker.Start();
    }

    void StartLanGame(LaunchOptions options) {
        string core = Path.Combine(options.Root, "shadp2p", "client", "shadPS4.exe");
        if (!File.Exists(core)) { Warn("shadp2p 核心不完整，请重新点击“安装 / 更新运行环境”。"); return; }
        busy = true;
        UpdateButtons();
        tabs.SelectedTab = logPage;
        progress.Style = ProgressBarStyle.Marquee;
        statusLabel.Text = "正在准备并启动局域网游戏……";
        Log("");
        Log("正在使用 shadp2p 启动游戏：" + options.Game);
        Thread worker = new Thread(new ThreadStart(delegate { RunLanGame(options, core); }));
        worker.IsBackground = true;
        worker.Start();
    }

    static string ShadNetLoginHint(string line) {
        string lower = (line ?? "").ToLowerInvariant();
        if (!lower.Contains("login rejected")) return null;
        if (lower.Contains("already logged in") || lower.Contains("error code 6") || lower.EndsWith("code 6"))
            return "联机诊断：此 NPID 已在其他客户端登录。请关闭旧游戏／旧模拟器，或为每台电脑注册不同的 NPID；必要时重启 shadNet。";
        if (lower.Contains("not registered") || lower.Contains("error code 7") || lower.EndsWith("code 7"))
            return "联机诊断：此 NPID 未在当前 shadNet 服务器注册，请在同一服务器重新注册账号。";
        if (lower.Contains("password is incorrect") || lower.Contains("error code 8") || lower.EndsWith("code 8"))
            return "联机诊断：联机密码错误，请重新输入该 NPID 在当前服务器上的密码。";
        return "联机诊断：shadNet 拒绝登录，请检查 NPID、密码和服务器注册状态。";
    }

    void RunLanGame(LaunchOptions options, string core) {
        string error = null;
        int exitCode = -1;
        string logPath = null;
        List<string> recent = new List<string>();
        object outputLock = new object();
        StreamWriter runLog = null;
        try {
            ConfigureLanClient(options);
            if (options.LanHost) {
                string serverError = StartLanServerProcess(options);
                if (serverError != null) throw new InvalidOperationException("本机 shadNet 启动失败：" + serverError);
            }
            string statusMessage = "";
            bool online = false;
            for (int attempt = 0; attempt < (options.LanHost ? 10 : 1); ++attempt) {
                if (CheckLanStatus(options.LanAddress, out statusMessage)) { online = true; break; }
                if (options.LanHost) Thread.Sleep(500);
            }
            if (!online) throw new IOException(statusMessage + " 请检查服务器、防火墙和局域网地址。");

            string playerRoot = LanPlayerRoot(options);
            Directory.CreateDirectory(playerRoot);
            if (options.AutoBackup) {
                string backup = SaveTools.CreateBackup(playerRoot, "auto-lan-before");
                if (backup != null) Log("局域网启动前存档备份：" + backup);
                SaveTools.PruneAutomaticBackups(playerRoot, 20);
            }
            string logDirectory = Path.Combine(options.Root, "logs");
            Directory.CreateDirectory(logDirectory);
            logPath = Path.Combine(logDirectory, "lan-run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            runLog = new StreamWriter(logPath, false, new UTF8Encoding(false));
            runLog.AutoFlush = true;
            lastRunLog = logPath;
            Action<string> capture = delegate(string line) {
                if (line == null) return;
                string hint = ShadNetLoginHint(line);
                lock (outputLock) {
                    runLog.WriteLine(line);
                    recent.Add(line);
                    if (hint != null) { runLog.WriteLine(hint); recent.Add(hint); }
                    if (recent.Count > 240) recent.RemoveAt(0);
                }
                Log(line);
                if (hint != null) Log(hint);
            };
            capture("Bloodborne PC shadp2p LAN launch log " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            capture("Core: " + core);
            capture("Game: " + options.Game);
            capture("Server: " + options.LanAddress);
            capture("NPID: " + options.LanNpid);
            capture("LAN player data: " + playerRoot);
            ProcessStartInfo info = new ProcessStartInfo(core,
                "--game \"" + Path.Combine(options.Game, "eboot.bin") + "\" --user-id 1000") {
                UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(core), RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8 };
            if (options.LanSeamless) info.EnvironmentVariables["SHADPS4_BLOODBORNE_SEAMLESS_COOP"] = "1";
            else info.EnvironmentVariables.Remove("SHADPS4_BLOODBORNE_SEAMLESS_COOP");
            using (Process process = new Process { StartInfo = info }) {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) capture(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) capture(e.Data); };
                process.Start();
                launcherProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                exitCode = process.ExitCode;
                launcherProcess = null;
            }
            if (options.AutoBackup) {
                string backup = SaveTools.CreateBackup(playerRoot, "auto-lan-after");
                if (backup != null) Log("局域网退出后存档备份：" + backup);
                SaveTools.PruneAutomaticBackups(playerRoot, 20);
            }
        } catch (Exception e) { error = e.Message; Log("shadp2p 启动失败：" + e); }
        finally { if (runLog != null) { lock (outputLock) runLog.Dispose(); } }
        string diagnostic = CrashDiagnostic(recent);
        BeginInvoke((Action)delegate { GameFinished(error, exitCode, logPath, diagnostic); });
    }

    void RunGame(LaunchOptions options, string python, string script) {
        string error = null;
        int exitCode = -1;
        string logPath = null;
        List<string> recent = new List<string>();
        object outputLock = new object();
        StreamWriter runLog = null;
        try {
            Directory.CreateDirectory(options.SaveRoot);
            if (options.AutoBackup || options.Env["BB_PATCHES"].Length > 0) {
                string backup = SaveTools.CreateBackup(options.SaveRoot,
                    options.Env["BB_PATCHES"].Length > 0 ? "auto-before-cheats" : "auto-before");
                if (backup != null) Log("启动前存档备份：" + backup);
                SaveTools.PruneAutomaticBackups(options.SaveRoot, 20);
            }
            string logDirectory = Path.Combine(options.Root, "logs");
            Directory.CreateDirectory(logDirectory);
            logPath = Path.Combine(logDirectory, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            runLog = new StreamWriter(logPath, false, new UTF8Encoding(false));
            runLog.AutoFlush = true;
            lastRunLog = logPath;
            Action<string> capture = delegate(string line) {
                if (line == null) return;
                if (options.PublicOnline) {
                    string host;
                    int ignoredPort;
                    if (TryParseLanEndpoint(options.LanAddress, out host, out ignoredPort))
                        line = line.Replace(host, "[公共服务]");
                    if (line.IndexOf("shadNet authentication confirmed", StringComparison.OrdinalIgnoreCase) >= 0) {
                        publicLoginSucceeded = true;
                        if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate {
                            onlineLoginStatus.Text = "登录状态：已登录（" + options.LanNpid + "）";
                            onlineLoginStatus.ForeColor = Color.DarkGreen;
                        });
                    } else if (line.IndexOf("shadNet authentication failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("Login rejected", StringComparison.OrdinalIgnoreCase) >= 0) {
                        if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate {
                            onlineLoginStatus.Text = "登录状态：认证失败，请检查账号、密码和日志";
                            onlineLoginStatus.ForeColor = Color.Firebrick;
                        });
                    }
                }
                string hint = ShadNetLoginHint(line);
                lock (outputLock) {
                    runLog.WriteLine(line);
                    recent.Add(line);
                    if (hint != null) { runLog.WriteLine(hint); recent.Add(hint); }
                    if (recent.Count > 240) recent.RemoveAt(0);
                }
                Log(line);
                if (hint != null) Log(hint);
            };
            capture("Bloodborne PC launch log " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            capture("Runtime: " + options.Root);
            capture("Game: " + options.Game);
            capture("Player data: " + options.SaveRoot);
            if (options.NativeLan) {
                capture("Native bbport online: enabled; server=" + (options.PublicOnline ? "[公共服务]" : options.LanAddress) +
                    "; NPID=" + options.LanNpid + "; seamless=" + (options.LanSeamless ? "on" : "off"));
                string statusMessage = "";
                bool online = CheckLanStatus(options.LanAddress, out statusMessage);
                bool localEndpoint = IsLocalLanEndpoint(options.LanAddress);
                if (!online && !options.PublicOnline && (options.LanHost || localEndpoint)) {
                    if (!options.LanHost)
                        capture("Native bbport online: local endpoint detected; starting bundled shadNet automatically");
                    string serverError = StartLanServerProcess(options);
                    if (serverError != null) throw new InvalidOperationException("本机 shadNet 启动失败：" + serverError);
                    for (int attempt = 0; attempt < 10; ++attempt) {
                        if (CheckLanStatus(options.LanAddress, out statusMessage)) { online = true; break; }
                        Thread.Sleep(500);
                    }
                }
                if (!online) {
                    if (options.PublicOnline) throw new IOException("公共在线服务暂时不可用，请稍后重试。");
                    string localHint = localEndpoint
                        ? " 地址指向本机，但内置服务器未能就绪；请检查 shadnet.exe、防火墙或注册密钥。"
                        : " 请确认开服电脑已启动 shadNet，并检查防火墙和联机地址。";
                    throw new IOException(statusMessage + localHint);
                }
                capture("Native bbport online: shadNet WebAPI reachable");
                if (localEndpoint && !options.PublicOnline) {
                    if (!Regex.IsMatch(options.LanNpid, @"^[A-Za-z0-9_-]{3,16}$") ||
                        options.LanPassword.Length == 0 || options.LanEmail.IndexOf('@') <= 0)
                        throw new InvalidDataException("本机 shadNet 首次使用需要填写有效的联机 ID、密码和注册邮箱。");
                    string registrationMessage;
                    byte registrationCode;
                    bool registered = RegisterShadNet(options, out registrationMessage, out registrationCode);
                    if (!registered && registrationCode != 11)
                        throw new InvalidOperationException("本机 shadNet 账号准备失败：" + registrationMessage);
                    capture(registrationCode == 0
                        ? "Native bbport online: local shadNet account registered successfully"
                        : "Native bbport online: local shadNet account already exists");
                }
            }
            ProcessStartInfo info = new ProcessStartInfo(python,
                "\"" + script + "\" --game-dir \"" + options.Game + "\"") {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = options.Root,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            info.EnvironmentVariables["BB_PREBUILT"] = "1";
            info.EnvironmentVariables["BB_DATA_DIR"] = options.Root;
            info.EnvironmentVariables["BB_RUNTIME"] = Path.Combine(options.Root, "runtime");
            info.EnvironmentVariables["BB_PYTHON"] = python;
            info.EnvironmentVariables["BB_USER_DIR"] = options.SaveRoot;
            foreach (KeyValuePair<string, string> item in options.Env) {
                if (item.Value.Length > 0) info.EnvironmentVariables[item.Key] = item.Value;
                else info.EnvironmentVariables.Remove(item.Key);
            }
            info.EnvironmentVariables["PATH"] = Path.Combine(options.Root, "runtime") + Path.PathSeparator +
                Environment.GetEnvironmentVariable("PATH");
            using (Process process = new Process { StartInfo = info }) {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) capture(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) capture(e.Data); };
                process.Start();
                launcherProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                exitCode = process.ExitCode;
                launcherProcess = null;
            }
            if (options.AutoBackup) {
                string backup = SaveTools.CreateBackup(options.SaveRoot, "auto-after");
                if (backup != null) Log("退出后存档备份：" + backup);
                SaveTools.PruneAutomaticBackups(options.SaveRoot, 20);
            }
        } catch (Exception e) { error = e.Message; Log("启动失败：" + e); }
        finally { if (runLog != null) { lock (outputLock) runLog.Dispose(); } }
        string diagnostic = CrashDiagnostic(recent);
        BeginInvoke((Action)delegate { GameFinished(error, exitCode, logPath, diagnostic); });
    }

    static string CrashDiagnostic(List<string> lines) {
        List<string> important = new List<string>();
        foreach (string line in lines) {
            string lower = line.ToLowerInvariant();
            if (lower.Contains("crash report") || lower.Contains(" fault ") || lower.Contains("target region") ||
                lower.Contains("exception") || lower.Contains("rip=") || lower.Contains("rax=") ||
                lower.StartsWith("  #") || lower.Contains("gpu guest writes") || lower.Contains("stop:") ||
                lower.Contains("memory pool") || lower.Contains("allocatedirectmemory") ||
                lower.Contains("windows error 1450") || lower.Contains("windows error 1455"))
                important.Add(line);
        }
        List<string> chosen = important.Count > 0 ? important : lines;
        int first = Math.Max(0, chosen.Count - 45);
        StringBuilder result = new StringBuilder();
        for (int i = first; i < chosen.Count; ++i) result.Append(chosen[i]).Append("\r\n");
        return result.ToString().Trim();
    }

    void GameFinished(string error, int exitCode, string logPath, string diagnostic) {
        busy = false;
        if (currentRunPublic) {
            onlineLoginStatus.Text = publicLoginSucceeded
                ? "登录状态：未登录（上次游戏中认证成功）" : "登录状态：未登录（上次未完成认证）";
            onlineLoginStatus.ForeColor = publicLoginSucceeded ? SystemColors.GrayText : Color.Firebrick;
            currentRunPublic = false;
        }
        progress.Style = ProgressBarStyle.Continuous;
        progress.Value = 0;
        UpdateButtons();
        if (error != null) {
            statusLabel.Text = "启动失败：" + error;
            Warn("启动游戏失败：" + error);
        } else if (exitCode == 0) {
            statusLabel.Text = "游戏已退出。";
            Log("游戏已正常退出。");
        } else {
            statusLabel.Text = "游戏已退出，返回代码：" + exitCode;
            Log("游戏返回代码：" + exitCode);
            uint native = unchecked((uint)exitCode);
            if (exitCode == 139 || native == 0xC0000005u) {
                bool commitFailure = diagnostic.IndexOf("memory pool", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (diagnostic.IndexOf("Windows error 1450", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     diagnostic.IndexOf("Windows error 1455", StringComparison.OrdinalIgnoreCase) >= 0);
                string detail = commitFailure
                    ? "Windows 无法提交游戏实际申请的内存。请关闭占用内存较大的程序，并把虚拟内存设为“系统管理的大小”或至少 16384 MB，重启 Windows 后再试。\r\n\r\n本次关键日志：\r\n\r\n"
                    : "返回代码 139 表示程序发生了内存访问异常。下面是本次捕获到的崩溃位置和寄存器摘要：\r\n\r\n";
                detail +=
                    (diagnostic.Length == 0 ? "没有捕获到崩溃栈，请查看完整日志。" : diagnostic) +
                    "\r\n\r\n完整日志：" + (logPath ?? "未生成");
                MessageBox.Show(this, detail, "Bloodborne PC 崩溃详情（139）",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            } else if (exitCode == 23) {
                MessageBox.Show(this, "返回代码 23：游戏内容配置或 AppContent 初始化失败。请确认游戏为完整的 v1.09，" +
                    "且 eboot.bin、sce_sys 和 dvdroot_ps4 来自同一份游戏。\r\n\r\n完整日志：" + (logPath ?? "未生成"),
                    "Bloodborne PC 启动失败（23）", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        RefreshSaveManager();
    }

    void Log(string line) { pending.Enqueue(line); }
    void DrainLog() {
        if (pending.IsEmpty) return;
        StringBuilder text = new StringBuilder();
        string line;
        while (pending.TryDequeue(out line)) text.Append(line.Replace("\r", "")).Append("\r\n");
        logBox.AppendText(text.ToString());
    }
    void UpdateButtons() {
        installButton.Enabled = !busy;
        saveButton.Enabled = !busy;
        playButton.Enabled = !busy;
        if (lanStartServerButton != null)
            lanStartServerButton.Enabled = !busy && (shadNetProcess == null || shadNetProcess.HasExited);
        if (lanStopServerButton != null)
            lanStopServerButton.Enabled = !busy && shadNetProcess != null && !shadNetProcess.HasExited;
        if (lanTestButton != null) lanTestButton.Enabled = !busy;
        if (lanRegisterButton != null) lanRegisterButton.Enabled = !busy;
        if (backupButton != null) backupButton.Enabled = !busy;
        if (restoreButton != null) restoreButton.Enabled = !busy && backupList.SelectedItems.Count > 0;
        if (importFolderButton != null) importFolderButton.Enabled = !busy;
        if (importZipButton != null) importZipButton.Enabled = !busy;
        if (exportButton != null) exportButton.Enabled = !busy;
        if (refreshSaveButton != null) refreshSaveButton.Enabled = !busy;
        if (renameCharacterButton != null)
            renameCharacterButton.Enabled = !busy && characterList.SelectedItems.Count > 0;
    }
    void OnClosing(object sender, FormClosingEventArgs e) {
        if (busy && launcherProcess != null &&
            MessageBox.Show(this, "游戏核心仍在运行。关闭窗口不会强制结束游戏；若正在使用内置私网，联机连接会断开。确定关闭吗？", TitleText,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) {
            e.Cancel = true;
            return;
        }
        if (shadNetProcess != null && !shadNetProcess.HasExited) {
            if (MessageBox.Show(this, "本机 shadNet 服务器仍在运行。关闭启动器时同时停止服务器吗？", TitleText,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                StopLanServer();
            else {
                e.Cancel = true;
                return;
            }
        }
        if (embeddedNetProcess != null && !embeddedNetProcess.HasExited) {
            try { embeddedNetProcess.Kill(); embeddedNetProcess.WaitForExit(2000); } catch { }
        }
    }
}

} // namespace BbportPortable
