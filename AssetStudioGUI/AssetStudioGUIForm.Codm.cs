using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AssetStudio;
using AssetStudio.Codm;
using static AssetStudioGUI.Studio;

namespace AssetStudioGUI
{
    /// <summary>CODM menu and the hooks the weapon browser uses to push paks into the main window.</summary>
    partial class AssetStudioGUIForm
    {
        private CodmBrowserForm codmBrowser;
        private ToolStripMenuItem codmDiagnosticsMenuItem;

        private void InitCodmMenu()
        {
            var cfg = CodmSession.LoadConfig();

            var codmMenu = new ToolStripMenuItem("CODM");
            var browser = new ToolStripMenuItem("Weapon browser...", null, (s, e) => ShowCodmBrowser());
            browser.ShortcutKeys = Keys.Control | Keys.W;
            var reload = new ToolStripMenuItem("Reload codm.config.json", null, (s, e) =>
            {
                var c = CodmSession.LoadConfig();
                codmDiagnosticsMenuItem.Checked = c.Diagnostics;
                StatusStripUpdate($"CODM config reloaded from {c.ConfigPath}");
                codmBrowser?.ReloadIndex();
            });
            var openCfg = new ToolStripMenuItem("Open codm.config.json", null, (s, e) =>
            {
                var c = CodmSession.EnsureConfig();
                if (!File.Exists(c.ConfigPath))
                    c.Save();
                Process.Start(new ProcessStartInfo(c.ConfigPath) { UseShellExecute = true });
            });
            var openExports = new ToolStripMenuItem("Open export folder", null, (s, e) =>
            {
                var dir = CodmSession.EnsureConfig().ExportRootPath;
                Directory.CreateDirectory(dir);
                OpenFolderInExplorer(dir);
            });
            codmDiagnosticsMenuItem = new ToolStripMenuItem("Reader diagnostics in log") { CheckOnClick = true, Checked = cfg.Diagnostics };
            codmDiagnosticsMenuItem.CheckedChanged += (s, e) => CodmDiagnostics.Enabled = codmDiagnosticsMenuItem.Checked;

            codmMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                browser,
                new ToolStripSeparator(),
                reload,
                openCfg,
                openExports,
                new ToolStripSeparator(),
                codmDiagnosticsMenuItem,
            });

            var insertAt = menuStrip1.Items.IndexOf(exportToolStripMenuItem);
            menuStrip1.Items.Insert(insertAt >= 0 ? insertAt + 1 : menuStrip1.Items.Count, codmMenu);

            // "AssetStudioGUI.exe --codm [weapon]" opens the browser right away, optionally on a weapon
            var args = Environment.GetCommandLineArgs();
            var flag = Array.FindIndex(args, a => string.Equals(a, "--codm", StringComparison.OrdinalIgnoreCase));
            if (flag >= 0)
            {
                var query = flag + 1 < args.Length && !args[flag + 1].StartsWith("-") ? args[flag + 1] : null;
                Shown += (s, e) => ShowCodmBrowser(query);
            }
        }

        private void ShowCodmBrowser(string query = null)
        {
            if (codmBrowser == null || codmBrowser.IsDisposed)
            {
                codmBrowser = new CodmBrowserForm(this);
                codmBrowser.Show(this);
            }
            else
            {
                codmBrowser.Activate();
            }
            if (query != null)
                codmBrowser.SelectWeapon(query);
        }

        /// <summary>Load pak files into the main window; <paramref name="additive"/> keeps what is already loaded.</summary>
        internal async Task LoadPaksFromBrowser(string[] paths, bool additive, bool resolveDependencies = false, Action<string> log = null)
        {
            if (paths == null || paths.Length == 0)
                return;
            if (!additive)
                ResetForm();
            assetsManager.SpecifyUnityVersion = specifyUnityVersion.Text;
            StatusStripUpdate($"Loading {paths.Length} pak(s)...");
            await Task.Run(() => assetsManager.LoadFiles(paths));
            if (resolveDependencies)
            {
                StatusStripUpdate("Resolving bundle dependencies...");
                await Task.Run(() =>
                {
                    var cabs = CodmSession.EnsureCabIndex(log);
                    DependencyResolver.Resolve(assetsManager, cabs, CodmSession.Config, log);
                });
            }
            if (additive)
                RefreshBrowserAfterLoad();
            await BuildAssetStructures();
            Activate();
        }

        /// <summary>
        /// Select the most interesting mesh of what is loaded (first-person prefab first, then the
        /// biggest) in the Asset List and show it in the Preview tab.
        /// </summary>
        internal bool PreviewBestMesh()
        {
            var meshes = visibleAssets.Select((a, i) => (asset: a, index: i)).Where(x => x.asset.Asset is Mesh).ToList();
            if (meshes.Count == 0)
            {
                StatusStripUpdate("Nothing to preview: no Mesh loaded.");
                return false;
            }
            int Score((AssetItem asset, int index) x)
            {
                var m = (Mesh)x.asset.Asset;
                var name = x.asset.Text ?? "";
                var score = (int)Math.Min(m.m_VertexCount, 1_000_000);
                if (name.IndexOf("_1p", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("1p_", StringComparison.OrdinalIgnoreCase) >= 0) score += 5_000_000;
                if (name.IndexOf("lod0", StringComparison.OrdinalIgnoreCase) >= 0) score += 1_000_000;
                if (name.IndexOf("lod1", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("lod2", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("lod3", StringComparison.OrdinalIgnoreCase) >= 0) score -= 2_000_000;
                if (m.m_Skin != null && m.m_Skin.Length > 0) score += 500_000;
                return score;
            }
            var best = meshes.OrderByDescending(Score).First();
            tabControl1.SelectedIndex = 1; // Asset List
            assetListView.SelectedIndices.Clear();
            assetListView.SelectedIndices.Add(best.index);
            assetListView.EnsureVisible(best.index);
            tabControl2.SelectedIndex = 0; // Preview
            lastSelectedItem = best.asset;
            PreviewAsset(best.asset);
            Activate();
            return true;
        }
    }
}
