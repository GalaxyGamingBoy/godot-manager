using Godot;
using Godot.Collections;
using Godot.Sharp.Extras;
using System.Linq;
using System.Threading.Tasks;
using System.IO.Compression;
using Directory = System.IO.Directory;
using Path = System.IO.Path;
using Guid = System.Guid;
using DateTime = System.DateTime;

public class GodotPanel : Panel
{
#region Nodes
    [NodePath("VB/MC/HC/UseMono")]
    CheckBox UseMono = null;

    [NodePath("VB/MC/HC/ActionButtons")]
    ActionButtons ActionButtons = null;

    [NodePath("VB/MC/HC/DownloadSource")]
    OptionButton DownloadSource = null;

    [NodePath("VB/SC/GodotList/Install")]
    CategoryList Installed = null;

    [NodePath("VB/SC/GodotList/Download")]
    CategoryList Downloading = null;

    [NodePath("VB/SC/GodotList/Available")]
    CategoryList Available = null;
#endregion

#region Templates
    PackedScene GodotLE = GD.Load<PackedScene>("res://components/GodotLineEntry.tscn");
#endregion

    Array<Github.Release> Releases = new Array<Github.Release>();

    // Called when the node enters the scene tree for the first time.
    public override void _Ready()
    {
        this.OnReady();
        GetParent<TabContainer>().Connect("tab_changed", this, "OnPageChanged");
        AppDialogs.AddCustomGodot.Connect("added_custom_godot", this, "PopulateList");
        DownloadSource.Clear();
        DownloadSource.AddItem("Github");
        DownloadSource.AddItem("TuxFamily.org");
    }

    [SignalHandler("clicked", nameof(ActionButtons))]
    async void OnActionClicked(int index) {
        switch(index) {
            case 0:     // Add Custom Godot
                AppDialogs.AddCustomGodot.ShowDialog();
                break;
            case 1:     // Scan for Godot
                break;
            case 2:     // Manually Check for Updates for Godot
                await CheckForUpdates();
                break;
        }
    }

    [SignalHandler("toggled", nameof(UseMono))]
    void OnToggledUseMono(bool value) {
        foreach(GodotLineEntry gle in Available.List.GetChildren())
            gle.Mono = value;
        UpdateVisibility();
    }

    async void OnPageChanged(int page) {
        if (GetParent<TabContainer>().GetCurrentTabControl() == this) {
            if (CentralStore.GHVersions.Count == 0) {
                var t = GatherReleases();
                while(!t.IsCompleted) {
                    await this.IdleFrame();
                }
            } else {
                if (CentralStore.Settings.CheckForUpdates &&
                    (DateTime.UtcNow - CentralStore.Settings.LastCheck) >= CentralStore.Settings.CheckInterval)
				{
					await CheckForUpdates();
				}
			}
            var task = PopulateList();
        }
    }

	public async Task CheckForUpdates()
	{
        AppDialogs.BusyDialog.UpdateHeader(Tr("Grabbing information from Github"));
        AppDialogs.BusyDialog.UpdateByline(Tr("Getting the latest version information from Github for Godot Engine..."));
        AppDialogs.BusyDialog.ShowDialog();

		var tres = Github.Github.Instance.GetLatestRelease();
		while (!tres.IsCompleted)
		{
			await this.IdleFrame();
		}
		var gv = GithubVersion.FromAPI(tres.Result);
		var l = from version in CentralStore.GHVersions
				where version.Name == gv.Name
				select gv;
		var c = l.FirstOrDefault<GithubVersion>();
		if (c == null)
		{
			CentralStore.GHVersions.Clear();
			var t = GatherReleases();
			while (!t.IsCompleted)
			{
				await this.IdleFrame();
			}
			//AppDialogs.NewVersion.UpdateReleaseInfo(tres.Result);
			//AppDialogs.NewVersion.Visible = true;
            await PopulateList();
            AppDialogs.NewVersion.Connect("download_update", this, "OnDownloadUpdate");
            AppDialogs.NewVersion.ShowDialog(tres.Result);
		}

        AppDialogs.BusyDialog.HideDialog();
        CentralStore.Settings.LastCheck = DateTime.UtcNow;
        CentralStore.Instance.SaveDatabase();
	}

    async void OnDownloadUpdate(Github.Release release, bool useMono) {
        AppDialogs.NewVersion.Disconnect("download_update", this, "OnDownloadUpdate");
        foreach (GodotLineEntry gle in Available.List.GetChildren()) {
            if (gle.GithubVersion.Name == release.Name) {
                gle.Mono = useMono;
                await OnInstallClicked(gle);
                break;
            }
        }
    }

	async Task OnInstallClicked(GodotLineEntry gle) {
        Available.List.RemoveChild(gle);
        Downloading.List.AddChild(gle);
        Downloading.Visible = true;
        gle.ToggleDownloadProgress(true);
        Task blah = gle.StartDownload();
        while (!blah.IsCompleted)
            await this.IdleFrame();
        Downloading.List.RemoveChild(gle);
        
        if (Downloading.List.GetChildCount() == 0)
            Downloading.Visible = false;
        
        CentralStore.Versions.Add(gle.CreateGodotVersion());
        if (CentralStore.Versions.Count == 1) {
            CentralStore.Settings.DefaultEngine = CentralStore.Versions[0].Id;
        }
        CentralStore.Instance.SaveDatabase();
        var task = PopulateList();
        await task;
    }

    public Array<string> RecursiveListDir(string path) {
        Array<string> list = new Array<string>();
        foreach(string dir in Directory.EnumerateDirectories(path)) {
            foreach(string file in RecursiveListDir(Path.Combine(path,dir).NormalizePath())) {
                list.Add(file);
            }
            list.Add(Path.Combine(path,dir).NormalizePath());
        }

        foreach(string file in Directory.EnumerateFiles(path)) {
            list.Add(file.NormalizePath());
        }
        
        list.Add(path.NormalizePath());
        
        return list;
    }


    async void OnUninstallClicked(GodotLineEntry gle) {
        Task<bool> result;
        if (gle.Source == gle.GodotVersion.Location) {
            result = AppDialogs.YesNoDialog.ShowDialog(
                Tr("Remove Godot Install"),
                string.Format(Tr("You are about to remove the reference to {0}, are you sure you want to continue?"),gle.GodotVersion.Tag)
            );
        } else {
            result = AppDialogs.YesNoDialog.ShowDialog(
                Tr("Remove Godot Install"),
                string.Format(Tr("You are about to uninstall {0}, are you sure you want to continue?"),gle.GodotVersion.Tag)
            );
        }
        while (!result.IsCompleted)
            await this.IdleFrame();

        Task task = null;

        if (result.Result) {
            if (gle.GodotVersion.GithubVersion == null && gle.GodotVersion.TuxfamilyVersion == null) {
                // Custom Godot added Locally, do not remove files, only remove entry.
                foreach (ProjectFile pf in CentralStore.Projects) {
                    if (pf.GodotVersion == gle.GodotVersion.Id) {
                        pf.GodotVersion = Guid.Empty.ToString();
                    }
                }

                if ((string)CentralStore.Settings.DefaultEngine == gle.GodotVersion.Id)
                    CentralStore.Settings.DefaultEngine = Guid.Empty.ToString();
                
                CentralStore.Versions.Remove(gle.GodotVersion);
                CentralStore.Instance.SaveDatabase();
                task = PopulateList();
                await task;
                return;
            }
            Godot.Directory dir = new Godot.Directory();
            var install = ProjectSettings.GlobalizePath(gle.GodotVersion.Location);
            var cache = ProjectSettings.GlobalizePath(gle.GodotVersion.CacheLocation);
            var files = RecursiveListDir(install);

            foreach (string file in files) {
                dir.Remove(file);
            }
            dir.Remove(cache);

            foreach (ProjectFile pf in CentralStore.Projects) {
                if (pf.GodotVersion == gle.GodotVersion.Id) {
                    pf.GodotVersion = Guid.Empty.ToString();
                }
            }

            if ((string)CentralStore.Settings.DefaultEngine == gle.GodotVersion.Id)
                CentralStore.Settings.DefaultEngine = Guid.Empty.ToString();  // Should Prompt to change
            
            CentralStore.Versions.Remove(gle.GodotVersion);
            CentralStore.Instance.SaveDatabase();
            task = PopulateList();
            await task;
        } else {
            gle.ToggleDownloadUninstall(true);
        }
    }

    void OnDefaultSelected(GodotLineEntry gle) {
        if (gle.GodotVersion.Id == CentralStore.Settings.DefaultEngine) {
            return; // Don't need to do anything
        } else {
            foreach(GodotLineEntry igle in Installed.List.GetChildren()) {
                if (igle.IsDefault)
                    igle.ToggleDefault(false);
            }
            CentralStore.Settings.DefaultEngine = gle.GodotVersion.Id;
            CentralStore.Instance.SaveDatabase();
            gle.ToggleDefault(true);
        }
    }

    public async Task PopulateList() {
        foreach (Node child in Installed.List.GetChildren())
            child.QueueFree();
        foreach (Node child in Available.List.GetChildren())
            child.QueueFree();

        await this.IdleFrame();

        foreach(GithubVersion gv in CentralStore.GHVersions) {
            GodotLineEntry gle = GodotLE.Instance<GodotLineEntry>();
            gle.GithubVersion = gv;
            gle.Mono = UseMono.Pressed;
            Available.List.AddChild(gle);
            gle.Connect("install_clicked", this, "OnInstallClicked");
        }

        foreach(GodotVersion gdv in CentralStore.Versions) {
            GodotLineEntry gle = GodotLE.Instance<GodotLineEntry>();
            gle.GodotVersion = gdv;
            gle.GithubVersion = gdv.GithubVersion;
            gle.Mono = gdv.IsMono;
            gle.Downloaded = true;
            gle.ToggleDefault(CentralStore.Settings.DefaultEngine == gdv.Id);
            Installed.List.AddChild(gle);
            gle.Connect("uninstall_clicked", this, "OnUninstallClicked");
            gle.Connect("default_selected", this, "OnDefaultSelected");
        }

        UpdateVisibility();
    }

    private void UpdateVisibility() {
        Array<string> gdName = new Array<string>();
        foreach (GodotLineEntry igle in Installed.List.GetChildren()) {
            gdName.Add(igle.Label);
        }

        foreach(GodotLineEntry agle in Available.List.GetChildren()) {
            if (gdName.IndexOf(agle.Label) != -1)
                agle.Visible = false;
            else
                agle.Visible = true;
        }
    }

	public async Task GetReleases() {
        Mutex mutex = new Mutex();
        bool stop = false;
        int page = 1;
        while (stop == false) {
            var tres = Github.Github.Instance.GetReleases(30,page);
            while (!tres.IsCompleted) {
                await this.IdleFrame();
            }

            mutex.Lock();
            if (tres.Result == null) {
                OS.Alert(Tr("Failed to get Release information from Github"), Tr("Github Connection Error"));
                stop = true;
            }
            else {
                Array<Github.Release> releases = tres.Result;
                if (releases.Count > 0) {
                    foreach(Github.Release release in releases) {
                        Releases.Add(release);
                    }
                }
                else
                    stop = true;
            }
            page++;
            mutex.Unlock();
        }
    }

    private int downloadedBytes = 0;
    void OnChunkReceived(int bytes) {
        downloadedBytes += bytes;
        AppDialogs.BusyDialog.UpdateByline(string.Format(Tr("Downloaded {0}..."),Util.FormatSize(downloadedBytes)));
    }

    public async Task GatherReleases() {
        AppDialogs.BusyDialog.UpdateHeader(Tr("Fetching Releases from Github..."));
        AppDialogs.BusyDialog.UpdateByline(Tr("Connecting..."));
        AppDialogs.BusyDialog.ShowDialog();
        downloadedBytes = 0;
        Github.Github.Instance.Connect("chunk_received", this, "OnChunkReceived");
        var task = GetReleases();
        while(!task.IsCompleted) {
            await this.IdleFrame();
        }

        Github.Github.Instance.Disconnect("chunk_received", this, "OnChunkReceived");
        
        AppDialogs.BusyDialog.UpdateHeader(Tr("Processing Release Information from Github..."));
        AppDialogs.BusyDialog.UpdateByline(string.Format(Tr("Processing {0}/{1}"),0,Releases.Count));
        int i = 0;
        foreach(Github.Release release in Releases) {
            i++;
            AppDialogs.BusyDialog.UpdateByline(string.Format(Tr("Processing {0}/{1}"),i,Releases.Count));
            GithubVersion gv = GithubVersion.FromAPI(release);
            CentralStore.GHVersions.Add(gv);
            await this.IdleFrame();
        }
        CentralStore.Instance.SaveDatabase();

        AppDialogs.BusyDialog.HideDialog();
    }
}