using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RimLiaison.Observability;

namespace RimLiaison.Desktop;

public readonly record struct AgentObservabilityDesktopMutationCounts(
    long StageControlCreations,
    long StageControlRemovals,
    long StageControlReplacements,
    long IssueItemInserts,
    long IssueItemRemovals,
    long IssueItemMoves,
    long IssueItemUpdates,
    long IssueItemClears);

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
    private readonly ListView pastSessions;
    private readonly Button viewActivityButton;
    private readonly Button issueDetailsButton;
    private readonly Button loadMoreIssuesButton;
    private readonly Button prepareAssessmentButton;
    private readonly Button copyChatButton;
    private readonly Button copyBundleButton;
    private readonly Button exportBundleButton;
    private TabControl agentDetailTabs = null!;
    private readonly Dictionary<DevelopmentStage, Label> stageControls = [];
    private readonly Dictionary<string, ListViewItem> issueItems =
        new(StringComparer.Ordinal);
    private string? renderedIssueDetailSignature;
    private AgentObservabilityIssuesView? renderedIssuesView;
    private string? renderedIssueSelectionId;
    private AgentObservabilityAllView? renderedAllView;
    private long renderedAgentDataRevision = -1;
    private string? renderedAgentEventId;
    private long renderedAgentDetailRevision = -1;
    private string? bundleJson;
    private string? bundleStatusMessage;
    private string? renderedNavigationSignature;
    private string? renderedPastSessionsSignature;
    private bool suppressIssueSelection;
    private bool suppressActivitySelection;
    private bool suppressDetailTabSelection;
    private long stageControlCreations;
    private long stageControlRemovals;
    private long stageControlReplacements;
    private long issueItemInserts;
    private long issueItemRemovals;
    private long issueItemMoves;
    private long issueItemUpdates;
    private long issueItemClears;
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
                MaximumIssueRows = 100,
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
            ("Shared", 92),
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
        loadMoreIssuesButton = CreateButton("Load older issues", OnLoadMoreIssues);
        prepareAssessmentButton = CreateButton("Preview full assessment", OnPrepareAssessment);
        copyChatButton = CreateButton("Copy for ChatGPT", OnCopyForChatGPT);
        copyBundleButton = CreateButton("Copy full diagnostic", OnCopyBundle);
        exportBundleButton = CreateButton("Export full diagnostic", OnExportBundle);
        copyChatButton.Enabled = false;
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
        agentProgress = new BufferedFlowLayoutPanel
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
        pastSessions = CreateListView(
            ("Start", 150),
            ("Completed", 150),
            ("Duration", 100),
            ("Status", 110),
            ("Run / session", 300));
        pastSessions.SelectedIndexChanged += OnPastSessionSelected;
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
    public AgentObservabilityDesktopMutationCounts MutationCounts =>
        new(
            stageControlCreations,
            stageControlRemovals,
            stageControlReplacements,
            issueItemInserts,
            issueItemRemovals,
            issueItemMoves,
            issueItemUpdates,
            issueItemClears);

    public void ResetMutationCounts()
    {
        stageControlCreations = 0;
        stageControlRemovals = 0;
        stageControlReplacements = 0;
        issueItemInserts = 0;
        issueItemRemovals = 0;
        issueItemMoves = 0;
        issueItemUpdates = 0;
        issueItemClears = 0;
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
        actions.Controls.Add(loadMoreIssuesButton);
        actions.Controls.Add(prepareAssessmentButton);
        actions.Controls.Add(copyChatButton);
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
        agentDetailTabs.TabPages.Add(CreateTab("Past sessions", pastSessions));
        agentDetailTabs.SelectedIndexChanged += OnAgentDetailTabChanged;
        split.Panel2.Controls.Add(agentDetailTabs);
        panel.Controls.Add(split);
        panel.Controls.Add(agentProgress);
        panel.Controls.Add(agentHeader);
        return panel;
    }

    private sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    private sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
    {
        public BufferedFlowLayoutPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    private static TabPage CreateTab(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private static ListView CreateListView(params (string Name, int Width)[] columns)
    {
        var list = new BufferedListView
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
        SetText(streamStatus, bundleStatusMessage ?? liveStatus);

        SetVisible(allPanel, snapshot.View == AgentObservabilityUiView.All);
        SetVisible(
            issuesPanel,
            snapshot.View is AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue);
        SetVisible(agentPanel, snapshot.View == AgentObservabilityUiView.Agent);
        SetText(
            viewTitle,
            snapshot.View switch
            {
                AgentObservabilityUiView.All => "All",
                AgentObservabilityUiView.Issues or AgentObservabilityUiView.Issue => "Issues",
                AgentObservabilityUiView.Agent => snapshot.Agent?.Agent.ModName ?? "Agent",
                _ => "RimLiaison"
            });

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
                item.NavigationStatus,
                item.HasUnresolvedError)));
        if (string.Equals(renderedNavigationSignature, navigationSignature, StringComparison.Ordinal))
        {
            return;
        }

        renderedNavigationSignature = navigationSignature;
        HashSet<string> desiredKeys = snapshot.Navigation.Items
            .Select(static item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        navigationPanel.SuspendLayout();
        try
        {
            for (int index = navigationPanel.Controls.Count - 1; index >= 0; index--)
            {
                if (navigationPanel.Controls[index].Tag is not string key ||
                    !desiredKeys.Contains(key))
                {
                    navigationPanel.Controls.RemoveAt(index);
                }
            }

            for (int index = 0; index < snapshot.Navigation.Items.Count; index++)
            {
                AgentObservabilityUiNavigationItem item = snapshot.Navigation.Items[index];
                Panel? container = navigationPanel.Controls
                    .Cast<Control>()
                    .FirstOrDefault(control =>
                        string.Equals(control.Tag as string, item.Key, StringComparison.Ordinal)) as Panel;
                if (container is null)
                {
                    container = CreateNavigationItem(item);
                    navigationPanel.Controls.Add(container);
                    navigationPanel.Controls.SetChildIndex(container, index);
                }
                else
                {
                    UpdateNavigationItem(container, item);
                    int currentIndex = navigationPanel.Controls.GetChildIndex(container);
                    if (currentIndex != index)
                    {
                        navigationPanel.Controls.SetChildIndex(container, index);
                    }
                }
            }
        }
        finally
        {
            navigationPanel.ResumeLayout();
        }
    }

    private Panel CreateNavigationItem(AgentObservabilityUiNavigationItem item)
    {
        var container = new Panel
        {
            Tag = item.Key,
            Height = 30,
            Margin = new Padding(0, 0, 6, 0)
        };
        var button = new Button
        {
            Tag = item,
            AutoSize = false,
            Height = 30,
            Margin = Padding.Empty,
            FlatStyle = FlatStyle.System,
            UseMnemonic = false
        };
        button.Click += OnNavigationClick;
        container.Controls.Add(button);
        UpdateNavigationItem(container, item);
        return container;
    }

    private void UpdateNavigationItem(
        Panel container,
        AgentObservabilityUiNavigationItem item)
    {
        if (container.Controls.OfType<Button>().FirstOrDefault() is not Button button)
        {
            return;
        }

        string statusMarker = item.NavigationStatus switch
        {
            AgentObservabilityAgentNavigationStatus.NeedsAttention => "! ",
            AgentObservabilityAgentNavigationStatus.Failed => "x ",
            AgentObservabilityAgentNavigationStatus.Working => "> ",
            _ => string.Empty
        };
        string displayLabel = statusMarker + item.Label;
        int fullWidth = Math.Min(240, Math.Max(96, displayLabel.Length * 9 + 28));
        if (container.Width != fullWidth)
        {
            container.Width = fullWidth;
        }

        SetText(button, displayLabel);
        if (button.Width != fullWidth)
        {
            button.Width = fullWidth;
        }

        if (button.Tag is not AgentObservabilityUiNavigationItem existing ||
            existing != item)
        {
            button.Tag = item;
        }

        string accessibleName = item.FullLabel + " · " + item.NavigationStatus;
        if (!string.Equals(button.AccessibleName, accessibleName, StringComparison.Ordinal))
        {
            button.AccessibleName = accessibleName;
            toolTip.SetToolTip(button, accessibleName);
        }

        Color backColor = item.Selected
            ? SystemColors.Highlight
            : item.NavigationStatus == AgentObservabilityAgentNavigationStatus.NeedsAttention
                ? Color.MistyRose
                : item.NavigationStatus == AgentObservabilityAgentNavigationStatus.Failed
                    ? Color.LightSalmon
                    : SystemColors.Control;
        if (button.BackColor != backColor)
        {
            button.BackColor = backColor;
        }

        Color foreColor = item.Selected
            ? SystemColors.HighlightText
            : item.NavigationStatus is AgentObservabilityAgentNavigationStatus.NeedsAttention or AgentObservabilityAgentNavigationStatus.Failed
                ? Color.DarkRed
                : SystemColors.ControlText;
        if (button.ForeColor != foreColor)
        {
            button.ForeColor = foreColor;
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


    private void RefreshAll(AgentObservabilityAllView view)
    {
        if (ReferenceEquals(renderedAllView, view))
        {
            return;
        }

        renderedAllView = view;
        SetText(
            allAgentSummary,
            view.Agents.Count == 0
                ? view.EmptyState ?? "No agents"
                : string.Join(
                    "   ",
                    view.Agents.Select(agent =>
                        $"{agent.ModName}: {StatusText(agent.Status)}")));
        RefreshActivityList(allActivity, view.Activity, includeMod: true);
        if (allActivity.SelectedItems.Count == 0)
        {
            SetText(
                allDetails,
                view.EmptyState ?? "Select an activity row to inspect bounded details.");
        }
    }

    private void RefreshIssues(
        AgentObservabilityIssuesView view,
        AgentObservabilityIssueDetail? detail,
        AgentObservabilityUiSnapshot snapshot)
    {
        string? selectedIssue = snapshot.SelectedIssueId;
        AgentObservabilityIssueListItem[] desired = view.Issues
            .Select(static row => new AgentObservabilityIssueListItem(row.Issue.Id, row))
            .ToArray();
        AgentObservabilityIssueListItem[] current = issueList.Items
            .Cast<ListViewItem>()
            .Select(item => item.Tag as AgentObservabilityIssueListItem)
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();
        AgentObservabilityIssueReconciliationPlan plan =
            AgentObservabilityIssueReconciliation.Plan(current, desired);
        bool selectionChanged = !string.Equals(
            renderedIssueSelectionId,
            selectedIssue,
            StringComparison.Ordinal);
        bool listChanged = plan.HasChanges ||
            !ReferenceEquals(renderedIssuesView, view) ||
            selectionChanged;

        if (listChanged)
        {
            int topIndex = TopIndex(issueList);
            suppressIssueSelection = true;
            if (plan.HasChanges)
            {
                issueList.BeginUpdate();
            }

            try
            {
                foreach (string issueId in plan.RemovedIssueIds)
                {
                    if (issueItems.Remove(issueId, out ListViewItem? item))
                    {
                        issueList.Items.Remove(item);
                        issueItemRemovals++;
                    }
                }

                HashSet<string> updatedIds =
                    plan.UpdatedIssueIds.ToHashSet(StringComparer.Ordinal);
                for (int index = 0; index < desired.Length; index++)
                {
                    AgentObservabilityIssueListItem desiredItem = desired[index];
                    if (!issueItems.TryGetValue(desiredItem.IssueId, out ListViewItem? item))
                    {
                        item = CreateIssueItem(desiredItem);
                        issueItems[desiredItem.IssueId] = item;
                        issueList.Items.Insert(index, item);
                        issueItemInserts++;
                    }
                    else
                    {
                        int currentIndex = issueList.Items.IndexOf(item);
                        if (currentIndex != index)
                        {
                            issueList.Items.RemoveAt(currentIndex);
                            issueList.Items.Insert(index, item);
                            issueItemMoves++;
                        }

                        if (updatedIds.Contains(desiredItem.IssueId))
                        {
                            UpdateIssueItem(item, desiredItem);
                        }
                    }

                    bool shouldBeChecked = view.SelectedIssueIds.Contains(
                        desiredItem.IssueId,
                        StringComparer.Ordinal);
                    if (item.Checked != shouldBeChecked)
                    {
                        item.Checked = shouldBeChecked;
                        issueItemUpdates++;
                    }

                    bool shouldBeSelected = string.Equals(
                        desiredItem.IssueId,
                        selectedIssue,
                        StringComparison.Ordinal);
                    if (item.Selected != shouldBeSelected)
                    {
                        item.Selected = shouldBeSelected;
                    }
                }
            }
            finally
            {
                if (plan.HasChanges)
                {
                    issueList.EndUpdate();
                }

                suppressIssueSelection = false;
            }

            RestoreTopIndex(issueList, topIndex);
            renderedIssuesView = view;
            renderedIssueSelectionId = selectedIssue;
        }

        RefreshIssueDetails(view, detail, snapshot);
        SetEnabled(loadMoreIssuesButton, view.HasMoreIssues);
    }

    private ListViewItem CreateIssueItem(
        AgentObservabilityIssueListItem state)
    {
        var item = new ListViewItem(state.Row.StateLabel);
        for (int index = 1; index < 7; index++)
        {
            item.SubItems.Add(string.Empty);
        }

        UpdateIssueItem(item, state);
        return item;
    }

    private void UpdateIssueItem(
        ListViewItem item,
        AgentObservabilityIssueListItem state)
    {
        bool changed = item.Tag is not AgentObservabilityIssueListItem current ||
            current.Row != state.Row;
        if (!changed)
        {
            return;
        }

        SetSubItem(item, 0, state.Row.StateLabel);
        SetSubItem(item, 1, state.Row.Issue.Severity.ToString());
        SetSubItem(
            item,
            2,
            state.Row.SharedAgentCount > 1
                ? state.Row.SharedAgentCount + " agents"
                : string.Empty);
        SetSubItem(item, 3, state.Row.ModName);
        SetSubItem(item, 4, state.Row.Issue.Category.ToString());
        SetSubItem(item, 5, state.Row.Issue.Summary);
        SetSubItem(item, 6, state.Row.Issue.Id);
        Color foreColor = state.Row.Issue.Recovered
            ? Color.DarkGreen
            : state.Row.Issue.Severity == AgentIssueSeverity.Error
                ? Color.DarkRed
                : SystemColors.WindowText;
        if (item.ForeColor != foreColor)
        {
            item.ForeColor = foreColor;
        }

        item.Tag = state;
        issueItemUpdates++;
    }

    private void RefreshIssueDetails(
        AgentObservabilityIssuesView view,
        AgentObservabilityIssueDetail? detail,
        AgentObservabilityUiSnapshot snapshot)
    {
        if (snapshot.IssueMode == AgentObservabilityIssueMode.Assessment &&
            snapshot.Assessment is not null)
        {
            string signature = "assessment:" + string.Join(
                '\u001F',
                snapshot.Assessment.IssueIds);
            if (!string.Equals(
                    renderedIssueDetailSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                bundleJson = JsonSerializer.Serialize(
                    snapshot.Assessment,
                    new JsonSerializerOptions(AgentObservabilityJson.Options)
                    {
                        WriteIndented = true
                    });
                SetText(issueDetails, bundleJson);
                bundleStatusMessage = FormatBundleStatus(snapshot.Assessment);
                renderedIssueDetailSignature = signature;
            }

            SetEnabled(copyChatButton, false);
            SetEnabled(copyBundleButton, true);
            SetEnabled(exportBundleButton, true);
            return;
        }

        if (detail is not null)
        {
            string signature = "detail:" + detail.Issue.Id + ":" + snapshot.Stream.Revision;
            if (!string.Equals(
                    renderedIssueDetailSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                bundleJson = null;
                SetText(issueDetails, FormatIssueDetail(detail));
                renderedIssueDetailSignature = signature;
            }

            SetEnabled(copyChatButton, detail.Triage is not null);
            bool hasCheckedIssues = snapshot.SelectedIssueIds.Count > 0;
            SetEnabled(copyBundleButton, hasCheckedIssues);
            SetEnabled(exportBundleButton, hasCheckedIssues);
            return;
        }

        bundleJson = null;
        string emptySignature = "empty:" + (view.EmptyState ?? string.Empty);
        if (!string.Equals(
                renderedIssueDetailSignature,
                emptySignature,
                StringComparison.Ordinal))
        {
            SetText(
                issueDetails,
                view.EmptyState ?? "Select an issue to inspect supporting evidence.");
            renderedIssueDetailSignature = emptySignature;
        }

        SetEnabled(copyChatButton, false);
        bool hasSelectedIssues = snapshot.SelectedIssueIds.Count > 0;
        SetEnabled(copyBundleButton, hasSelectedIssues);
        SetEnabled(exportBundleButton, hasSelectedIssues);
    }

    private void RefreshAgent(
        AgentObservabilityAgentView view,
        AgentObservabilityUiSnapshot snapshot)
    {
        AgentSnapshot agent = view.Agent;
        SetText(
            agentHeader,
            $"{agent.ModName}   ·   {StatusText(agent.Status)}   ·   {agent.CurrentStage}   ·   " +
            $"{view.ElapsedMilliseconds / 1000.0:0.0}s   ·   session {agent.RunId}   ·   {agent.CurrentActivity ?? "—"}");
        RefreshStageProgress(view.StageProgress);

        bool dataChanged =
            renderedAgentDataRevision != snapshot.Stream.Revision ||
            !string.Equals(
                renderedAgentEventId,
                view.SelectedEventId,
                StringComparison.Ordinal);
        if (dataChanged)
        {
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

            RefreshPastSessions(view.PastSessions);
            renderedAgentDataRevision = snapshot.Stream.Revision;
            renderedAgentEventId = view.SelectedEventId;
        }

        if (view.SelectedEvent is not null &&
            (renderedAgentDetailRevision != snapshot.Stream.Revision ||
             !string.Equals(
                 renderedAgentEventId,
                 view.SelectedEventId,
                 StringComparison.Ordinal)))
        {
            SetText(agentDetails, FormatEventDetail(view.SelectedEvent));
            SetText(agentEvidence, FormatEventEvidence(view.SelectedEvent));
            SetText(agentResults, FormatEventResults(view.SelectedEvent));
            renderedAgentDetailRevision = snapshot.Stream.Revision;
        }
        else if (view.SelectedEvent is null)
        {
            SetText(
                agentDetails,
                view.EmptyState ?? "Select an activity row to inspect bounded details.");
            SetText(
                agentEvidence,
                "Select an activity row to inspect files, tools, and commands.");
            SetText(
                agentResults,
                "Select an activity row to inspect build, test, and issue data.");
            renderedAgentDetailRevision = snapshot.Stream.Revision;
        }

        if (agentDetailTabs.SelectedIndex != 3)
        {
            int selectedTab = snapshot.AgentDetailTab switch
            {
                AgentObservabilityAgentDetailTab.Artifacts => 1,
                AgentObservabilityAgentDetailTab.BuildTestIssues => 2,
                _ => 0
            };
            if (agentDetailTabs.SelectedIndex != selectedTab)
            {
                suppressDetailTabSelection = true;
                try
                {
                    agentDetailTabs.SelectedIndex = selectedTab;
                }
                finally
                {
                    suppressDetailTabSelection = false;
                }
            }
        }
    }

    private void RefreshStageProgress(
        IReadOnlyList<AgentObservabilityStageProgress> desired)
    {
        AgentObservabilityStageProgress[] current = agentProgress.Controls
            .Cast<Control>()
            .Select(control => control.Tag as AgentObservabilityStageProgress)
            .Where(static stage => stage is not null)
            .Select(static stage => stage!)
            .ToArray();
        AgentObservabilityStageReconciliationPlan plan =
            AgentObservabilityStageReconciliation.Plan(current, desired);
        if (!plan.HasChanges)
        {
            return;
        }

        agentProgress.SuspendLayout();
        try
        {
            foreach (DevelopmentStage stage in plan.RemovedStages)
            {
                if (stageControls.Remove(stage, out Label? label))
                {
                    agentProgress.Controls.Remove(label);
                    label.Dispose();
                    stageControlRemovals++;
                }
            }

            for (int index = 0; index < desired.Count; index++)
            {
                AgentObservabilityStageProgress state = desired[index];
                if (!stageControls.TryGetValue(state.Stage, out Label? label))
                {
                    label = CreateStageLabel(state);
                    stageControls[state.Stage] = label;
                    agentProgress.Controls.Add(label);
                    stageControlCreations++;
                }

                int currentIndex = agentProgress.Controls.IndexOf(label);
                if (currentIndex != index)
                {
                    agentProgress.Controls.SetChildIndex(label, index);
                }

                UpdateStageLabel(label, state);
            }
        }
        finally
        {
            agentProgress.ResumeLayout();
        }
    }

    private Label CreateStageLabel(AgentObservabilityStageProgress state)
    {
        var label = new Label
        {
            AutoSize = true,
            Padding = new Padding(5, 4, 5, 3),
            Margin = new Padding(0, 0, 4, 0),
            BorderStyle = BorderStyle.FixedSingle
        };
        UpdateStageLabel(label, state);
        return label;
    }

    private void UpdateStageLabel(
        Label label,
        AgentObservabilityStageProgress state)
    {
        SetText(label, StageGlyph(state.State) + " " + state.Stage);
        Color foreColor = state.State is "failed" ? Color.DarkRed : Color.Black;
        if (label.ForeColor != foreColor)
        {
            label.ForeColor = foreColor;
        }

        Color backColor = state.IsCurrent
            ? Color.LightGoldenrodYellow
            : Color.WhiteSmoke;
        if (label.BackColor != backColor)
        {
            label.BackColor = backColor;
        }

        if (label.Tag is not AgentObservabilityStageProgress current ||
            current != state)
        {
            label.Tag = state;
        }
    }

    private void RefreshPastSessions(IReadOnlyList<AgentObservabilitySessionSummary> sessions)
    {
        string signature = string.Join(
            '\u001E',
            sessions.Select(session => string.Join(
                '\u001F',
                session.RunId,
                session.AgentId,
                session.StartTime,
                session.CompletedAt,
                session.DurationMilliseconds,
                session.Status,
                session.CompletionState,
                session.FailureState,
                session.FailureSummary)));
        if (string.Equals(renderedPastSessionsSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        renderedPastSessionsSignature = signature;
        pastSessions.BeginUpdate();
        try
        {
            pastSessions.Items.Clear();
            foreach (AgentObservabilitySessionSummary session in sessions)
            {
                var item = new ListViewItem(FormatTimestamp(session.StartTime));
                item.SubItems.Add(session.CompletedAt is long completedAt
                    ? FormatTimestamp(completedAt)
                    : "—");
                item.SubItems.Add(session.DurationMilliseconds is long duration
                    ? FormatDuration(duration)
                    : "—");
                item.SubItems.Add(SessionStatusText(session));
                item.SubItems.Add(session.RunId);
                item.Tag = session;
                if (session.Status == AgentStatus.Failed || session.FailureState)
                {
                    item.ForeColor = Color.DarkRed;
                }

                pastSessions.Items.Add(item);
            }
        }
        finally
        {
            pastSessions.EndUpdate();
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
        else
        {
            item.SubItems.Add(row.IssueIds.Count == 0 ? string.Empty : row.IssueIds.Count.ToString());
        }

        item.BackColor = row.HasIssue ? Color.MistyRose : SystemColors.Window;
        item.Tag = new AgentObservabilityActivityListItem(row.Event.Id, row);
        return item;
    }

    private static void RefreshActivityList(
        ListView list,
        IReadOnlyList<AgentObservabilityActivityRow> rows,
        bool includeMod,
        string? selectedEventId = null)
    {
        string? selected = selectedEventId ?? SelectedEventId(list);
        var current = list.Items
            .Cast<ListViewItem>()
            .Select(item => item.Tag as AgentObservabilityActivityListItem)
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();
        AgentObservabilityActivityReconciliationPlan plan =
            AgentObservabilityActivityReconciliation.Plan(current, rows);
        if (!plan.HasChanges)
        {
            return;
        }

        int topIndex = TopIndex(list);
        HashSet<string> removedIds = plan.RemovedEventIds.ToHashSet(StringComparer.Ordinal);
        HashSet<string> updatedIds = plan.UpdatedEventIds.ToHashSet(StringComparer.Ordinal);
        list.BeginUpdate();
        try
        {
            for (int index = list.Items.Count - 1; index >= 0; index--)
            {
                if (EventIdFromTag(list.Items[index]) is string eventId &&
                    removedIds.Contains(eventId))
                {
                    list.Items.RemoveAt(index);
                }
            }

            for (int index = 0; index < rows.Count; index++)
            {
                AgentObservabilityActivityRow row = rows[index];
                ListViewItem? item = list.Items.Cast<ListViewItem>()
                    .FirstOrDefault(candidate =>
                        string.Equals(EventIdFromTag(candidate), row.Event.Id, StringComparison.Ordinal));
                if (item is null)
                {
                    list.Items.Insert(index, ActivityItem(row, includeMod));
                    continue;
                }

                int currentIndex = list.Items.IndexOf(item);
                if (currentIndex != index)
                {
                    list.Items.RemoveAt(currentIndex);
                    list.Items.Insert(index, item);
                }

                if (updatedIds.Contains(row.Event.Id))
                {
                    UpdateActivityItem(item, row, includeMod);
                    item.Tag = new AgentObservabilityActivityListItem(row.Event.Id, row);
                }
            }

            if (selected is not null)
            {
                ListViewItem? selectedItem = list.Items.Cast<ListViewItem>()
                    .FirstOrDefault(item => string.Equals(
                        EventIdFromTag(item),
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
        SetSubItem(item, 0, time);
        if (includeMod)
        {
            SetSubItem(item, 1, row.ModName);
            SetSubItem(item, 2, row.Event.Stage.ToString());
            SetSubItem(item, 3, row.Event.Summary);
            SetSubItem(item, 4, row.AgentStatus is null ? string.Empty : StatusText(row.AgentStatus.Value));
        }
        else
        {
            SetSubItem(item, 1, row.Event.Stage.ToString());
            SetSubItem(item, 2, row.Event.Summary);
            SetSubItem(item, 3, row.IssueIds.Count == 0 ? string.Empty : row.IssueIds.Count.ToString());
        }

        Color backColor = row.HasIssue ? Color.MistyRose : SystemColors.Window;
        if (item.BackColor != backColor)
        {
            item.BackColor = backColor;
        }
    }

    private static void SetText(Control control, string? value)
    {
        string text = value ?? string.Empty;
        if (!string.Equals(control.Text, text, StringComparison.Ordinal))
        {
            control.Text = text;
        }
    }

    private static void SetVisible(Control control, bool visible)
    {
        if (control.Visible != visible)
        {
            control.Visible = visible;
        }
    }

    private static void SetEnabled(Control control, bool enabled)
    {
        if (control.Enabled != enabled)
        {
            control.Enabled = enabled;
        }
    }

    private static void SetSubItem(ListViewItem item, int index, string value)
    {
        if (!string.Equals(item.SubItems[index].Text, value, StringComparison.Ordinal))
        {
            item.SubItems[index].Text = value;
        }
    }

    private void OnIssueChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (suppressIssueSelection || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        string[] ids = issueList.Items.Cast<ListViewItem>()
            .Where(item =>
                item.Checked &&
                item.Tag is AgentObservabilityIssueListItem)
            .Select(item => ((AgentObservabilityIssueListItem)item.Tag!).IssueId)
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
            issueList.SelectedItems[0].Tag is not AgentObservabilityIssueListItem item)
        {
            return;
        }

        observabilityUi.SelectIssue(item.IssueId);
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
    private void OnLoadMoreIssues(object? sender, EventArgs e)
    {
        RefreshFromSnapshot(observabilityUi.LoadMoreIssues());
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
        string? eventId = agentActivity.SelectedItems.Count == 0
            ? null
            : EventIdFromTag(agentActivity.SelectedItems[0]);
        if (suppressActivitySelection || eventId is null)
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

    private void OnPastSessionSelected(object? sender, EventArgs e)
    {
        if (pastSessions.SelectedItems.Count == 0 ||
            pastSessions.SelectedItems[0].Tag is not AgentObservabilitySessionSummary session)
        {
            return;
        }

        try
        {
            observabilityUi.ShowAgent(session.AgentId, session.RunId);
            suppressDetailTabSelection = true;
            try
            {
                agentDetailTabs.SelectedIndex = 0;
            }
            finally
            {
                suppressDetailTabSelection = false;
            }

            RefreshFromSnapshot(observabilityUi.Snapshot);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or KeyNotFoundException)
        {
        }
    }

    private void OnAgentDetailTabChanged(object? sender, EventArgs e)
    {
        if (suppressDetailTabSelection ||
            agentDetailTabs.SelectedIndex < 0 ||
            agentDetailTabs.SelectedIndex == 3)
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

    private void OnCopyForChatGPT(object? sender, EventArgs e)
    {
        AgentIssue? issue = SelectedIssue();
        if (issue is null)
        {
            return;
        }

        try
        {
            string packet = observabilityUi.CreateChatPacket(issue.Id);
            Clipboard.SetText(packet);
            streamStatus.Text = "Compact ChatGPT diagnostic packet copied.";
        }
        catch (ExternalException)
        {
            streamStatus.Text = "The ChatGPT diagnostic packet could not be copied.";
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or KeyNotFoundException)
        {
            streamStatus.Text = exception.Message;
        }
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
            .Where(item =>
                item.Checked &&
                item.Tag is AgentObservabilityIssueListItem)
            .Select(item => ((AgentObservabilityIssueListItem)item.Tag!).IssueId)
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

    private AgentIssue? SelectedIssue()
    {
        if (issueList.SelectedItems.Count == 0)
        {
            return null;
        }

        return issueList.SelectedItems[0].Tag is AgentObservabilityIssueListItem item
            ? item.Row.Issue
            : issueList.SelectedItems[0].Tag as AgentIssue;
    }

    private void ShowEventDetails(ListViewItem item, TextBox target)
    {
        string? eventId = EventIdFromTag(item);
        if (eventId is null)
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
        builder.AppendLine("Issue triage");
        builder.AppendLine("------------");
        if (detail.Triage is AgentObservabilityIssueTriage triage)
        {
            builder.AppendLine($"What failed:             {triage.WhatFailed}");
            builder.AppendLine($"Attempted operation:     {triage.AttemptedOperation}");
            builder.AppendLine($"Stage:                   {triage.Stage}");
            builder.AppendLine($"Currently blocked:       {YesNo(triage.IsBlocked)}");
            builder.AppendLine($"Immediately before:      {triage.ImmediatelyBefore}");
            builder.AppendLine($"Retry:                   {YesNo(triage.Retried)} ({triage.RetryCount})");
            builder.AppendLine($"Recovery:                {triage.ResolutionState}");
            builder.AppendLine($"Tool/component:          {triage.ToolOrComponent}");
            builder.AppendLine($"Error code:              {triage.ErrorCode ?? "not recorded"}");
            builder.AppendLine($"Command:                 {triage.Command ?? "not recorded"}");
            builder.AppendLine($"Probable owner:          {triage.ProbableOwner.Owner} — {triage.ProbableOwner.Confidence}");
            builder.AppendLine($"Last successful action:  {triage.LastSuccessfulOperation ?? "not recorded"}");
            builder.AppendLine($"Owner reason:            {triage.ProbableOwner.Reason}");
            builder.AppendLine($"Evidence:                {(triage.EvidenceComplete ? "Complete" : "Incomplete")}");
            if (!triage.EvidenceComplete)
            {
                builder.AppendLine("Missing:                 " + string.Join(", ", triage.MissingEvidence));
            }
            if (triage.SharedTooling is not null)
            {
                builder.AppendLine(
                    $"Shared tooling:          {triage.SharedTooling.AffectedAgentCount} logical agents affected by {triage.SharedTooling.FailureCode}");
                if (triage.SharedTooling.AffectedSessionCount > triage.SharedTooling.AffectedAgentCount)
                {
                    builder.AppendLine(
                        $"Affected sessions:       {triage.SharedTooling.AffectedSessionCount}");
                }
                builder.AppendLine($"Shared component:        {triage.SharedTooling.Component}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Technical details");
        builder.AppendLine("-----------------");
        builder.AppendLine($"Issue:      {detail.Issue.Id}");
        builder.AppendLine($"Mod:        {detail.Issue.ModId}");
        builder.AppendLine($"Agent:      {detail.Issue.AgentId}");
        builder.AppendLine($"Logical agent: {detail.Issue.LogicalAgentId ?? "legacy/session-scoped"}");
        builder.AppendLine($"Run:        {detail.Issue.RunId}");
        builder.AppendLine($"Category:   {detail.Issue.Category}");
        builder.AppendLine($"Severity:   {detail.Issue.Severity}");
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

    private static string YesNo(bool value) => value ? "Yes" : "No";

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
    private static string FormatTimestamp(long timestamp) =>
        DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss");

    private static string FormatDuration(long durationMilliseconds)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(0, durationMilliseconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
    }

    private static string SessionStatusText(AgentObservabilitySessionSummary session) =>
        session.Status == AgentStatus.Failed || session.FailureState
            ? "Failed"
            : session.Status == AgentStatus.Completed
                ? "Completed"
                : StatusText(session.Status);


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

    private static string? EventIdFromTag(ListViewItem item) =>
        item.Tag switch
        {
            AgentObservabilityActivityListItem state => state.EventId,
            string eventId => eventId,
            _ => null
        };

    private static string? SelectedEventId(ListView list) =>
        list.SelectedItems.Count == 0 ? null : EventIdFromTag(list.SelectedItems[0]);

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
