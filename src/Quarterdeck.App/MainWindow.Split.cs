using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Quarterdeck.App;

/// <summary>
/// Two sides, so two sessions can be seen at once: a second tab strip right of the first, which
/// appears when a tab is moved to it and goes when either side has no tabs left. Each tab is still
/// one session; only which strip holds it changes. The side last clicked or typed in is the active
/// one: the menus, shortcuts and new tabs act on it, and a blue line marks it.
/// </summary>
public partial class MainWindow
{
    private static readonly Brush ActiveSideBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF)));

    private TabControl? _activeTabs;

    /// <summary>The side the menus and shortcuts act on.</summary>
    private TabControl ActiveTabs => _activeTabs is { } t && (t == Tabs || t.Items.Count > 0) ? t : Tabs;

    private bool IsSplit => TabsRight.Items.Count > 0;

    private IEnumerable<TabItem> AllTabItems => Tabs.Items.OfType<TabItem>().Concat(TabsRight.Items.OfType<TabItem>());

    private TabControl SideOf(TabItem tab) => TabsRight.Items.Contains(tab) ? TabsRight : Tabs;

    private void WireSides()
    {
        foreach (var side in new[] { Tabs, TabsRight })
        {
            // Typing or clicking in a side makes it the active one. WebView2 has its own window, so
            // its clicks never reach WPF as mouse events; the keyboard focus moving into it does.
            side.IsKeyboardFocusWithinChanged += (s, e) => { if ((bool)e.NewValue) SetActiveSide((TabControl)s); };
            side.PreviewMouseDown += (s, _) => SetActiveSide((TabControl)s);
        }
    }

    /// <summary>Brings a tab to the front on its own side and makes that side the active one.</summary>
    private void SelectTab(TabItem tab)
    {
        var side = SideOf(tab);
        side.SelectedItem = tab;
        SetActiveSide(side);
    }

    private void SetActiveSide(TabControl side)
    {
        if (ReferenceEquals(_activeTabs, side))
        {
            return;
        }

        _activeTabs = side;
        ShowActiveSide();
        UpdateConnectionToggle();
        MarkFocusedRow();
    }

    private void ShowActiveSide()
    {
        LeftTabsFrame.BorderBrush = IsSplit && ActiveTabs == Tabs ? ActiveSideBrush : Brushes.Transparent;
        RightTabsFrame.BorderBrush = IsSplit && ActiveTabs == TabsRight ? ActiveSideBrush : Brushes.Transparent;
    }

    /// <summary>Opens or closes the right side to match what it holds; a left side left empty takes the right's tabs.</summary>
    private void LayoutSides()
    {
        if (Tabs.Items.Count == 0 && TabsRight.Items.Count > 0)
        {
            var moving = TabsRight.Items.OfType<TabItem>().ToList();
            var selected = TabsRight.SelectedItem;
            TabsRight.Items.Clear();
            foreach (var t in moving)
            {
                Tabs.Items.Add(t);
            }

            Tabs.SelectedItem = selected;
            _activeTabs = Tabs;
        }

        var split = IsSplit;
        var was = TabSplitter.Visibility == Visibility.Visible;
        TabSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        TabSplitterColumn.Width = split ? new GridLength(5) : new GridLength(0);
        if (split && !was)
        {
            LeftTabsColumn.Width = new GridLength(1, GridUnitType.Star);
            RightTabsColumn.Width = new GridLength(1, GridUnitType.Star);
            RightTabsColumn.MinWidth = 200;
        }
        else if (!split)
        {
            RightTabsColumn.MinWidth = 0;
            RightTabsColumn.Width = new GridLength(0);
            LeftTabsColumn.Width = new GridLength(1, GridUnitType.Star);
        }

        ShowActiveSide();
        SplitToggle.IsChecked = split;
        SplitToggle.IsEnabled = split || AllTabItems.Count() > 1;
    }

    /// <summary>The toolbar's Split: on splits off this tab; off brings the right side's tabs back.</summary>
    private void OnSplitToggle(object sender, RoutedEventArgs e)
    {
        if (IsSplit)
        {
            MergeSides();
        }
        else
        {
            OnMoveToOtherSide(sender, e);
        }

        // The click flips the button whatever happened; it shows what is, not what was asked.
        SplitToggle.IsChecked = IsSplit;
    }

    /// <summary>One strip again: the right side's tabs join the left, the tab in use stays in front.</summary>
    private void MergeSides()
    {
        var keep = ActiveTabs.SelectedItem as TabItem;
        foreach (var t in TabsRight.Items.OfType<TabItem>().ToList())
        {
            TabsRight.Items.Remove(t);
            Tabs.Items.Add(t);
        }

        LayoutSides();
        if (keep is not null)
        {
            SelectTab(keep);
            if (LiveFor(keep) is { PoppedOut: null } live)
            {
                live.View.FocusTerminal();
            }
        }
    }

    /// <summary>Moves a tab to the other side, opening the split if it was closed.</summary>
    private void MoveToOtherSide(TabItem tab)
    {
        var from = SideOf(tab);
        var to = from == Tabs ? TabsRight : Tabs;
        if (from == Tabs && Tabs.Items.Count == 1)
        {
            // Nothing would be left on the left: the split needs two tabs.
            LiveFor(tab)?.View.SetStatus("Open a second tab first: a split shows two tabs side by side.");
            return;
        }

        from.Items.Remove(tab);
        to.Items.Add(tab);
        LayoutSides();
        SelectTab(tab);
        if (LiveFor(tab) is { PoppedOut: null } live)
        {
            live.View.FocusTerminal();
        }
    }

    private void OnMoveToOtherSide(object sender, RoutedEventArgs e)
    {
        if (ActiveTabs.SelectedItem is TabItem tab)
        {
            MoveToOtherSide(tab);
        }
    }

    /// <summary>F6: the keyboard goes to the other side's tab.</summary>
    private void OnSwitchSide(object sender, RoutedEventArgs e) => SwitchSide();

    private void SwitchSide()
    {
        if (!IsSplit)
        {
            return;
        }

        var other = ActiveTabs == Tabs ? TabsRight : Tabs;
        if (other.SelectedItem is TabItem tab)
        {
            SelectTab(tab);
            if (LiveFor(tab) is { PoppedOut: null } live)
            {
                live.View.FocusTerminal();
            }
        }
    }
}
