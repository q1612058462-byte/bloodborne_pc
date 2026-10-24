// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BbportPortable {
static class XboxModArchiveTest {
    static void Add(ZipArchive archive, string name, string content) {
        using (Stream stream = archive.CreateEntry(name).Open()) {
            byte[] bytes = Encoding.ASCII.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    static void ExpectFailure(string zip, string mods) {
        bool failed = false;
        try { XboxModArchive.Import(zip, mods, "Xbox 按钮 (Dommo)"); }
        catch (InvalidDataException) { failed = true; }
        if (!failed) throw new Exception("Unsafe or ambiguous archive was accepted.");
        if (Directory.Exists(Path.Combine(mods, "Xbox 按钮 (Dommo)")))
            throw new Exception("Failed import left a visible mod.");
    }

    public static int Main() {
        string root = Path.Combine(Path.GetTempPath(), "bbport-mod-test-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(root);
            string mods = Path.Combine(root, "mods");
            string zip = Path.Combine(root, "good.zip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create)) {
                Add(archive, "wrapper/dvdroot_ps4/menu/common.tpf.dcx", "xbox");
                Add(archive, "wrapper/readme.txt", "author: Dommo");
            }
            XboxModArchive.Import(zip, mods, "Xbox 按钮 (Dommo)");
            string target = Path.Combine(mods, "Xbox 按钮 (Dommo)", "dvdroot_ps4", "menu", "common.tpf.dcx");
            if (File.ReadAllText(target) != "xbox") throw new Exception("Imported wrong data.");
            bool refusesOverwrite = false;
            try { XboxModArchive.Import(zip, mods, "Xbox 按钮 (Dommo)"); }
            catch (IOException) { refusesOverwrite = true; }
            if (!refusesOverwrite) throw new Exception("Existing mod was overwritten.");
            Directory.Delete(Path.Combine(mods, "Xbox 按钮 (Dommo)"), true);
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Update))
                Add(archive, "another/menu/common.tpf.dcx", "conflict");
            ExpectFailure(zip, mods);
            string loose = Path.Combine(root, "common.tpf.dcx");
            File.WriteAllText(loose, "loose");
            XboxModArchive.Import(loose, mods, "Xbox 按钮 (Dommo)");
            if (File.ReadAllText(target) != "loose") throw new Exception("Loose file import failed.");
            Console.WriteLine("Xbox mod archive tests passed.");
            return 0;
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
}
