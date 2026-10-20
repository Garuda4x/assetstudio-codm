using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AssetStudio.Codm;
using AssetStudio.Codm.Wwise;

namespace AssetStudioGUI
{
    /// <summary>
    /// CODM weapon browser: decoded pak names grouped per weapon (skin paks, base weapon, components,
    /// effects, sound banks). Loads the chosen paks into the main window or exports a weapon through
    /// <see cref="CodmExportService"/>, the same code the command line uses.
    /// </summary>
    internal sealed class CodmBrowserForm : Form
    {
        private readonly AssetStudioGUIForm owner;

        private TabControl tabs;
        private TextBox log;
        private ToolStripStatusLabel status;

        // weapons tab
        private TextBox weaponFilter;
        private TreeView weaponTree;
        private Label weaponInfo;
        private ListView assetList;
        private CheckBox optModels, optTextures, optSounds, optWav, optFx, optBase, optComponents, optDeps;
        private ComboBox classCombo;
        private Button btnLoad, btnAdd, btnPreview, btnExport, btnOpenFolder, btnCopy;
        private WeaponEntry current;
        private CharacterEntry currentCharacter;

        // paks tab
        private TextBox pakSearch;
        private ListView pakList;

        // banks tab
        private TextBox bankFilter;
        private ListView bankList;
        private TextBox bankInfo;
        private Button btnExtractWems;

        private bool busy;
        private SplitContainer weaponSplit, bankSplit;
        private string pendingQuery;

        public CodmBrowserForm(AssetStudioGUIForm owner)
        {
            this.owner = owner;
            Text = "CODM weapon browser";
            Width = 1280;
            Height = 860;
            StartPosition = FormStartPosition.CenterParent;
            Icon = owner.Icon;
            BuildUi();
            Shown += (s, e) =>
            {
                weaponSplit.SplitterDistance = 440;
                bankSplit.SplitterDistance = 560;
            };
            Shown += async (s, e) => await LoadIndexAsync(false);
        }

        /// <summary>Select a weapon by id, folder or name once the index is ready.</summary>
        public void SelectWeapon(string query)
        {
            pendingQuery = query;
            if (CodmSession.IsLoaded)
                ApplyPendingQuery();
        }

        private void ApplyPendingQuery()
        {
            if (string.IsNullOrEmpty(pendingQuery))
                return;
            var found = CodmSession.Catalog.Find(pendingQuery);
            pendingQuery = null;
            if (found.Count == 0)
                return;
            tabs.SelectedIndex = 0;
            weaponFilter.Text = found[0].Folder;
            var node = FindNode(weaponTree.Nodes, found[0]);
            if (node != null)
            {
                weaponTree.SelectedNode = node;
                node.EnsureVisible();
            }
        }

        private static TreeNode FindNode(TreeNodeCollection nodes, WeaponEntry w)
        {
            foreach (TreeNode n in nodes)
            {
                if (ReferenceEquals(n.Tag, w))
                    return n;
                var child = FindNode(n.Nodes, w);
                if (child != null)
                    return child;
            }
            return null;
        }

        // ------------------------------------------------------------------ ui construction

        private void BuildUi()
        {
            tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildWeaponsTab());
            tabs.TabPages.Add(BuildPaksTab());
            tabs.TabPages.Add(BuildBanksTab());

            log = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 130,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
                BackColor = SystemColors.Window,
            };

            var statusStrip = new StatusStrip();
            status = new ToolStripStatusLabel("Starting...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(status);

            Controls.Add(tabs);
            Controls.Add(log);
            Controls.Add(statusStrip);
        }

        private TabPage BuildWeaponsTab()
        {
            var page = new TabPage("Weapons");
            var split = weaponSplit = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 200 };

            weaponFilter = new TextBox { Dock = DockStyle.Top };
            weaponFilter.TextChanged += (s, e) => PopulateWeaponTree();
            var filterLabel = new Label { Dock = DockStyle.Top, Text = "Filter by id, name or folder. Bold = base weapon (mesh), children = skins.", AutoSize = false, Height = 18 };
            weaponTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
            weaponTree.AfterSelect += (s, e) =>
            {
                if (e.Node.Tag is CharacterEntry c) ShowCharacter(c);
                else ShowWeapon(e.Node.Tag as WeaponEntry);
            };
            split.Panel1.Controls.Add(weaponTree);
            split.Panel1.Controls.Add(weaponFilter);
            split.Panel1.Controls.Add(filterLabel);

            weaponInfo = new Label { Dock = DockStyle.Top, Height = 44, AutoSize = false, Text = "Select a weapon.", Padding = new Padding(4) };
            assetList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                ShowGroups = true,
                HideSelection = false,
            };
            assetList.Columns.Add("Kind", 110);
            assetList.Columns.Add("Source", 70);
            assetList.Columns.Add("Module", 170);
            assetList.Columns.Add("Size", 70, HorizontalAlignment.Right);
            assetList.Columns.Add("Decoded name / bank", 520);
            assetList.DoubleClick += async (s, e) =>
            {
                var entry = assetList.SelectedItems.Count > 0 ? assetList.SelectedItems[0].Tag as PakEntry : null;
                if (entry != null)
                    await LoadIntoMain(new[] { entry.FullPath }, false);
            };

            var options = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4, 6, 4, 0) };
            optModels = new CheckBox { Text = "Models (cast)", Checked = true, AutoSize = true };
            optTextures = new CheckBox { Text = "Textures", Checked = true, AutoSize = true };
            optSounds = new CheckBox { Text = "Sounds", Checked = true, AutoSize = true };
            optWav = new CheckBox { Text = "wem -> wav", Checked = true, AutoSize = true };
            optFx = new CheckBox { Text = "FX textures", Checked = false, AutoSize = true };
            optBase = new CheckBox { Text = "Include base weapon", Checked = true, AutoSize = true };
            optComponents = new CheckBox { Text = "Include components", Checked = false, AutoSize = true };
            optDeps = new CheckBox { Text = "Resolve dependencies (shared anims, controllers)", Checked = true, AutoSize = true };
            options.Controls.AddRange(new Control[] { optModels, optTextures, optSounds, optWav, optFx, optBase, optComponents, optDeps });

            var classRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4, 4, 4, 0) };
            classRow.Controls.Add(new Label { Text = "Sound class of this family:", AutoSize = true, Padding = new Padding(0, 5, 4, 0) });
            classCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            classCombo.Items.Add("(unknown)");
            foreach (var c in WeaponClassMap.KnownClasses)
                classCombo.Items.Add(c);
            classRow.Controls.Add(classCombo);
            classRow.Controls.Add(MakeButton("Save class", (s, e) => SaveClass()));
            classRow.Controls.Add(new Label { Text = "picks the wpn_<class> bank for base weapon sounds; saved to weapon-classes.json", AutoSize = true, Padding = new Padding(6, 5, 0, 0) });

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
            btnLoad = MakeButton("Load checked (replace)", async (s, e) => await LoadIntoMain(CheckedPaks(), false));
            btnAdd = MakeButton("Add checked", async (s, e) => await LoadIntoMain(CheckedPaks(), true));
            btnPreview = MakeButton("Preview mesh", async (s, e) => await PreviewChecked());
            btnExport = MakeButton("Export weapon...", async (s, e) => await ExportCurrent());
            btnOpenFolder = MakeButton("Show pak in Explorer", (s, e) => ShowInExplorer());
            btnCopy = MakeButton("Copy names", (s, e) => CopyNames());
            buttons.Controls.AddRange(new Control[] { btnLoad, btnAdd, btnPreview, btnExport, btnOpenFolder, btnCopy });

            split.Panel2.Controls.Add(assetList);
            split.Panel2.Controls.Add(classRow);
            split.Panel2.Controls.Add(options);
            split.Panel2.Controls.Add(weaponInfo);
            split.Panel2.Controls.Add(buttons);
            page.Controls.Add(split);
            return page;
        }

        private TabPage BuildPaksTab()
        {
            var page = new TabPage("All paks");
            var top = new Panel { Dock = DockStyle.Top, Height = 30 };
            pakSearch = new TextBox { Dock = DockStyle.Fill };
            pakSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; PopulatePakList(); } };
            var go = new Button { Text = "Search", Dock = DockStyle.Right, Width = 80 };
            go.Click += (s, e) => PopulatePakList();
            top.Controls.Add(pakSearch);
            top.Controls.Add(go);

            pakList = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false };
            pakList.Columns.Add("Source", 70);
            pakList.Columns.Add("Module", 170);
            pakList.Columns.Add("Size", 70, HorizontalAlignment.Right);
            pakList.Columns.Add("Decoded name", 700);
            pakList.Columns.Add("File", 300);
            pakList.DoubleClick += async (s, e) =>
            {
                var entry = pakList.SelectedItems.Count > 0 ? pakList.SelectedItems[0].Tag as PakEntry : null;
                if (entry != null)
                    await LoadIntoMain(new[] { entry.FullPath }, false);
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
            buttons.Controls.Add(MakeButton("Load checked (replace)", async (s, e) => await LoadIntoMain(CheckedPaks(pakList), false)));
            buttons.Controls.Add(MakeButton("Add checked", async (s, e) => await LoadIntoMain(CheckedPaks(pakList), true)));
            buttons.Controls.Add(MakeButton("Show in Explorer", (s, e) =>
            {
                var entry = pakList.SelectedItems.Count > 0 ? pakList.SelectedItems[0].Tag as PakEntry : null;
                if (entry != null) SelectInExplorer(entry.FullPath);
            }));
            var hint = new Label { AutoSize = true, Padding = new Padding(12, 10, 0, 0), Text = "Search matches the decoded path (e.g. cod_effects/particles, avatar/, ui_icon). Enter to search." };
            buttons.Controls.Add(hint);

            page.Controls.Add(pakList);
            page.Controls.Add(top);
            page.Controls.Add(buttons);
            return page;
        }

        private TabPage BuildBanksTab()
        {
            var page = new TabPage("Sound banks");
            var split = bankSplit = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 200 };

            bankFilter = new TextBox { Dock = DockStyle.Top };
            bankFilter.TextChanged += (s, e) => PopulateBankList();
            bankList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            bankList.Columns.Add("Bank", 200);
            bankList.Columns.Add("Copies", 50, HorizontalAlignment.Right);
            bankList.Columns.Add("Embedded", 70, HorizontalAlignment.Right);
            bankList.Columns.Add("Size", 80, HorizontalAlignment.Right);
            bankList.Columns.Add("Languages", 100);
            bankList.SelectedIndexChanged += (s, e) => ShowBank();
            split.Panel1.Controls.Add(bankList);
            split.Panel1.Controls.Add(bankFilter);

            bankInfo = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 8.5f), BackColor = SystemColors.Window };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
            btnExtractWems = MakeButton("Extract wems of selected bank", async (s, e) => await ExtractSelectedBank());
            buttons.Controls.Add(btnExtractWems);
            buttons.Controls.Add(MakeButton("Show in Explorer", (s, e) =>
            {
                var info = bankList.SelectedItems.Count > 0 ? bankList.SelectedItems[0].Tag as WwiseBankInfo : null;
                if (info != null) SelectInExplorer(info.Path);
            }));
            split.Panel2.Controls.Add(bankInfo);
            split.Panel2.Controls.Add(buttons);
            page.Controls.Add(split);
            return page;
        }

        private static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 0, 4, 0) };
            b.Click += onClick;
            return b;
        }

        // ------------------------------------------------------------------ index

        public void ReloadIndex()
        {
            var _ = LoadIndexAsync(true);
        }

        private async Task LoadIndexAsync(bool force)
        {
            SetBusy(true);
            try
            {
                await CodmSession.EnsureLoadedAsync(Log, force);
                PopulateWeaponTree();
                PopulateBankList();
                PopulatePakList();
                var cfg = CodmSession.Config;
                SetStatus($"{CodmSession.Index.Entries.Count} paks, {CodmSession.Catalog.Weapons.Count} weapons, {CodmSession.Wwise.Banks.Count} bank files. Export root: {cfg.ExportRootPath}" +
                          (string.IsNullOrEmpty(cfg.VgmstreamPath) ? "  (vgmstream not configured: wems stay .wem)" : ""));
                if (CodmSession.Index.MissingFolders.Count > 0)
                    Log("Missing pak folders: " + string.Join("; ", CodmSession.Index.MissingFolders));
                ApplyPendingQuery();
                // attachments by container path, in the background (minutes the first time, cached after)
                var _ = Task.Run(() =>
                {
                    try
                    {
                        CodmSession.EnsureContainerIndex(Log);
                        BeginInvoke(new Action(() => { if (current != null) ShowWeapon(current); }));
                    }
                    catch (Exception ex) { Log("component index failed: " + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                Log("Index failed: " + ex.Message);
                SetStatus("Index failed. Check codm.config.json (CODM menu).");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ------------------------------------------------------------------ weapons tab

        private void PopulateWeaponTree()
        {
            var catalog = CodmSession.Catalog;
            if (catalog == null)
                return;
            var filter = weaponFilter.Text.Trim();
            weaponTree.BeginUpdate();
            weaponTree.Nodes.Clear();
            var bold = new Font(weaponTree.Font, FontStyle.Bold);

            foreach (var kindGroup in catalog.Weapons.GroupBy(w => w.Kind).OrderBy(g => g.Key))
            {
                var kindNode = new TreeNode(kindGroup.Key.ToString());
                if (filter.Length > 0)
                {
                    foreach (var w in kindGroup.Where(w => Matches(w, filter)).OrderBy(w => w.Name).ThenBy(w => w.Id))
                        kindNode.Nodes.Add(MakeWeaponNode(w, bold));
                    kindNode.Expand();
                }
                else
                {
                    foreach (var w in kindGroup.Where(w => w.Base == null).OrderBy(w => w.Name).ThenBy(w => w.Id))
                        kindNode.Nodes.Add(MakeWeaponNodeTree(w, bold, 0));
                }
                kindNode.Text += $" ({kindNode.GetNodeCount(true)})";
                if (kindNode.Nodes.Count > 0)
                    weaponTree.Nodes.Add(kindNode);
            }
            var chars = CodmSession.Characters;
            if (chars != null)
            {
                var root = new TreeNode("Characters");
                var groups = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in chars.Characters.Where(c => filter.Length == 0 || c.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var node = new TreeNode(c.Name) { Tag = c, ToolTipText = $"{c.Paks.Count} pak(s)" };
                    if (c.Group != null)
                    {
                        if (!groups.TryGetValue(c.Group, out var g))
                        {
                            g = new TreeNode(c.Group);
                            groups[c.Group] = g;
                            root.Nodes.Add(g);
                        }
                        g.Nodes.Add(node);
                    }
                    else
                        root.Nodes.Add(node);
                }
                root.Text += $" ({root.GetNodeCount(true) - groups.Count})";
                if (root.Nodes.Count > 0)
                {
                    weaponTree.Nodes.Add(root);
                    if (filter.Length > 0) root.Expand();
                }
            }
            if (weaponTree.Nodes.Count > 0 && filter.Length == 0)
                weaponTree.Nodes[0].Expand();
            weaponTree.EndUpdate();
        }

        private void ShowCharacter(CharacterEntry c)
        {
            current = null;
            currentCharacter = c;
            assetList.BeginUpdate();
            assetList.Items.Clear();
            assetList.Groups.Clear();
            weaponInfo.Text = $"{c.Folder}   (character skin{(c.Group != null ? ", " + c.Group : "")})\r\n{c.Paks.Count} pak(s). Export writes every prefab (body, head, hair, first-person arms) as Cast plus textures; no sounds.";
            classCombo.SelectedIndex = 0;
            AddGroup("Character paks", PakIndex.OrderForLoading(c.Paks, CodmSession.Config), true);
            assetList.EndUpdate();
        }

        private async Task PreviewChecked()
        {
            var paths = CheckedPaks();
            if (paths.Length == 0)
            {
                SetStatus("Nothing checked.");
                return;
            }
            SetBusy(true);
            try
            {
                Log($"Loading {paths.Length} pak(s) for preview");
                await owner.LoadPaksFromBrowser(paths, false, optDeps.Checked, Log);
                if (!owner.PreviewBestMesh())
                    Log("No mesh found in the checked paks.");
            }
            catch (Exception ex)
            {
                Log("Preview failed: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static bool Matches(WeaponEntry w, string filter)
        {
            return w.Folder.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                   || (w.Base != null && w.Base.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static TreeNode MakeWeaponNodeTree(WeaponEntry w, Font bold, int depth)
        {
            var node = MakeWeaponNode(w, bold);
            if (depth < 8)
                foreach (var skin in w.Skins.OrderBy(s => s.Name).ThenBy(s => s.Id))
                    node.Nodes.Add(MakeWeaponNodeTree(skin, bold, depth + 1));
            return node;
        }

        private static TreeNode MakeWeaponNode(WeaponEntry w, Font bold)
        {
            var text = w.Folder;
            if (w.Skins.Count > 0)
                text += $"  [{w.Skins.Count} skins]";
            var node = new TreeNode(text) { Tag = w, ToolTipText = $"{w.Paks.Count} pak(s), {w.Effects.Count} fx, {w.Banks.Count} banks" };
            if (w.IsBase || w.Skins.Count > 0)
                node.NodeFont = bold;
            return node;
        }

        private void ShowWeapon(WeaponEntry w)
        {
            current = w;
            currentCharacter = null;
            assetList.BeginUpdate();
            assetList.Items.Clear();
            assetList.Groups.Clear();
            if (w == null)
            {
                weaponInfo.Text = "Select a weapon.";
                assetList.EndUpdate();
                return;
            }

            var info = new StringBuilder();
            info.Append($"{w.Folder}   ({w.Kind}, id {w.Id})");
            if (w.IsBase) info.Append("   BASE weapon");
            if (w.Base != null) info.Append($"   base chain: {string.Join(" -> ", w.Ancestors().Select(a => a.Folder))}");
            if (w.Skins.Count > 0) info.Append($"   skins: {w.Skins.Count}");
            info.AppendLine();
            info.Append($"{w.Paks.Count} pak(s), {w.Components.Count} component(s), {w.Effects.Count} effect pak(s), {w.Banks.Count} own bank(s), {w.SharedBanks.Count} class bank(s), sound class: {w.WeaponClass ?? "unknown"}{(w.WeaponClassSource != null ? " (" + w.WeaponClassSource + ")" : "")}");
            weaponInfo.Text = info.ToString();
            classCombo.SelectedIndex = w.WeaponClass != null ? Math.Max(0, classCombo.Items.IndexOf(w.WeaponClass)) : 0;

            var cfg = CodmSession.Config;
            AddGroup("Weapon paks", PakIndex.OrderForLoading(w.Paks, cfg), true);
            foreach (var a in w.Ancestors())
                AddGroup("Base: " + a.Folder, PakIndex.OrderForLoading(a.Paks, cfg), true);
            AddGroup("Components", PakIndex.OrderForLoading(w.AllComponents(), cfg), false, w);
            AddGroup("Effects", PakIndex.OrderForLoading(w.Effects, cfg), false);
            AddBankGroup("Sound banks", w.Banks);
            AddBankGroup("Class banks (shared media)", w.SharedBanks);
            assetList.EndUpdate();
        }

        private void AddGroup(string name, IEnumerable<PakEntry> entries, bool check, WeaponEntry evidenceOf = null)
        {
            var list = entries.ToList();
            if (list.Count == 0)
                return;
            var group = new ListViewGroup($"{name} ({list.Count})");
            assetList.Groups.Add(group);
            foreach (var e in list)
            {
                var evidence = evidenceOf?.ComponentPaths(e);
                var text = e.Name.Path + (e.Name.Category != null ? "#" + e.Name.Category : "") + (e.Name.Shard >= 0 ? "@" + e.Name.Shard : "");
                if (evidence != null && evidence.Count > 0)
                {
                    var p = evidence[0];
                    var parts = p.Split('/');
                    text += "   <- " + (parts.Length >= 2 ? parts[parts.Length - 2] : p);
                }
                var item = new ListViewItem(new[] { name, e.Source, e.Name.Module ?? "", FormatSize(e.Size), text })
                {
                    Tag = e,
                    Group = group,
                    Checked = check,
                    ToolTipText = evidence != null && evidence.Count > 0 ? string.Join(Environment.NewLine, evidence.Take(12)) : e.FullPath,
                };
                assetList.Items.Add(item);
            }
        }

        private void AddBankGroup(string name, List<WwiseBankInfo> banks)
        {
            if (banks.Count == 0)
                return;
            var group = new ListViewGroup($"{name} ({banks.Count})");
            assetList.Groups.Add(group);
            foreach (var b in banks.OrderBy(b => b.Name))
            {
                var item = new ListViewItem(new[] { name, b.HighQuality ? "HQ" : "", b.Language ?? "", FormatSize(b.Size), $"{b.Name}.bnk  (embedded {b.EmbeddedCount})" })
                {
                    Tag = b,
                    Group = group,
                    ToolTipText = b.Path,
                };
                assetList.Items.Add(item);
            }
        }

        private string[] CheckedPaks()
        {
            return CheckedPaks(assetList);
        }

        private static string[] CheckedPaks(ListView list)
        {
            return list.CheckedItems.Cast<ListViewItem>()
                .Select(i => i.Tag as PakEntry)
                .Where(e => e != null)
                .Select(e => e.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void SaveClass()
        {
            if (current == null)
                return;
            var cls = classCombo.SelectedIndex > 0 ? classCombo.SelectedItem.ToString() : null;
            try
            {
                CodmSession.Catalog.SetClass(current, cls);
                Log($"{current.RootBase.Folder}: sound class {(cls ?? "cleared")} -> {CodmSession.Catalog.ClassMap.Path}");
                ShowWeapon(current);
            }
            catch (Exception ex)
            {
                Log("Saving the class failed: " + ex.Message);
            }
        }

        private async Task LoadIntoMain(string[] paths, bool additive)
        {
            if (paths.Length == 0)
            {
                SetStatus("Nothing checked.");
                return;
            }
            SetBusy(true);
            try
            {
                Log($"{(additive ? "Adding" : "Loading")} {paths.Length} pak(s) into AssetStudio{(optDeps.Checked ? " with dependencies" : "")}");
                await owner.LoadPaksFromBrowser(paths, additive, optDeps.Checked, Log);
                SetStatus($"{paths.Length} pak(s) loaded.");
            }
            catch (Exception ex)
            {
                Log("Load failed: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task ExportCurrent()
        {
            if (current == null && currentCharacter == null)
            {
                SetStatus("Select a weapon or character first.");
                return;
            }
            var cfg = CodmSession.Config;
            var options = new WeaponExportOptions
            {
                Models = optModels.Checked,
                Textures = optTextures.Checked,
                Sounds = optSounds.Checked,
                ConvertWem = optWav.Checked,
                Effects = optFx.Checked,
                IncludeBaseWeapon = optBase.Checked,
                IncludeComponents = optComponents.Checked,
                ResolveDependencies = optDeps.Checked,
            };
            var outRoot = cfg.ExportRootPath;
            using (var dlg = new FolderBrowserDialog { Description = "Export folder (a sub folder named after the weapon is created)", SelectedPath = Directory.Exists(outRoot) ? outRoot : "" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                outRoot = dlg.SelectedPath;
            }

            var weapon = current;
            var character = currentCharacter;
            var label = weapon?.Folder ?? character.Folder;
            SetBusy(true);
            try
            {
                SetStatus($"Exporting {label}...");
                var report = await Task.Run(() => weapon != null
                    ? CodmSession.CreateExportService(Log).ExportWeapon(weapon, outRoot, options, Log)
                    : CodmSession.CreateExportService(Log).ExportCharacter(character, Path.Combine(outRoot, "characters"), options, Log));
                SetStatus($"Exported {label}: {report.Models.Count} models, {report.Textures.Count} textures, {report.DependencyPaks.Count} dependency paks, {report.WemsExtracted} wems ({report.WemsConverted} wav), {report.Warnings.Count} warnings");
                foreach (var warn in report.Warnings)
                    Log("  ! " + warn);
                if (Properties.Settings.Default.openAfterExport)
                    Studio.OpenFolderInExplorer(report.OutputDir);
            }
            catch (Exception ex)
            {
                Log("Export failed: " + ex);
                SetStatus("Export failed: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ShowInExplorer()
        {
            var entry = assetList.SelectedItems.Count > 0 ? assetList.SelectedItems[0].Tag : null;
            var path = (entry as PakEntry)?.FullPath ?? (entry as WwiseBankInfo)?.Path;
            if (path != null)
                SelectInExplorer(path);
        }

        private static void SelectInExplorer(string path)
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private void CopyNames()
        {
            var sb = new StringBuilder();
            var items = assetList.CheckedItems.Count > 0 ? assetList.CheckedItems.Cast<ListViewItem>() : assetList.Items.Cast<ListViewItem>();
            foreach (var item in items)
            {
                if (item.Tag is PakEntry e)
                    sb.AppendLine($"{e.Name.Decoded}\t{e.FullPath}");
                else if (item.Tag is WwiseBankInfo b)
                    sb.AppendLine($"{b.Name}.bnk\t{b.Path}");
            }
            if (sb.Length > 0)
            {
                Clipboard.SetText(sb.ToString());
                SetStatus("Names copied to clipboard.");
            }
        }

        // ------------------------------------------------------------------ paks tab

        private void PopulatePakList()
        {
            var index = CodmSession.Index;
            if (index == null)
                return;
            var text = pakSearch.Text.Trim();
            pakList.BeginUpdate();
            pakList.Items.Clear();
            var hits = (text.Length == 0 ? index.Entries.Take(0) : index.Search(text)).Take(3000).ToList();
            foreach (var e in hits)
            {
                pakList.Items.Add(new ListViewItem(new[] { e.Source, e.Name.Module ?? "", FormatSize(e.Size), e.Name.Path + (e.Name.Category != null ? "#" + e.Name.Category : "") + (e.Name.Shard >= 0 ? "@" + e.Name.Shard : ""), e.FileName })
                {
                    Tag = e,
                    ToolTipText = e.FullPath,
                });
            }
            pakList.EndUpdate();
            if (text.Length > 0)
                SetStatus($"{hits.Count} pak(s) match '{text}'" + (hits.Count >= 3000 ? " (showing the first 3000)" : ""));
        }

        // ------------------------------------------------------------------ banks tab

        private void PopulateBankList()
        {
            var wwise = CodmSession.Wwise;
            if (wwise == null)
                return;
            var text = bankFilter.Text.Trim();
            bankList.BeginUpdate();
            bankList.Items.Clear();
            var groups = wwise.Banks
                .Where(b => text.Length == 0 || b.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var first = wwise.PreferredCopy(g.Key) ?? g.First();
                var langs = string.Join(",", g.Select(b => (b.Language ?? "-") + (b.HighQuality ? "/HQ" : "")).Distinct());
                bankList.Items.Add(new ListViewItem(new[] { g.Key, g.Count().ToString(), first.EmbeddedCount.ToString(), FormatSize(first.Size), langs })
                {
                    Tag = first,
                    ToolTipText = first.Path,
                });
            }
            bankList.EndUpdate();
        }

        private void ShowBank()
        {
            var info = bankList.SelectedItems.Count > 0 ? bankList.SelectedItems[0].Tag as WwiseBankInfo : null;
            if (info == null)
            {
                bankInfo.Text = "";
                return;
            }
            try
            {
                var wwise = CodmSession.Wwise;
                var bank = WwiseBank.Parse(info.Path);
                var sb = new StringBuilder();
                sb.AppendLine($"{bank.Name}.bnk  version {bank.Version}  id {bank.BankId}  {FormatSize(bank.FileSize)}");
                sb.AppendLine(info.Path);
                sb.AppendLine($"objects: {string.Join(", ", bank.ObjectTypeCounts.OrderBy(kv => kv.Key).Select(kv => $"type{kv.Key}={kv.Value}"))}");
                sb.AppendLine($"embedded media: {bank.Embedded.Count}   sounds: {bank.Sounds.Count}   events: {bank.Events.Count}   actions: {bank.Actions.Count}");
                sb.AppendLine();
                var byBank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var unresolved = 0;
                foreach (var s in bank.Sounds)
                {
                    string where;
                    if (wwise.TryResolve(s.SourceId, out var media, out var loose))
                        where = media != null ? Path.GetFileName(media.BankPath) : "loose wem";
                    else { where = "UNRESOLVED"; unresolved++; }
                    byBank[where] = byBank.TryGetValue(where, out var n) ? n + 1 : 1;
                }
                sb.AppendLine("where the sounds live:");
                foreach (var kv in byBank.OrderByDescending(kv => kv.Value))
                    sb.AppendLine($"  {kv.Value,5}  {kv.Key}");
                sb.AppendLine();
                var actionsById = bank.Actions.ToDictionary(a => a.Id);
                var nm = wwise.Names;
                sb.AppendLine("events (name or id, wems reached, actions):");
                foreach (var ev in bank.Events.Take(200))
                {
                    var acts = ev.ActionIds.Select(id => actionsById.TryGetValue(id, out var a) ? $"{a.ActionTypeName}->{(nm?.GetOrId(a.TargetId) ?? a.TargetId.ToString())}" : id.ToString());
                    var srcs = bank.EventSources(ev.Id, out var ext);
                    sb.AppendLine($"  {(nm?.GetOrId(ev.Id) ?? ev.Id.ToString()),-36} {srcs.Count,3} wem(s)  {string.Join("  ", acts)}");
                }
                sb.AppendLine();
                sb.AppendLine("sounds (sound id, wem id, codec, size):");
                foreach (var s in bank.Sounds.OrderBy(s => s.SourceId).Take(500))
                    sb.AppendLine($"  {s.Id,-11} {s.SourceId,-11} {s.Codec,-7} {FormatSize(s.InMemorySize)}");
                bankInfo.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                bankInfo.Text = "Failed to parse bank: " + ex.Message;
            }
        }

        private async Task ExtractSelectedBank()
        {
            var info = bankList.SelectedItems.Count > 0 ? bankList.SelectedItems[0].Tag as WwiseBankInfo : null;
            if (info == null)
            {
                SetStatus("Select a bank first.");
                return;
            }
            var cfg = CodmSession.Config;
            var outDir = Path.Combine(cfg.ExportRootPath, "sounds", info.Name);
            SetBusy(true);
            try
            {
                var r = await Task.Run(() => WemTools.ExportBank(CodmSession.Wwise, info, outDir, cfg.VgmstreamPath, !string.IsNullOrEmpty(cfg.VgmstreamPath), Log));
                SetStatus($"{info.Name}: {r.Extracted}/{r.SourcesTotal} wems extracted, {r.Converted} converted -> {outDir}");
                if (Properties.Settings.Default.openAfterExport)
                    Studio.OpenFolderInExplorer(outDir);
            }
            catch (Exception ex)
            {
                Log("Extract failed: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ------------------------------------------------------------------ helpers

        private void SetBusy(bool value)
        {
            busy = value;
            UseWaitCursor = value;
            foreach (var b in new[] { btnLoad, btnAdd, btnPreview, btnExport, btnExtractWems })
                if (b != null) b.Enabled = !value;
        }

        private void SetStatus(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => status.Text = text)); return; }
            status.Text = text;
        }

        private void Log(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(line))); return; }
            log.AppendText(line + Environment.NewLine);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}
