using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RimLiaison.Observability;

namespace RimLiaison.Desktop;

public sealed class ObservabilityMainForm : Form
{
    private readonly IAgentObservabilityStore store;
    private readonly bool ownsStore;
    private readonly AgentObservabilityUi observabilityUi;
    private readonly IDisposable uiSubscription;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly FlowLayoutPanel navigationPanel;
    private readonly Label viewTitle;
    private readonly Label streamStatus;
    private readonly Panel contentPanel;
    private readonly Panel allPanel;
    private readonly Panel issuesPanel;
    private readonly Panel agentPanel;
    private readonly ListView allActivity;
    private readonly ListView agentActivity;
    private readonly ListView issueList;
    private readonly TextBox allDetails;
    private readonly TextBox agentDetails;
    private readonly TextBox issueDetails;
    private readonly Label allAgentSummary;
    private readonly Label agentHeader;
    private readonly FlowLayoutPanel agentProgress;
    private readonly TextBox agentEvidence;
    private readonly TextBox agentResults;
    private readonly Button viewActivityButton;
    private readonly Button issueDetailsButton;
    private readonly Button prepareAssessmentButton;
    private readonly Button copyBundleButton;
    private readonly Button exportBundleButton;
    private TabControl agentDetailTabs = null!;
    private string? bundleJson;
    private string? bundleStatusMessage;
    private string? renderedNavigationSignature;
    private bool suppressIssueSelection;
    private bool suppressActivitySelection;
    private bool suppressDetailTabSelection;
    private int disposed;

    public ObservabilityMainForm(IAgentObservabilityStore? store = null)
    {
        this.store = store ?? AgentObservabilityStore.CreateDefault();
        ownsStore = store is null;
        observabilityUi = new AgentObservabilityUi(
            this.store,
            new AgentObservabilityUiOptions
            {
                MaximumActivityRows = 1_000,
                MaximumIssueRows = 500,
                MaximumRecentActivityRows = 100,
                MaximumSupportingEvents = 2_000
            });
        uiSubscription = observabilityUi.Subscribe(OnObservabilityUpdate);
        refreshTimer = new System.Windows.Forms.Timer { Interval = 250 };
        refreshTimer.Tick += OnRefreshTimerTick;
        refreshTimer.Start();

        Text = "RimLiaison";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 420);
        ClientSize = new Size(1_160, 760);
        AutoScaleMode = AutoScaleMode.Font;

        navigationPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(8, 8, 8, 6),
            AutoScroll = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.FromArgb(245, 246, 248)
        };
        viewTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(12, 8, 8, 4),
            Font = new Font(Font, FontStyle.Bold),
            Text = "All"
        };
        streamStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(10, 5, 8, 4),
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        contentPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 4) };

        allActivity = CreateListView(
            ("Time", 84),
            ("Mod", 180),
            ("Stage", 112),
            ("Activity", 520),
            ("Status", 100));
        allActivity.SelectedIndexChanged += OnAllActivitySelected;
        allDetails = CreateDetailsBox();
        allAgentSummary = new Label
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(4, 4, 4, 4),
            AutoEllipsis = true,
            ForeColor = Color.DimGray
        };
        allPanel = BuildAllPanel();

        issueList = CreateListView(
            ("State", 92),
            ("Severity", 92),
            ("Mod", 180),
            ("Category", 150),
            ("Summary", 510),
            ("Issue", 220));
        issueList.CheckBoxes = true;
        issueList.MultiSelect = false;
        issueList.ItemChecked += OnIssueChecked;
        issueList.SelectedIndexChanged += OnIssueSelected;
        issueDetails = CreateDetailsBox();
        viewActivityButton = CreateButton("View activity", OnViewIssueActivity);
        issueDetailsButton = CreateButton("Details", OnViewIssueDetails);
        prepareAssessmentButton = CreateButton("Preview assessment", OnPrepareAssessment);
        copyBundleButton = CreateButton("Copy diagnostic bundle", OnCopyBundle);
        exportBundleButton = CreateButton("Export diagnostic bundle", OnExportBundle);
        copyBundleButton.Enabled = false;
        exportBundleButton.Enabled = false;
        issuesPanel = BuildIssuesPanel();

        agentHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(4, 5, 4, 4),
            Font = new Font(Font, FontStyle.Bold),
            AutoEllipsis = true
        };
        agentProgress = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(4, 4, 4, 4),
            AutoScroll = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        agentActivity = CreateListView(
            ("Time", 84),
            ("Stage", 112),
            ("Activity", 560),
            ("Issues", 90));
        agentActivity.SelectedIndexChanged += OnAgentActivitySelected;
        agentDetails = CreateDetailsBox();
        agentEvidence = CreateDetailsBox();
        agentResults = CreateDetailsBox();
        agentPanel = BuildAgentPanel();

        contentPanel.Controls.Add(agentPanel);
        contentPanel.Controls.Add(issuesPanel);
        contentPanel.Controls.Add(allPanel);
        Controls.Add(contentPanel);
        Controls.Add(streamStatus);
        Controls.Add(viewTitle);
        Controls.Add(navigationPanel);
        FormClosed += OnFormClosed;

        RefreshFromSnapshot(observabilityUi.Snapshot);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
            uiSubscription.Dispose();
            observabilityUi.Dispose();
            if (ownsStore && store is IDisposable disposableStore)
            {
                disposableStore.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private Panel BuildAllPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 410,
            FixedPanel = FixedPanel.None
        };
        split.Panel1.Controls.Add(allActivity);
        split.Panel2.Controls.Add(allDetails);
        panel.Controls.Add(split);
        panel.Controls.Add(allAgentSummary);
        return panel;
    }

    private Panel BuildIssuesPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320,
            FixedPanel = FixedPanel.None
        };
        split.Panel1.Controls.Add(issueList);
        split.Panel2.Controls.Add(issueDetails);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(0, 4, 0, 4),
            WrapContents = false
        };
        actions.Controls.Add(viewActivityButton);
        actions.Controls.Add(issueDetailsButton);
        actions.Controls.Add(prepareAssessmentButton);
        actions.Controls.Add(copyBundleButton);
        actions.Controls.Add(exportBundleButton);
        panel.Controls.Add(split);
        panel.Controls.Add(actions);
        return panel;
    }

    private Panel BuildAgentPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 360,
            FixedPanel = FixedPanel.None
        };
        split.Panel1.Controls.Add(agentActivity);
        agentDetailTabs = new TabControl { Dock = DockStyle.Fill };
        agentDetailTabs.TabPages.Add(CreateTab("Event details", agentDetails));
        agentDetailTabs.TabPages.Add(CreateTab("Files / tools / commands", agentEvidence));
        agentDetailTabs.TabPages.Add(CreateTab("Build / test / issues", agentResults));
        agentDetailTabs.SelectedIndexChanged += OnAgentDetailTabChanged;
        split.Panel2.Controls.Add(agentDetailTabs);
        panel.Controls.Add(split);
        panel.Controls.Add(agentProgress);
        panel.Controls.Add(agentHeader);
        return panel;
    }

    private static TabPage CreateTab(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private static ListView CreateListView(params (string Name, int Width)[] columns)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false,
            UseCompatibleStateImageBehavior = false
        };
        foreach ((string name, int width) in columns)
        {
            list.Columns.Add(name, width);
        }

        return list;
    }

    private static TextBox CreateDetailsBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BackColor = SystemColors.Window,
        Font = new Font("Consolas", 9F)
    };

    private static Button CreateButton(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 0, 6, 0)
        };
        button.Click += handler;
        return button;
    }

    private void OnObservabilityUpdate(AgentObservabilityUiUpdate _)
    {
        if (Volatile.Read(ref disposed) != 0 || IsDisposed)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => OnObservabilityUpdate(_)));
                return;
            }

            refreshTimer.Start();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        refreshTimer.Stop();
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        RefreshFromSnapshot(observabilityUi.Snapshot);
        refreshTimer.Start();
    }

    private void RefreshFromSnapshot(AgentObservabilityUiSnapshot snapshot)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        RefreshNavigation(snapshot);
        string liveStatus = snapshot.Stream.Delayed
            ? "Live stream delayed" + (snapshot.Stream.Message is null ? string.Empty : ": " + snapshot.Stream.Message)
            : $"Live · revision {snapshot.Stream.Revision} · sequence {snapshot.Stream.LatestSequence?.ToString() ?? "—"}";
        streamStatus.Text = bundleStatusMessage ?? liveStatus;

        allPanel.Visible = snapshot.View == AgentObservabilityUiView.All;
        issuesPanel.Visible = snapshot.View is AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue;
        agentPanel.Visible = snapshot.View == AgentObservabilityUiView.Agent;
        viewTitle.Text = snapshot.View switch
        {
            AgentObservabilityUiView.All => "All",
            AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue => "Issues",
            AgentObservabilityUiView.Agent => snapshot.Agent?.Agent.ModName ?? "Agent",
            _ => "RimLiaison"
        };

        if (snapshot.All is not null)
        {
            RefreshAll(snapshot.All);
        }

        if (snapshot.Issues is not null)
        {
            RefreshIssues(snapshot.Issues, snapshot.Issue, snapshot);
        }

        if (snapshot.Agent is not null)
        {
            RefreshAgent(snapshot.Agent, snapshot);
        }
    }

    private void RefreshNavigation(AgentObservabilityUiSnapshot snapshot)
    {
        string navigationSignature = string.Join(
            '\u001E',
            snapshot.Navigation.Items.Select(item => string.Join(
                '\u001F',
                item.Key,
                item.Label,
                item.FullLabel,
                item.Kind,
                item.AgentId,
                item.RunId,
                item.Selected,
                item.CanDismiss)));
        if (string.Equals(renderedNavigationSignature, navigationSignature, StringComparison.Ordinal))
        {
            return;
        }

        renderedNavigationSignature = navigationSignature;
        string? selectedKey = snapshot.Navigation.Items
            .FirstOrDefault(item => item.Selected)?.Key;
        navigationPanel.SuspendLayout();
        try
        {
            navigationPanel.Controls.Clear();
            foreach (AgentObservabilityUiNavigationItem item in snapshot.Navigation.Items)
            {
                int fullWidth = Math.Min(220, Math.Max(82, item.Label.Length * 9 + 28));
                var container = new Panel
                {
                    Width = fullWidth + (item.CanDismiss ? 24 : 0),
                    Height = 30,
                    Margin = new Padding(0, 0, 6, 0)
                };
                var button = new Button
                {
                    Text = item.Label,
                    Tag = item,
                    AutoSize = false,
                    Width = fullWidth,
                    Height = 30,
                    Margin = Padding.Empty,
                    FlatStyle = FlatStyle.System,
                    AccessibleName = item.FullLabel,
                    UseMnemonic = false
                };
                toolTip.SetToolTip(button, item.FullLabel);
                button.BackColor = item.Key == selectedKey
                    ? SystemColors.Highlight
                    : SystemColors.Control;
                button.ForeColor = item.Key == selectedKey
                    ? SystemColors.HighlightText
                    : SystemColors.ControlText;
                button.Click += OnNavigationClick;
                container.Controls.Add(button);
                if (item.CanDismiss && item.AgentId is not null)
                {
                    var dismiss = new Button
                    {
                        Text = "×",
                        Tag = item,
                        AutoSize = false,
                        Width = 24,
                        Height = 30,
                        Location = new Point(fullWidth, 0),
                        FlatStyle = FlatStyle.System,
                        AccessibleName = "Dismiss " + item.FullLabel,
                        UseMnemonic = false
                    };
                    toolTip.SetToolTip(dismiss, "Dismiss finished agent");
                    dismiss.Click += OnDismissAgentClick;
                    container.Controls.Add(dismiss);
                }
                navigationPanel.Controls.Add(container);
            }
        }
        finally
        {
            navigationPanel.ResumeLayout();
        }
    }

    private readonly ToolTip toolTip = new();

    private void OnNavigationClick(object? sender, EventArgs e)
    {
        if (sender is not Button { Tag: AgentObservabilityUiNavigationItem item })
        {
            return;
        }

        switch (item.Kind)
        {
            case "all":
                observabilityUi.ShowAll();
                break;
            case "issues":
                observabilityUi.ShowIssues();
                break;
            case "agent" when item.AgentId is not null:
                observabilityUi.ShowAgent(item.AgentId, item.RunId);
                break;
        }

        RefreshFromSnapshot(observabilityUi.Snapshot);
    }

    private void OnDismissAgentClick(object? sender, EventArgs e)
    {
        if (sender is not Button { Tag: AgentObservabilityUiNavigationItem item } ||
            item.AgentId is null)
        {
            return;
        }

        observabilityUi.DismissAgent(item.AgentId, item.RunId);
        RefreshFromSnapshot(observabilityUi.Snapshot);
    }

    private void RefreshAll(AgentObservabilityAllView view)
    {
        allAgentSummary.Text = view.Agents.Count == 0
            ? view.EmptyState ?? "No agents"
            : string.Join(
                "   ",
                view.Agents.Select(agent =>
                    $"{agent.ModName}: {StatusText(agent.Status)}"));
        RefreshActivityList(allActivity, view.Activity, includeMod: true);
        if (allActivity.SelectedItems.Count == 0)
        {
            allDetails.Text = view.EmptyState ?? "Select an activity row to inspect bounded details.";
        }
    }

    private void RefreshIssues(
        AgentObservabilityIssuesView view,
        AgentObservabilityIssueDetail? detail,
        AgentObservabilityUiSnapshot snapshot)
    {
        string? selectedIssue = snapshot.SelectedIssueId;
        suppressIssueSelection = true;
        int topIndex = TopIndex(issueList);
        issueList.BeginUpdate();
        try
        {
            issueList.Items.Clear();
            foreach (AgentObservabilityIssueRow row in view.Issues)
            {
                var item = new ListViewItem(row.StateLabel);
                item.SubItems.Add(row.Issue.Severity.ToString());
                item.SubItems.Add(row.ModName);
                item.SubItems.Add(row.Issue.Category.ToString());
                item.SubItems.Add(row.Issue.Summary);
                item.SubItems.Add(row.Issue.Id);
                item.Tag = row.Issue;
                if (row.Issue.Recovered)
                {
                    item.ForeColor = Color.DarkGreen;
                }
                else if (row.Issue.Severity == AgentIssueSeverity.Error)
                {
                    item.ForeColor = Color.DarkRed;
                }

                issueList.Items.Add(item);
                item.Checked = view.SelectedIssueIds.Contains(row.Issue.Id, StringComparer.Ordinal);
                if (row.Issue.Id == selectedIssue)
                {
                    item.Selected = true;
                }
            }
        }
        finally
        {
            issueList.EndUpdate();
            suppressIssueSelection = false;
        }

        RestoreTopIndex(issueList, topIndex);
        RefreshIssueDetails(view, detail, snapshot);
    }

    private void RefreshIssueDetails(
        AgentObservabilityIssuesView view,
        AgentObservabilityIssueDetail? detail,
        AgentObservabilityUiSnapshot snapshot)
    {
        if (snapshot.IssueMode == AgentObservabilityIssueMode.Assessment &&
            snapshot.Assessment is not null)
        {
            bundleJson = JsonSerializer.Serialize(
                snapshot.Assessment,
                new JsonSerializerOptions(AgentObservabilityJson.Options)
                {
                    WriteIndented = true
                });
            issueDetails.Text = bundleJson;
            bundleStatusMessage = FormatBundleStatus(snapshot.Assessment);
            copyBundleButton.Enabled = true;
            exportBundleButton.Enabled = true;
        }
        else if (detail is not null)
        {
            bundleJson = null;
            issueDetails.Text = FormatIssueDetail(detail);
            bool hasCheckedIssues = snapshot.SelectedIssueIds.Count > 0;
            copyBundleButton.Enabled = hasCheckedIssues;
            exportBundleButton.Enabled = hasCheckedIssues;
        }
        else
        {
            bundleJson = null;
            bool hasCheckedIssues = snapshot.SelectedIssueIds.Count > 0;
            copyBundleButton.Enabled = hasCheckedIssues;
            exportBundleButton.Enabled = hasCheckedIssues;
            issueDetails.Text = view.EmptyState ?? "Select an issue to inspect supporting evidence.";
        }
    }

    private void RefreshAgent(
        AgentObservabilityAgentView view,
        AgentObservabilityUiSnapshot snapshot)
    {
        AgentSnapshot agent = view.Agent;
        agentHeader.Text =
            $"{agent.ModName}   ·   {StatusText(agent.Status)}   ·   {agent.CurrentStage}   ·   " +
            $"{view.ElapsedMilliseconds / 1000.0:0.0}s   ·   {agent.CurrentActivity ?? "—"}";
        agentProgress.SuspendLayout();
        try
        {
            agentProgress.Controls.Clear();
            foreach (AgentObservabilityStageProgress stage in view.StageProgress)
            {
                var label = new Label
                {
                    AutoSize = true,
                    Text = StageGlyph(stage.State) + " " + stage.Stage,
                    Padding = new Padding(5, 4, 5, 3),
                    Margin = new Padding(0, 0, 4, 0),
                    BorderStyle = BorderStyle.FixedSingle,
                    ForeColor = stage.State is "failed" ? Color.DarkRed : Color.Black,
                    BackColor = stage.IsCurrent ? Color.LightGoldenrodYellow : Color.WhiteSmoke
                };
                agentProgress.Controls.Add(label);
            }
        }
        finally
        {
            agentProgress.ResumeLayout();
        }

        suppressActivitySelection = true;
        try
        {
            RefreshActivityList(
                agentActivity,
                view.RecentActivity,
                includeMod: false,
                selectedEventId: view.SelectedEventId);
        }
        finally
        {
            suppressActivitySelection = false;
        }

        if (view.SelectedEvent is not null)
        {
            agentDetails.Text = FormatEventDetail(view.SelectedEvent);
            agentEvidence.Text = FormatEventEvidence(view.SelectedEvent);
            agentResults.Text = FormatEventResults(view.SelectedEvent);
        }
        else
        {
            agentDetails.Text = view.EmptyState ?? "Select an activity row to inspect bounded details.";
            agentEvidence.Text = "Select an activity row to inspect files, tools, and commands.";
            agentResults.Text = "Select an activity row to inspect build, test, and issue data.";
        }

        suppressDetailTabSelection = true;
        try
        {
            agentDetailTabs.SelectedIndex = snapshot.AgentDetailTab switch
            {
                AgentObservabilityAgentDetailTab.Artifacts => 1,
                AgentObservabilityAgentDetailTab.BuildTestIssues => 2,
                _ => 0
            };
        }
        finally
        {
            suppressDetailTabSelection = false;
        }
    }

    private static ListViewItem ActivityItem(
        AgentObservabilityActivityRow row,
        bool includeMod)
    {
        string time = DateTimeOffset.FromUnixTimeMilliseconds(row.Event.Timestamp)
            .ToLocalTime()
            .ToString("HH:mm:ss");
        var item = new ListViewItem(time);
        if (includeMod)
        {
            item.SubItems.Add(row.ModName);
        }

        item.SubItems.Add(row.Event.Stage.ToString());
        item.SubItems.Add(row.Event.Summary);
        if (includeMod)
        {
            item.SubItems.Add(row.AgentStatus is null ? string.Empty : StatusText(row.AgentStatus.Value));
        }

        if (row.HasIssue)
        {
            item.BackColor = Color.MistyRose;
        }

        return item;
    }

    private static void RefreshActivityList(
        ListView list,
        IReadOnlyList<AgentObservabilityActivityRow> rows,
        bool includeMod,
        string? selectedEventId = null)
    {
        int topIndex = TopIndex(list);
        string? selected = selectedEventId ?? SelectedTag(list);
        HashSet<string> desiredIds = rows
            .Select(static row => row.Event.Id)
            .ToHashSet(StringComparer.Ordinal);

        list.BeginUpdate();
        try
        {
            for (int index = list.Items.Count - 1; index >= 0; index--)
            {
                if (list.Items[index].Tag is not string eventId || !desiredIds.Contains(eventId))
                {
                    list.Items.RemoveAt(index);
                }
            }

            bool aligned = list.Items.Count <= rows.Count &&
                list.Items.Cast<ListViewItem>()
                    .Select(static item => item.Tag as string)
                    .SequenceEqual(rows.Take(list.Items.Count).Select(static row => row.Event.Id));
            if (!aligned)
            {
                list.Items.Clear();
                foreach (AgentObservabilityActivityRow row in rows)
                {
                    ListViewItem item = ActivityItem(row, includeMod);
                    if (!includeMod)
                    {
                        item.SubItems.Add(row.IssueIds.Count == 0
                            ? string.Empty
                            : row.IssueIds.Count.ToString());
                    }
                    item.Tag = row.Event.Id;
                    list.Items.Add(item);
                }
            }
            else
            {
                for (int index = 0; index < list.Items.Count; index++)
                {
                    UpdateActivityItem(list.Items[index], rows[index], includeMod);
                }

                for (int index = list.Items.Count; index < rows.Count; index++)
                {
                    AgentObservabilityActivityRow row = rows[index];
                    ListViewItem item = ActivityItem(row, includeMod);
                    if (!includeMod)
                    {
                        item.SubItems.Add(row.IssueIds.Count == 0
                            ? string.Empty
                            : row.IssueIds.Count.ToString());
                    }
                    item.Tag = row.Event.Id;
                    list.Items.Add(item);
                }
            }

            if (selected is not null)
            {
                ListViewItem? selectedItem = list.Items.Cast<ListViewItem>()
                    .FirstOrDefault(item => string.Equals(
                        item.Tag as string,
                        selected,
                        StringComparison.Ordinal));
                if (selectedItem is not null)
                {
                    selectedItem.Selected = true;
                }
            }
        }
        finally
        {
            list.EndUpdate();
        }

        RestoreTopIndex(list, topIndex);
    }

    private static void UpdateActivityItem(
        ListViewItem item,
        AgentObservabilityActivityRow row,
        bool includeMod)
    {
        string time = DateTimeOffset.FromUnixTimeMilliseconds(row.Event.Timestamp)
            .ToLocalTime()
            .ToString("HH:mm:ss");
        item.SubItems[0].Text = time;
        if (includeMod)
        {
            item.SubItems[1].Text = row.ModName;
            item.SubItems[2].Text = row.Event.Stage.ToString();
            item.SubItems[3].Text = row.Event.Summary;
            item.SubItems[4].Text = row.AgentStatus is null ? string.Empty : StatusText(row.AgentStatus.Value);
        }
        else
        {
            item.SubItems[1].Text = row.Event.Stage.ToString();
            item.SubItems[2].Text = row.Event.Summary;
            item.SubItems[3].Text = row.IssueIds.Count == 0 ? string.Empty : row.IssueIds.Count.ToString();
        }

        item.BackColor = row.HasIssue ? Color.MistyRose : SystemColors.Window;
    }

    private void OnIssueChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (suppressIssueSelection || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        string[] ids = issueList.Items.Cast<ListViewItem>()
            .Where(item => item.Checked && item.Tag is AgentIssue)
            .Select(item => ((AgentIssue)item.Tag!).Id)
            .ToArray();
        observabilityUi.SelectIssues(ids);
        bundleJson = null;
        bundleStatusMessage = null;
        AgentObservabilityUiSnapshot snapshot = observabilityUi.Snapshot;
        if (snapshot.Issues is not null)
        {
            RefreshIssueDetails(snapshot.Issues, snapshot.Issue, snapshot);
        }
    }

    private void OnIssueSelected(object? sender, EventArgs e)
    {
        if (suppressIssueSelection ||
            issueList.SelectedItems.Count == 0 ||
            issueList.SelectedItems[0].Tag is not AgentIssue issue)
        {
            return;
        }

        observabilityUi.SelectIssue(issue.Id);
        bundleJson = null;
        bundleStatusMessage = null;
        AgentObservabilityUiSnapshot snapshot = observabilityUi.Snapshot;
        if (snapshot.Issues is not null)
        {
            RefreshIssueDetails(snapshot.Issues, snapshot.Issue, snapshot);
        }
    }

    private void OnViewIssueActivity(object? sender, EventArgs e)
    {
        AgentIssue? issue = SelectedIssue();
        if (issue is null)
        {
            return;
        }

        observabilityUi.ShowAgent(
            issue.AgentId,
            issue.RunId,
            issue.EventIds.FirstOrDefault());
        RefreshFromSnapshot(observabilityUi.Snapshot);
    }

    private void OnViewIssueDetails(object? sender, EventArgs e)
    {
        AgentIssue? issue = SelectedIssue();
        if (issue is null)
        {
            return;
        }

        observabilityUi.ShowIssue(issue.Id);
        RefreshFromSnapshot(observabilityUi.Snapshot);
    }

    private void OnPrepareAssessment(object? sender, EventArgs e)
    {
        try
        {
            PrepareFreshBundle();
            RefreshFromSnapshot(observabilityUi.Snapshot);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            issueDetails.Text = exception.Message;
            bundleStatusMessage = exception.Message;
            copyBundleButton.Enabled = false;
            exportBundleButton.Enabled = false;
        }
    }

    private void OnAllActivitySelected(object? sender, EventArgs e)
    {
        if (suppressActivitySelection || allActivity.SelectedItems.Count == 0)
        {
            return;
        }

        ShowEventDetails(allActivity.SelectedItems[0], allDetails);
    }

    private void OnAgentActivitySelected(object? sender, EventArgs e)
    {
        if (suppressActivitySelection || agentActivity.SelectedItems.Count == 0 ||
            agentActivity.SelectedItems[0].Tag is not string eventId)
        {
            return;
        }

        try
        {
            observabilityUi.SelectEvent(eventId);
            RefreshFromSnapshot(observabilityUi.Snapshot);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or KeyNotFoundException)
        {
        }
    }

    private void OnAgentDetailTabChanged(object? sender, EventArgs e)
    {
        if (suppressDetailTabSelection || agentDetailTabs.SelectedIndex < 0)
        {
            return;
        }

        AgentObservabilityAgentDetailTab tab = agentDetailTabs.SelectedIndex switch
        {
            1 => AgentObservabilityAgentDetailTab.Artifacts,
            2 => AgentObservabilityAgentDetailTab.BuildTestIssues,
            _ => AgentObservabilityAgentDetailTab.Event
        };
        observabilityUi.SetAgentDetailTab(tab);
    }

    private void OnCopyBundle(object? sender, EventArgs e)
    {
        try
        {
            string json = PrepareFreshBundle();
            RefreshFromSnapshot(observabilityUi.Snapshot);
            Clipboard.SetText(json);
            streamStatus.Text = "Diagnostic bundle copied to the clipboard. " +
                bundleStatusMessage;
        }
        catch (ExternalException)
        {
            streamStatus.Text = "The diagnostic bundle could not be copied.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            bundleStatusMessage = exception.Message;
            streamStatus.Text = exception.Message;
        }
    }

    private void OnExportBundle(object? sender, EventArgs e)
    {
        string json;
        try
        {
            // Export is intentionally one action: checked issues are read at
            // click time, the bundle is rebuilt, and only then is the Save
            // dialog shown.
            json = PrepareFreshBundle();
            RefreshFromSnapshot(observabilityUi.Snapshot);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            bundleStatusMessage = exception.Message;
            streamStatus.Text = exception.Message;
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "rimliaison-diagnostic-bundle.json",
            AddExtension = true,
            DefaultExt = "json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, json, Encoding.UTF8);
            streamStatus.Text = "Diagnostic bundle exported. " + bundleStatusMessage;
        }
        catch (IOException exception)
        {
            streamStatus.Text = "Export failed: " + exception.Message;
        }
    }

    private string PrepareFreshBundle()
    {
        string[] ids = issueList.Items.Cast<ListViewItem>()
            .Where(item => item.Checked && item.Tag is AgentIssue)
            .Select(item => ((AgentIssue)item.Tag!).Id)
            .ToArray();
        observabilityUi.SelectIssues(ids);
        AgentDiagnosticBundle bundle = observabilityUi.PrepareAssessment();
        bundleJson = JsonSerializer.Serialize(
            bundle,
            new JsonSerializerOptions(AgentObservabilityJson.Options)
            {
                WriteIndented = true
            });
        bundleStatusMessage = FormatBundleStatus(bundle);
        return bundleJson;
    }

    private static string FormatBundleStatus(AgentDiagnosticBundle bundle)
    {
        if (bundle.Completeness.IsComplete)
        {
            return "Diagnostic bundle: complete.";
        }

        string missing = bundle.Completeness.MissingEvidence.Count == 0
            ? "unspecified evidence gap"
            : string.Join(", ", bundle.Completeness.MissingEvidence);
        return "Diagnostic bundle: incomplete (missing " + missing + ").";
    }

    private AgentIssue? SelectedIssue() =>
        issueList.SelectedItems.Count == 0
            ? null
            : issueList.SelectedItems[0].Tag as AgentIssue;

    private void ShowEventDetails(ListViewItem item, TextBox target)
    {
        if (item.Tag is not string eventId)
        {
            return;
        }

        AgentObservabilityEventDetail? detail = observabilityUi.GetEventDetail(eventId);
        if (detail is null)
        {
            return;
        }

        target.Text = FormatEventDetail(detail);
    }

    private static string FormatEventDetail(AgentObservabilityEventDetail detail)
    {
        AgentEvent eventRecord = detail.Event;
        var builder = new StringBuilder();
        builder.AppendLine("Event");
        builder.AppendLine("-----");
        builder.AppendLine($"Id:        {eventRecord.Id}");
        builder.AppendLine($"Mod:       {eventRecord.ModId}");
        builder.AppendLine($"Agent:     {eventRecord.AgentId}");
        builder.AppendLine($"Stage:     {eventRecord.Stage}");
        builder.AppendLine($"Type:      {eventRecord.Type}");
        builder.AppendLine($"Timestamp: {DateTimeOffset.FromUnixTimeMilliseconds(eventRecord.Timestamp):O}");
        builder.AppendLine($"Sequence:  {eventRecord.Sequence}");
        builder.AppendLine($"Status:    {detail.Status ?? "—"}");
        if (detail.DurationMilliseconds is long duration)
        {
            builder.AppendLine($"Duration:  {duration} ms");
        }
        if (!string.IsNullOrWhiteSpace(detail.OperationKey))
        {
            builder.AppendLine($"Operation: {detail.OperationKey}");
        }
        builder.AppendLine($"Summary:   {eventRecord.Summary}");
        if (eventRecord.Data is JsonElement data)
        {
            builder.AppendLine();
            builder.AppendLine("Bounded data:");
            builder.AppendLine(data.GetRawText());
        }

        if (!string.IsNullOrWhiteSpace(eventRecord.TraceId) ||
            !string.IsNullOrWhiteSpace(eventRecord.SpanId))
        {
            builder.AppendLine();
            builder.AppendLine("Technical correlation:");
            builder.AppendLine($"Trace:     {eventRecord.TraceId ?? "—"}");
            builder.AppendLine($"Span:      {eventRecord.SpanId ?? "—"}");
        }

        return builder.ToString();
    }

    private static string FormatEventEvidence(AgentObservabilityEventDetail detail)
    {
        var builder = new StringBuilder();
        AppendSection(builder, "Files", detail.Files);
        AppendSection(builder, "Tools", detail.Tools);
        AppendSection(builder, "Commands", detail.Commands);
        if (detail.Output.Count > 0)
        {
            builder.AppendLine("Output excerpts:");
            foreach (AgentObservabilityOutputExcerpt excerpt in detail.Output)
            {
                builder.AppendLine($"  [{excerpt.Kind}] {excerpt.EventId}");
                builder.AppendLine(excerpt.Text);
            }
        }
        else
        {
            builder.AppendLine("Output excerpts:");
            builder.AppendLine("  No output was recorded for this activity.");
        }

        return builder.ToString();
    }

    private static string FormatEventResults(AgentObservabilityEventDetail detail)
    {
        var builder = new StringBuilder();
        bool hasData = detail.BuildResults.Count > 0 ||
            detail.TestResults.Count > 0 ||
            detail.Issues.Count > 0;
        if (!hasData)
        {
            return "No build, test, or issue data for this activity.";
        }

        builder.AppendLine("Build results:");
        if (detail.BuildResults.Count == 0)
        {
            builder.AppendLine("  No build data.");
        }
        else
        {
            foreach (AgentObservabilityBuildTestResult result in detail.BuildResults)
            {
                builder.AppendLine($"  {result.Status}: {result.Summary}");
            }
        }

        builder.AppendLine("Test results:");
        if (detail.TestResults.Count == 0)
        {
            builder.AppendLine("  No test data.");
        }
        else
        {
            foreach (AgentObservabilityBuildTestResult result in detail.TestResults)
            {
                builder.AppendLine($"  {result.Status}: {result.Summary}");
            }
        }

        builder.AppendLine("Related issues:");
        foreach (AgentIssue issue in detail.Issues)
        {
            builder.AppendLine(
                $"  {issue.Id} · {issue.Severity} · {(issue.Recovered ? "recovered" : "unresolved")} · {issue.Summary}");
        }

        return builder.ToString();
    }

    private static string FormatIssueDetail(AgentObservabilityIssueDetail detail)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Issue");
        builder.AppendLine("-----");
        builder.AppendLine($"Id:         {detail.Issue.Id}");
        builder.AppendLine($"Mod:        {detail.Issue.ModId}");
        builder.AppendLine($"Agent:      {detail.Issue.AgentId}");
        builder.AppendLine($"Stage:      {detail.Issue.Stage?.ToString() ?? "—"}");
        builder.AppendLine($"Category:   {detail.Issue.Category}");
        builder.AppendLine($"Severity:   {detail.Issue.Severity}");
        builder.AppendLine($"State:      {detail.RecoveryState}");
        builder.AppendLine($"Timestamp:  {DateTimeOffset.FromUnixTimeMilliseconds(detail.Issue.Timestamp):O}");
        builder.AppendLine($"Summary:    {detail.Issue.Summary}");
        builder.AppendLine();
        builder.AppendLine("Supporting events:");
        foreach (AgentEvent eventRecord in detail.SupportingEvents)
        {
            builder.AppendLine($"  {eventRecord.Sequence}: {eventRecord.Id} · {eventRecord.Summary}");
        }

        if (detail.UnresolvedEventIds.Count > 0)
        {
            builder.AppendLine("Unresolved event references: " + string.Join(", ", detail.UnresolvedEventIds));
        }

        AppendSection(builder, "Files", detail.RelatedFiles);
        AppendSection(builder, "Tools", detail.RelatedTools);
        AppendSection(builder, "Commands", detail.RelatedCommands);
        if (detail.Output.Count > 0)
        {
            builder.AppendLine("Output:");
            foreach (AgentObservabilityOutputExcerpt excerpt in detail.Output)
            {
                builder.AppendLine($"  [{excerpt.Kind}] {excerpt.EventId}");
                builder.AppendLine(excerpt.Text);
            }
        }

        builder.AppendLine("Recovery: " +
            (detail.ResolutionEvent is null ? "not resolved" : detail.ResolutionEvent.Summary));
        if (!string.IsNullOrWhiteSpace(detail.TraceId) || detail.SpanIds.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Technical correlation:");
            builder.AppendLine("Trace: " + (detail.TraceId ?? "—"));
            builder.AppendLine("Spans: " + (detail.SpanIds.Count == 0 ? "—" : string.Join(", ", detail.SpanIds)));
        }

        return builder.ToString();
    }

    private static string FormatEvidence(AgentObservabilityAgentView view)
    {
        var builder = new StringBuilder();
        AppendSection(builder, "Files", view.Files);
        AppendSection(builder, "Tools", view.Tools);
        AppendSection(builder, "Commands", view.Commands);
        return builder.ToString();
    }

    private static string FormatResults(AgentObservabilityAgentView view)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Build results:");
        foreach (AgentObservabilityBuildTestResult result in view.BuildResults)
        {
            builder.AppendLine($"  {result.Status}: {result.Summary}");
        }

        builder.AppendLine("Test results:");
        foreach (AgentObservabilityBuildTestResult result in view.TestResults)
        {
            builder.AppendLine($"  {result.Status}: {result.Summary}");
        }

        AppendSection(builder, "Warnings", view.Warnings.Select(issue => issue.Summary));
        AppendSection(builder, "Errors", view.Errors.Select(issue => issue.Summary));
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, IEnumerable<string> values)
    {
        string[] bounded = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(256)
            .ToArray();
        builder.AppendLine(title + ":");
        if (bounded.Length == 0)
        {
            builder.AppendLine("  —");
            return;
        }

        foreach (string value in bounded)
        {
            builder.AppendLine("  " + value);
        }
    }

    private static string StatusText(AgentStatus status) => status switch
    {
        AgentStatus.Created => "Created",
        AgentStatus.Running => "Working",
        AgentStatus.Waiting => "Waiting",
        AgentStatus.Completed => "Completed",
        AgentStatus.Failed => "Failed",
        _ => status.ToString()
    };

    private static string StageGlyph(string state) => state switch
    {
        "completed" => "✓",
        "failed" => "✗",
        "waiting" => "Ⅱ",
        "created" => "○",
        "current" => "●",
        _ => "○"
    };

    private static int TopIndex(ListView list) =>
        list.Items.Count == 0 ? 0 : Math.Max(0, list.TopItem?.Index ?? 0);

    private static string? SelectedTag(ListView list) =>
        list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as string;

    private static void RestoreTopIndex(ListView list, int index)
    {
        if (list.Items.Count == 0)
        {
            return;
        }

        list.TopItem = list.Items[Math.Min(index, list.Items.Count - 1)];
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e) => Dispose();
}
