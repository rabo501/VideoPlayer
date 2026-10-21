using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
namespace VideoPlayer
{
    public partial class MainWindow
    {
        private double _toolsWidth=360;
        private void ConstrainToolsWidth()
        {
            if(ToolsColumn==null||ActualWidth<=0)return;
            ToolsColumn.MaxWidth=Math.Max(300,(ActualWidth-40)*0.55);
        }
        private void ToggleTools_Click(object sender,RoutedEventArgs e)
        { SetToolsVisible(ToolsPanel.Visibility!=Visibility.Visible); }
        private void SetToolsVisible(bool visible)
        {
            if(visible)
            {
                ConstrainToolsWidth();
                ToolsColumn.MinWidth=300;ToolsColumn.Width=new GridLength(Math.Max(300,_toolsWidth));
                ToolsSplitterColumn.Width=new GridLength(7);ToolsPanel.Visibility=Visibility.Visible;ToolsSplitter.Visibility=Visibility.Visible;
            }
            else
            {
                if(ToolsColumn.ActualWidth>=300)_toolsWidth=ToolsColumn.ActualWidth;
                ToolsColumn.MinWidth=0;ToolsColumn.Width=new GridLength(0);ToolsSplitterColumn.Width=new GridLength(0);
                ToolsPanel.Visibility=Visibility.Collapsed;ToolsSplitter.Visibility=Visibility.Collapsed;
            }
            ToolsPanelMenuItem.IsChecked=visible;PanelToggleButton.Content=visible?"Ukryj panel ▶":"◀ Pokaż panel";
        }
        private void SelectExportTab_Click(object sender,RoutedEventArgs e)
        {SetToolsVisible(true);ToolsTabs.SelectedIndex=2;}
        private void MenuScale_Click(object sender,RoutedEventArgs e)
        {
            if(sender is not MenuItem menu)return;
            foreach(ComboBoxItem item in VideoScaleBox.Items)
                if(string.Equals(item.Tag?.ToString(),menu.Tag?.ToString(),StringComparison.Ordinal))
                {VideoScaleBox.SelectedItem=item;break;}
        }
        private void ShowVideoContextMenu()
        {
            var menu=new ContextMenu{PlacementTarget=VideoHost,Placement=PlacementMode.MousePoint};
            void Add(string text,RoutedEventHandler action){var item=new MenuItem{Header=text};item.Click+=action;menu.Items.Add(item);}
            Add("Ustaw początek (I)",MarkStart_Click);Add("Ustaw koniec (O)",MarkEnd_Click);
            menu.Items.Add(new Separator());Add("Dodaj zdarzenie…",ContextAddMarker_Click);
            Add("Zapisz klatkę (S)",Screenshot_Click);Add("Resetuj widok (R)",ResetView_Click);
            menu.IsOpen=true;
        }
        private void ContextAddMarker_Click(object sender,RoutedEventArgs e)
        {SetToolsVisible(true);ToolsTabs.SelectedIndex=1;MarkerDescriptionBox.Focus();}
    }
}
