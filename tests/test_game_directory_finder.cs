// SPDX-License-Identifier: GPL-2.0-or-later
// Compile with BbportPortable.cs and /main:BbportPortable.GameDirectoryFinderTests.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace BbportPortable {
static class GameDirectoryFinderTests {
    static void Require(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }

    static LauncherPages FindPages(Control root) {
        LauncherPages pages = root as LauncherPages;
        if (pages != null) return pages;
        foreach (Control child in root.Controls) {
            pages = FindPages(child);
            if (pages != null) return pages;
        }
        return null;
    }

    static void WriteGame(string directory, string titleId) {
        Directory.CreateDirectory(Path.Combine(directory, "sce_sys"));
        File.WriteAllBytes(Path.Combine(directory, "eboot.bin"), new byte[] { 0x7f, 0x45, 0x4c, 0x46 });
        byte[] sfo = new byte[64];
        sfo[1] = 0x50; sfo[2] = 0x53; sfo[3] = 0x46;
        BitConverter.GetBytes(36).CopyTo(sfo, 8);   // key table offset
        BitConverter.GetBytes(48).CopyTo(sfo, 12);  // value table offset
        BitConverter.GetBytes(1).CopyTo(sfo, 16);   // one entry
        Encoding.ASCII.GetBytes("TITLE_ID\0").CopyTo(sfo, 36);
        Encoding.ASCII.GetBytes(titleId + "\0").CopyTo(sfo, 48);
        File.WriteAllBytes(Path.Combine(directory, "sce_sys", "param.sfo"), sfo);
    }

    [STAThread]
    public static int Main() {
        string root = Path.Combine(Path.GetTempPath(), "bbport-game-finder-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string good = Path.Combine(root, "download", "bloodborne_pc", "Games");
            string second = Path.Combine(root, "other_build", "Games");
            string wrongGame = Path.Combine(root, "download", "another_game");
            string incomplete = Path.Combine(root, "download", "empty_eboot");
            WriteGame(good, "CUSA03173");
            WriteGame(second, "CUSA01363");
            WriteGame(wrongGame, "CUSA99999");
            Directory.CreateDirectory(incomplete);
            File.WriteAllBytes(Path.Combine(incomplete, "eboot.bin"), new byte[1]);
            GameDirectorySearchResult result = GameDirectoryFinder.Search(root);
            Require(result.Candidates.Count == 2, "Only verified Bloodborne games should match.");
            Require(result.Candidates.Any(item => item.Path == good && item.TitleId == "CUSA03173"),
                "The nested Games directory should be found.");
            Require(result.Candidates.Any(item => item.Path == second && item.TitleId == "CUSA01363"),
                "Multiple editions should remain selectable.");
            Require(!result.Incomplete, "Small search should complete without limits.");
            LauncherPalette.Dark = false;
            var light = LauncherPalette.Canvas;
            LauncherPalette.Dark = true;
            Require(LauncherPalette.Canvas != light, "The two themes should use distinct surfaces.");
            Require(LauncherPalette.AccentText != LauncherPalette.Text,
                "Dark accent buttons should have their own readable text colour.");
            ComboBox picker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            picker.Items.AddRange(new object[] { new Choice("亮色", "light"), new Choice("暗色", "dark") });
            picker.SelectedIndex = 0;
            using (LauncherComboHost host = new LauncherComboHost(picker, 38)) {
                host.Width = 360;
                host.RefreshTheme();
                Require(picker.Parent == host && host.Controls.Count == 2,
                    "Modern dropdown must retain the real ComboBox and a separate visual face.");
                Require(host.Controls[0] != picker,
                    "The dark dropdown must cover the native control's bright frame.");
                picker.SelectedIndex = 1;
                Require(((Choice)picker.SelectedItem).Value == "dark",
                    "Modern dropdown must preserve existing selection semantics.");
                Require(picker.Width > 250, "Dropdown should have a full-width click target.");
                using (Bitmap preview = new Bitmap(host.Width, host.Height))
                    host.DrawToBitmap(preview, new Rectangle(0, 0, preview.Width, preview.Height));
                LauncherPalette.Dark = false;
                host.RefreshTheme();
                Require(picker.BackColor == LauncherPalette.Input,
                    "Changing theme should update dropdown and selected text surfaces.");
            }
            using (LauncherPages pages = new LauncherPages()) {
                LauncherPage first = new LauncherPage("first");
                LauncherPage secondPage = new LauncherPage("second");
                pages.AddPage(first);
                pages.AddPage(secondPage);
                int changed = 0;
                pages.SelectedIndexChanged += delegate { changed++; };
                pages.SelectedTab = secondPage;
                Require(pages.SelectedTab == secondPage && changed == 1 &&
                    secondPage.Parent == pages && !first.Visible,
                    "Custom page router should select a page without a native TabControl frame.");
            }
            using (MainForm form = new MainForm(null)) {
                LauncherPages pages = FindPages(form);
                Require(pages != null && pages.TabPages.Count == 13 && pages.SelectedTab != null,
                    "The complete launcher should build all thirteen pages in the borderless router.");
                LauncherPage home = pages.SelectedTab;
                pages.SelectedTab = pages.TabPages[9];
                Require(pages.SelectedTab == pages.TabPages[9],
                    "The launcher sidebar must still be able to switch pages.");
                pages.SelectedTab = home;
            }
            Console.WriteLine("Game directory search, palette, dropdown and page router tests passed.");
            return 0;
        } finally {
            if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("bbport-game-finder-test-"))
                Directory.Delete(root, true);
        }
    }
}
}
