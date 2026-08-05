using WinMonitor.Config;
using WinMonitor.Localization;

namespace WinMonitor.UI;

// SettingsForm — Profiles tab: active-profile picker, profile list with
// new/clone/rename/delete, and the name-prompt dialog.
public sealed partial class SettingsForm
{
    // Profiles
    private ComboBox _cboActiveProfile = null!;
    private ListBox _lstProfiles = null!;

    // ================= Profiles tab =================

    private TabPage BuildProfilesTab()
    {
        var page = NewTabPage(Loc.T("set.tab.profiles"));

        var lblActiveProfile = NewLabel(Loc.T("set.prof.active"), 16, 20);
        page.Controls.Add(lblActiveProfile);
        _cboActiveProfile = NewCombo(200, 16, 220);
        _cboActiveProfile.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _cboActiveProfile.SelectedItem is string name)
            {
                if (string.Equals(Config.ActiveProfile, name, StringComparison.Ordinal)) return;
                Config.ActiveProfile = name;
                RefreshProfileScopedTabs();
            }
        };
        SetOptionToolTip("tip.profiles.active", lblActiveProfile, _cboActiveProfile);
        page.Controls.Add(_cboActiveProfile);

        _lstProfiles = new ListBox { Location = new Point(16, 60), Size = new Size(300, 220), IntegralHeight = false };
        ApplyInputTheme(_lstProfiles);
        SetOptionToolTip("tip.profiles.list", _lstProfiles);
        page.Controls.Add(_lstProfiles);

        var btnNew = NewButton(Loc.T("set.prof.add"), 330, 60, 110);
        var btnClone = NewButton(Loc.T("set.prof.clone"), 330, 94, 110);
        var btnRename = NewButton(Loc.T("set.prof.rename"), 330, 128, 110);
        var btnDelete = NewButton(Loc.T("set.prof.delete"), 330, 162, 110);
        btnNew.Click += OnProfileNew;
        btnClone.Click += OnProfileClone;
        btnRename.Click += OnProfileRename;
        btnDelete.Click += OnProfileDelete;
        SetOptionToolTip("tip.profiles.new", btnNew);
        SetOptionToolTip("tip.profiles.clone", btnClone);
        SetOptionToolTip("tip.profiles.rename", btnRename);
        SetOptionToolTip("tip.profiles.delete", btnDelete);
        page.Controls.Add(btnNew);
        page.Controls.Add(btnClone);
        page.Controls.Add(btnRename);
        page.Controls.Add(btnDelete);

        var hint = new Label
        {
            Text = Loc.T("set.prof.hint"),
            Location = new Point(16, 292),
            Size = new Size(650, 40),
            ForeColor = Theme.SubtleText,
        };
        SetOptionToolTip("tip.profiles.list", hint);
        page.Controls.Add(hint);
        return page;
    }

    private Profile? SelectedProfile
    {
        get
        {
            int i = _lstProfiles.SelectedIndex;
            var profiles = Config.Profiles;
            return i >= 0 && i < profiles.Count ? profiles[i] : null;
        }
    }

    private void LoadProfilesTab()
    {
        bool prev = _loading;
        _loading = true;
        try
        {
            int keepSelection = _lstProfiles.SelectedIndex;
            _cboActiveProfile.Items.Clear();
            _lstProfiles.Items.Clear();
            foreach (var p in Config.Profiles)
            {
                _cboActiveProfile.Items.Add(p.Name);
                _lstProfiles.Items.Add(p.Name);
            }
            int active = Config.Profiles.FindIndex(p => p.Name == Config.ActiveProfile);
            if (active < 0 && Config.Profiles.Count > 0) active = 0;
            if (active >= 0) _cboActiveProfile.SelectedIndex = active;
            if (_lstProfiles.Items.Count > 0)
                _lstProfiles.SelectedIndex = Math.Clamp(keepSelection < 0 ? active : keepSelection, 0, _lstProfiles.Items.Count - 1);
        }
        finally { _loading = prev; }
    }

    /// <summary>
    /// The active profile controls tray entries and can override sensor/alert thresholds. Refresh
    /// every profile-scoped editor immediately so its values cannot be mistaken for the old profile.
    /// </summary>
    private void RefreshProfileScopedTabs()
    {
        LoadTrayTab();
        LoadSensorsTab();
        LoadAlertsTab();
        SeedAlertSoundPicker();
    }

    private bool IsValidNewProfileName(string? name, Profile? ignore = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var p in Config.Profiles)
            if (!ReferenceEquals(p, ignore) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private void OnProfileNew(object? sender, EventArgs e)
    {
        string? name = PromptForName(Loc.T("set.prof.add"), "");
        if (!IsValidNewProfileName(name)) return;
        Config.Profiles.Add(Profile.CreateDefault(name!));
        LoadProfilesTab();
        _lstProfiles.SelectedIndex = _lstProfiles.Items.Count - 1;
    }

    private void OnProfileClone(object? sender, EventArgs e)
    {
        var src = SelectedProfile;
        if (src is null) return;
        string? name = PromptForName(Loc.T("set.prof.clone"), src.Name);
        if (!IsValidNewProfileName(name)) return;
        Config.Profiles.Add(src.Clone(name!.Trim()));
        LoadProfilesTab();
        _lstProfiles.SelectedIndex = _lstProfiles.Items.Count - 1;
    }

    private void OnProfileRename(object? sender, EventArgs e)
    {
        var p = SelectedProfile;
        if (p is null) return;
        string? name = PromptForName(Loc.T("set.prof.rename"), p.Name);
        if (name is null) return;
        name = name.Trim();
        if (name.Length == 0 || string.Equals(name, p.Name, StringComparison.Ordinal)) return;
        if (!IsValidNewProfileName(name, ignore: p)) return;
        bool wasActive = Config.ActiveProfile == p.Name;
        p.Name = name;
        if (wasActive) Config.ActiveProfile = name;
        LoadProfilesTab();
    }

    private void OnProfileDelete(object? sender, EventArgs e)
    {
        var p = SelectedProfile;
        if (p is null || Config.Profiles.Count <= 1) return;   // never delete the last profile
        if (MessageBox.Show(this, Loc.F("set.prof.delete_confirm", p.Name), Loc.T("set.prof.delete"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        bool wasActive = Config.ActiveProfile == p.Name;
        Config.Profiles.Remove(p);
        if (wasActive && Config.Profiles.Count > 0)
            Config.ActiveProfile = Config.Profiles[0].Name;
        LoadProfilesTab();
        RefreshProfileScopedTabs();
    }

    private string? PromptForName(string title, string initial)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(340, 108),
            BackColor = Theme.WindowBack,
        };
        form.HandleCreated += (_, _) => Theme.ApplyTitleBar(form);
        var lbl = new Label { Text = Loc.T("set.prof.name_prompt"), Location = new Point(12, 12), AutoSize = true, ForeColor = Theme.Text };
        var txt = new TextBox { Location = new Point(12, 36), Width = 316, Text = initial };
        ApplyInputTheme(txt);
        var ok = new Button { Text = Loc.T("common.ok"), DialogResult = DialogResult.OK, Location = new Point(160, 70), Size = new Size(80, 26) };
        var cancel = new Button { Text = Loc.T("common.cancel"), DialogResult = DialogResult.Cancel, Location = new Point(248, 70), Size = new Size(80, 26) };
        SetOptionToolTip("tip.profiles.name", lbl, txt);
        SetOptionToolTip("tip.common.ok", ok);
        SetOptionToolTip("tip.common.cancel", cancel);
        form.Controls.Add(lbl);
        form.Controls.Add(txt);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK ? txt.Text.Trim() : null;
    }
}
