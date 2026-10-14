param([switch]$Stable)
$ns = 'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:TodoBench"'
function Lines($t) { , @(($t -replace "`r`n", "`n").TrimEnd("`n") -split "`n") }
function Step($id, $title, $file, $edit, $checks, $expect) {
    $a = [ordered]@{ id = $id; title = $title; file = $file; edit = $edit }
    if ($checks) { $a.checkpoint = [ordered]@{ id = "c-$id"; kind = 'live'; uiChecks = @($checks) } }
    if ($expect) { $a.expect = $expect }
    $a
}
function Create($id, $title, $file, $text, $checks = $null, $expect = $null) { Step $id $title $file ([ordered]@{ op = 'create-file'; text = (Lines $text) }) $checks $expect }
function Replace($id, $title, $file, $find, $text, $checks = $null, $expect = $null) { Step $id $title $file ([ordered]@{ op = 'replace'; find = (Lines $find); text = (Lines $text) }) $checks $expect }
function C($assert, $target, $value = $null) { $c = [ordered]@{ target = $target; assert = $assert }; if ($null -ne $value) { $c.value = $value }; $c }
function Expect($outcome, $detail = $null) { $e = [ordered]@{ outcome = $outcome }; if ($detail) { $e.detail = $detail }; $e }

$window = @"
<?xml version="1.0" encoding="utf-8" ?>
<Window x:Class="TodoBench.MainWindow" $ns Title="TodoBench">
    <Frame x:Name="Root" />
</Window>
"@
$windowCs = @'
using Microsoft.UI.Xaml;

namespace TodoBench;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Root.Navigate(typeof(MainPage));
    }
}
'@
$models = @'
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TodoBench;

public class ItemBase
{
    public string Name { get; set; } = "";
}

public sealed class Item : ItemBase
{
    public int Price { get; set; }
}

public static class Fmt
{
    public static string Twice(int value) => $"twice {value * 2}";
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _title = "Hello";
    private int _count;
    private bool _showLazy;

    public string Title { get => _title; set { _title = value; Raise(); } }
    public string Subtitle => "Sub";
    public int Count { get => _count; set { _count = value; Raise(); } }
    public bool ShowLazy { get => _showLazy; set { _showLazy = value; Raise(); } }
    public ObservableCollection<Item> Items { get; } = [new() { Name = "Apple", Price = 1 }, new() { Name = "Banana", Price = 2 }];

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
'@
$page = @"
<?xml version="1.0" encoding="utf-8" ?>
<Page x:Class="TodoBench.MainPage" $ns>
    <StackPanel x:Name="Panel" Spacing="6" Margin="16">
        <TextBlock x:Name="Status" AutomationProperties.AutomationId="Status" Text="ready" />
        <Button x:Name="ExistingBtn" AutomationProperties.AutomationId="ExistingBtn" Content="Existing" />
        <Button x:Name="IncBtn" AutomationProperties.AutomationId="IncBtn" Content="Increment" Click="Inc_Click" />
        <Button x:Name="ProbeBtn" AutomationProperties.AutomationId="ProbeBtn" Content="Probe" Click="Probe_Click" />
        <Button x:Name="ToggleBtn" AutomationProperties.AutomationId="ToggleBtn" Content="Toggle" Click="Toggle_Click" />
        <TextBlock AutomationProperties.AutomationId="LaterText" Text="named later" />
        <ComboBox x:Name="Picker" AutomationProperties.AutomationId="Picker" SelectedIndex="0">
            <x:String>First</x:String>
            <x:String>Second</x:String>
        </ComboBox>
        <TextBlock AutomationProperties.AutomationId="Plain" Text="plain v1" />
    </StackPanel>
</Page>
"@
$pageCs = @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TodoBench;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
    }

    private void Existing_Click(object sender, RoutedEventArgs e) => Status.Text = "existing clicked";

    private void Inc_Click(object sender, RoutedEventArgs e) => ViewModel.Count++;

    private void Probe_Click(object sender, RoutedEventArgs e) => Status.Text = "probe";

    private void Toggle_Click(object sender, RoutedEventArgs e) => ViewModel.ShowLazy = !ViewModel.ShowLazy;
}
'@
$badge = @"
<?xml version="1.0" encoding="utf-8" ?>
<UserControl x:Class="TodoBench.Badge" $ns>
    <TextBlock AutomationProperties.AutomationId="BadgeText" Text="{x:Bind Label, Mode=OneWay}" />
</UserControl>
"@
$badgeCs = @'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TodoBench;

public sealed partial class Badge : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(Badge), new PropertyMetadata(""));

    public Badge()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}
'@

$P = 'MainPage.xaml'
$setup = @(
    (Create 'setup-window' 'A window hosting a Frame' 'MainWindow.xaml' $window),
    (Create 'setup-window-code' 'It navigates to MainPage' 'MainWindow.xaml.cs' $windowCs),
    (Create 'setup-models' 'A view model with change notification, items, a static formatter' 'Models.cs' $models),
    (Create 'setup-page' 'A page with no x:Bind yet' $P $page),
    (Create 'setup-page-code' 'Its code-behind' 'MainPage.xaml.cs' $pageCs)
)
if ($Stable) {
    $setup += Replace 'setup-stable-ids' 'Prototype A: stable, append-only connection IDs' 'TodoBench.csproj' '    <UseWinUI>true</UseWinUI>' "    <UseWinUI>true</UseWinUI>`n    <EnableHotReloadStableConnectionIds>true</EnableHotReloadStableConnectionIds>"
}
$actions = @(
    (Replace 's01-text' 'A plain property edit' $P 'Text="plain v1"' 'Text="plain v2"' @((C 'text-equals' 'Plain' 'plain v2')) (Expect 'applied')),
    (Replace 's02-click-existing' 'Click on a method that already exists' $P 'Content="Existing" />' 'Content="Existing" Click="Existing_Click" />' @((C 'invoke' 'ExistingBtn'), (C 'text-equals' 'Status' 'existing clicked')) (Expect 'applied')),
    (Replace 's03a-click-new-markup' 'Click on a method not written yet (the compiler rejects it)' $P '        <Button x:Name="IncBtn"' "        <Button x:Name=`"NewBtn`" AutomationProperties.AutomationId=`"NewBtn`" Content=`"New`" Click=`"New_Click`" />`n        <Button x:Name=`"IncBtn`"" $null (Expect 'error')),
    (Replace 's03b-click-new-code' 'The new method, added' 'MainPage.xaml.cs' '    private void Inc_Click' "    private void New_Click(object sender, RoutedEventArgs e) => Status.Text = `"new clicked`";`n`n    private void Inc_Click" @((C 'invoke' 'NewBtn'), (C 'text-equals' 'Status' 'new clicked')) (Expect 'applied')),
    (Replace 's04-add-name' 'x:Name added' $P '<TextBlock AutomationProperties.AutomationId="LaterText"' '<TextBlock x:Name="Later" AutomationProperties.AutomationId="LaterText"' $null (Expect 'applied')),
    (Replace 's05-use-name' 'Code uses the new field' 'MainPage.xaml.cs' 'Status.Text = "probe";' 'Status.Text = "later:" + Later.Text;' @((C 'invoke' 'ProbeBtn'), (C 'text-equals' 'Status' 'later:named later')) (Expect 'applied')),
    (Replace 's06-xbind-first' 'The page''s first x:Bind (OneTime)' $P '        <TextBlock AutomationProperties.AutomationId="Plain"' "        <TextBlock x:Name=`"B1`" AutomationProperties.AutomationId=`"B1`" Text=`"{x:Bind ViewModel.Title}`" />`n        <TextBlock AutomationProperties.AutomationId=`"Plain`"" @((C 'text-equals' 'B1' 'Hello')) (Expect 'applied')),
    (Replace 's07-xbind-oneway' 'x:Bind OneWay' $P '        <TextBlock AutomationProperties.AutomationId="Plain"' "        <TextBlock x:Name=`"B2`" AutomationProperties.AutomationId=`"B2`" Text=`"{x:Bind ViewModel.Count, Mode=OneWay}`" />`n        <TextBlock AutomationProperties.AutomationId=`"Plain`"" @((C 'text-equals' 'B2' '0'), (C 'invoke' 'IncBtn'), (C 'text-equals' 'B2' '1')) (Expect 'applied')),
    (Replace 's08-xbind-twoway' 'x:Bind TwoWay' $P '        <TextBlock AutomationProperties.AutomationId="Plain"' "        <TextBox x:Name=`"B3`" AutomationProperties.AutomationId=`"B3`" Text=`"{x:Bind ViewModel.Title, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}`" />`n        <TextBlock x:Name=`"B4`" AutomationProperties.AutomationId=`"B4`" Text=`"{x:Bind ViewModel.Title, Mode=OneWay}`" />`n        <TextBlock AutomationProperties.AutomationId=`"Plain`"" @((C 'set-value' 'B3' 'Typed'), (C 'text-equals' 'B4' 'Typed')) (Expect 'applied')),
    (Replace 's09-xbind-function' 'x:Bind function' $P '        <TextBlock AutomationProperties.AutomationId="Plain"' "        <TextBlock x:Name=`"B5`" AutomationProperties.AutomationId=`"B5`" Text=`"{x:Bind local:Fmt.Twice(ViewModel.Count), Mode=OneWay}`" />`n        <TextBlock AutomationProperties.AutomationId=`"Plain`"" @((C 'text-equals' 'B5' 'twice 2'), (C 'invoke' 'IncBtn'), (C 'text-equals' 'B5' 'twice 4')) (Expect 'applied')),
    (Replace 's10-xbind-cast' 'x:Bind casts' $P '        <TextBlock AutomationProperties.AutomationId="Plain"' "        <ComboBox x:Name=`"ItemPicker`" AutomationProperties.AutomationId=`"ItemPicker`" ItemsSource=`"{x:Bind ViewModel.Items}`" DisplayMemberPath=`"Name`" SelectedIndex=`"0`" />`n        <TextBlock x:Name=`"B6`" AutomationProperties.AutomationId=`"B6`" Text=`"{x:Bind ((local:Item)ItemPicker.SelectedItem).Name, Mode=OneWay}`" />`n        <TextBlock x:Name=`"B7`" AutomationProperties.AutomationId=`"B7`" Text=`"{x:Bind (x:String)Picker.SelectedItem, Mode=OneWay}`" />`n        <TextBlock AutomationProperties.AutomationId=`"Plain`"" @((C 'text-equals' 'B6' 'Apple'), (C 'text-equals' 'B7' 'First')) (Expect 'applied')),
    (Replace 's11-xbind-change' 'An x:Bind path changed' $P 'Text="{x:Bind ViewModel.Title}"' 'Text="{x:Bind ViewModel.Subtitle}"' @((C 'text-equals' 'B1' 'Sub')) (Expect 'applied')),
    (Replace 's12-xbind-remove' 'An element with an x:Bind function removed' $P "        <TextBlock x:Name=`"B5`" AutomationProperties.AutomationId=`"B5`" Text=`"{x:Bind local:Fmt.Twice(ViewModel.Count), Mode=OneWay}`" />`n" '' @((C 'absent' 'B5'), (C 'invoke' 'IncBtn'), (C 'text-equals' 'B2' '3')) (Expect 'applied')),
    (Replace 's13-insert-top' 'An element inserted at the top (connection IDs shift)' $P '    <StackPanel x:Name="Panel" Spacing="6" Margin="16">' "    <StackPanel x:Name=`"Panel`" Spacing=`"6`" Margin=`"16`">`n        <TextBlock x:Name=`"TopLabel`" AutomationProperties.AutomationId=`"TopLabel`" Text=`"top`" />" @((C 'exists' 'TopLabel'), (C 'invoke' 'IncBtn'), (C 'text-equals' 'B2' '4'), (C 'invoke' 'ExistingBtn'), (C 'text-equals' 'Status' 'existing clicked'), (C 'set-value' 'B3' 'Again'), (C 'text-equals' 'B4' 'Again'), (C 'invoke' 'ProbeBtn'), (C 'text-equals' 'Status' 'later:named later')) (Expect 'applied')),
    (Replace 's14-delete-element' 'An element with a Click removed' $P "        <Button x:Name=`"NewBtn`" AutomationProperties.AutomationId=`"NewBtn`" Content=`"New`" Click=`"New_Click`" />`n" '' @((C 'absent' 'NewBtn'), (C 'invoke' 'ExistingBtn'), (C 'text-equals' 'Status' 'existing clicked'), (C 'invoke' 'IncBtn'), (C 'text-equals' 'B2' '5')) (Expect 'applied')),
    (Replace 's15-datatemplate' 'x:Bind inside a DataTemplate' $P '        <TextBlock AutomationProperties.AutomationId="Plain"' "        <ListView x:Name=`"ItemList`" AutomationProperties.AutomationId=`"ItemList`" ItemsSource=`"{x:Bind ViewModel.Items}`">`n            <ListView.ItemTemplate>`n                <DataTemplate x:DataType=`"local:Item`">`n                    <TextBlock Text=`"{x:Bind Name}`" />`n                </DataTemplate>`n            </ListView.ItemTemplate>`n        </ListView>`n        <TextBlock AutomationProperties.AutomationId=`"Plain`"" @((C 'child-count-equals' 'ItemList' '2'), (C 'exists' 'Banana')) (Expect 'applied')),
    (Replace 's16-xload' 'x:Load' $P '        <TextBlock AutomationProperties.AutomationId="Plain"' "        <TextBlock x:Name=`"Lazy`" AutomationProperties.AutomationId=`"Lazy`" x:Load=`"{x:Bind ViewModel.ShowLazy, Mode=OneWay}`" Text=`"lazy`" />`n        <TextBlock AutomationProperties.AutomationId=`"Plain`"" @((C 'absent' 'Lazy'), (C 'invoke' 'ToggleBtn'), (C 'exists' 'Lazy')) (Expect 'applied')),
    (Replace 's17-datatype-change' 'x:DataType changed' $P 'x:DataType="local:Item"' 'x:DataType="local:ItemBase"' @((C 'child-count-equals' 'ItemList' '2'), (C 'exists' 'Banana')) (Expect 'applied')),
    (Create 's18a-usercontrol-markup' 'A new UserControl: its markup' 'Badge.xaml' $badge $null (Expect 'applied')),
    (Create 's18b-usercontrol-code' 'A new UserControl: its code' 'Badge.xaml.cs' $badgeCs $null (Expect 'applied')),
    (Replace 's18c-usercontrol-used' 'The new UserControl used on the page' $P '        <TextBlock AutomationProperties.AutomationId="Plain"' "        <local:Badge x:Name=`"Badge1`" Label=`"badge v1`" />`n        <TextBlock AutomationProperties.AutomationId=`"Plain`"" @((C 'text-equals' 'BadgeText' 'badge v1')) (Expect 'applied')),
    (Replace 's19a-resources-add' 'A page resource added and used' $P "    <StackPanel x:Name=`"Panel`" Spacing=`"6`" Margin=`"16`">" "    <Page.Resources>`n        <x:String x:Key=`"Greeting`">res v1</x:String>`n    </Page.Resources>`n    <StackPanel x:Name=`"Panel`" Spacing=`"6`" Margin=`"16`">`n        <TextBlock AutomationProperties.AutomationId=`"ResText`" Text=`"{StaticResource Greeting}`" />" @((C 'text-equals' 'ResText' 'res v1')) (Expect 'applied')),
    (Replace 's19b-resources-edit' 'The resource edited' $P '>res v1<' '>res v2<' @((C 'text-equals' 'ResText' 'res v2')) (Expect 'applied')),
    (Replace 's20-final' 'A final text edit: everything still works' $P 'Text="plain v2"' 'Text="plain v3"' @((C 'text-equals' 'Plain' 'plain v3'), (C 'invoke' 'IncBtn'), (C 'text-equals' 'B2' '6'), (C 'invoke' 'ExistingBtn'), (C 'text-equals' 'Status' 'existing clicked'), (C 'set-value' 'B3' 'Final'), (C 'text-equals' 'B4' 'Final'), (C 'text-equals' 'B1' 'Sub')) (Expect 'applied'))
)
$id = if ($Stable) { 'real-compiler-spike-stable' } else { 'real-compiler-spike' }
$journey = [ordered]@{
    schemaVersion = '1.0'; id = $id
    title = 'SPIKE S1/S2: the real XAML compiler on each save, its C# applied with .NET hot reload' + $(if ($Stable) { ', with stable connection IDs (Prototype A)' } else { '' })
    description = @('Each save builds the project with the real XAML compiler, applies the regenerated .g.cs through edit and continue, sends the compiler''s .xbf as binary markup and re-runs InitializeComponent.')
    app = [ordered]@{ template = 'app-template'; assetsFrom = 'samples/winui-app/Assets'; projectFile = 'TodoBench.csproj'; rootNamespace = 'TodoBench' }
    setup = $setup
    dimensions = @(); variants = @([ordered]@{ id = 'default'; primary = $true; summary = 'default'; choices = [ordered]@{} })
    segments = @([ordered]@{ kind = 'common'; phase = 'spike'; title = 'spike'; actions = $actions })
}
$journey | ConvertTo-Json -Depth 20 | Set-Content "C:\Users\nikolame\source\copilot-worktrees\winappcli-private\hr-real-spike\test\benchmarks\watch-hot-reload\journeys\$id.json"
