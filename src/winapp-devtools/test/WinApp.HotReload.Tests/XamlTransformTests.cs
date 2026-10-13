// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;

namespace WinApp.HotReload.Tests;

[TestClass]
public sealed class XamlTransformTests
{
    private const string Header = """
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:models="using:App.Models"
        """;

    private sealed class Resolver : IXamlTypeResolver
    {
        public string? Resolve(string xmlNamespace, string localName) => localName switch
        {
            "Page" => "Microsoft.UI.Xaml.Controls.Page",
            "Window" => "Microsoft.UI.Xaml.Window",
            "DataTemplate" => "Microsoft.UI.Xaml.DataTemplate",
            "AutomationProperties" => "Microsoft.UI.Xaml.Automation.AutomationProperties",
            _ when xmlNamespace.StartsWith("using:", StringComparison.Ordinal) => xmlNamespace[6..] + "." + localName,
            _ => "Microsoft.UI.Xaml.Controls." + localName,
        };

        public bool HasEvent(string typeName, string member) => member is "Click" or "Loaded";

        public bool IsWindow(string typeName) => typeName == "Microsoft.UI.Xaml.Window";

        public bool IsTemplate(string typeName) => typeName == "Microsoft.UI.Xaml.DataTemplate";
    }

    private static XamlTransformResult Transform(string body, string root = "Page") =>
        XamlTransform.Transform($"<{root} x:Class=\"App.MainPage\" {Header}>{body}</{root.Split(' ')[0]}>", new Resolver());

    private static XElement Runtime(XamlTransformResult result) => XElement.Parse(result.RuntimeXaml);

    [TestMethod]
    public void TemplateXBindBecomesDataContextBindingAndDataTypeIsRemoved()
    {
        var result = Transform("""
            <ListView><ListView.ItemTemplate><DataTemplate x:DataType="models:TodoItem">
              <CheckBox IsChecked="{x:Bind IsDone, Mode=TwoWay}" AutomationProperties.AutomationId="{x:Bind Title}" />
            </DataTemplate></ListView.ItemTemplate></ListView>
            """);

        var template = Runtime(result).Descendants().Single(e => e.Name.LocalName == "DataTemplate");
        var checkBox = template.Elements().Single();
        Assert.IsFalse(template.Attributes().Any(a => a.Name.LocalName == "DataType"));
        Assert.AreEqual("{Binding IsDone, Mode=TwoWay}", (string?)checkBox.Attribute("IsChecked"));
        Assert.AreEqual("{Binding Title, Mode=OneTime}", (string?)checkBox.Attribute("AutomationProperties.AutomationId"));
        Assert.IsEmpty(result.Manifest.Bindings);
        Assert.IsEmpty(result.Names);
    }

    [TestMethod]
    public void RootScopeXBindMovesToManifestWithAGeneratedName()
    {
        var result = Transform("""<StackPanel><TextBlock Text="{x:Bind ViewModel.Summary, Mode=OneWay}" /></StackPanel>""");

        var binding = result.Manifest.Bindings.Single();
        var text = Runtime(result).Descendants().Single(e => e.Name.LocalName == "TextBlock");
        Assert.AreEqual("ViewModel.Summary", binding.Path);
        Assert.AreEqual("OneWay", binding.Mode);
        Assert.AreEqual("Text", binding.Property);
        Assert.AreEqual(binding.Element, (string?)text.Attribute(XName.Get("Name", XamlTransform.XamlNamespace)));
        Assert.IsNull(text.Attribute("Text"));
        Assert.IsEmpty(result.Names, "Generated names must not become fields.");
    }

    [TestMethod]
    public void EventsAreRemovedAndRecordedForTheAgent()
    {
        var result = Transform("""<StackPanel><Button x:Name="AddButton" Click="AddButton_Click" /><Button Click="Other_Click" /></StackPanel>""");

        CollectionAssert.AreEqual(new[] { "AddButton_Click", "Other_Click" }, result.Manifest.Events.Select(e => e.Handler).ToArray());
        Assert.AreEqual("AddButton", result.Manifest.Events[0].Element);
        Assert.StartsWith(XamlTransform.GeneratedNamePrefix, result.Manifest.Events[1].Element!);
        Assert.IsFalse(Runtime(result).Descendants().Any(e => e.Attribute("Click") is not null));
        // Messages name elements the way the user wrote them, never by a generated name.
        Assert.AreEqual("MainPage: waiting for handler AddButton_Click (AddButton.Click)", result.Manifest.Events[0].Waiting("MainPage"));
        Assert.AreEqual("MainPage: waiting for handler Other_Click (Button.Click)", result.Manifest.Events[1].Waiting("MainPage"));
    }

    [TestMethod]
    public void WindowRootAttributesAreAppliedByTheAgent()
    {
        var result = Transform("""<Grid x:Name="Root" />""", root: "Window Title=\"Todos\"");

        Assert.AreEqual("Todos", result.Manifest.RootProperties["Title"]);
        Assert.IsNull(Runtime(result).Attribute("Title"));
        Assert.AreEqual("Microsoft.UI.Xaml.Window", result.RootTypeName);
    }

    [TestMethod]
    public void NamesOutsideTemplatesBecomeFields()
    {
        var result = Transform("""
            <Grid x:Name="RootGrid"><TextBox x:Name="NewTodoBox" />
              <ListView><ListView.ItemTemplate><DataTemplate><TextBlock x:Name="InTemplate" /></DataTemplate></ListView.ItemTemplate></ListView>
            </Grid>
            """);

        CollectionAssert.AreEqual(new[] { "RootGrid", "NewTodoBox" }, result.Names.Select(n => n.Name).ToArray());
        Assert.AreEqual("Microsoft.UI.Xaml.Controls.TextBox", result.Names[1].TypeName);
    }

    [TestMethod]
    public void ApplicationResourcesBecomeAStandaloneDictionaryWithAbsoluteSources()
    {
        var app = """
            <Application x:Class="App.App" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Application.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.MergedDictionaries>
                    <ResourceDictionary Source="Styles/Colors.xaml" />
                    <ResourceDictionary Source="ms-appx:///Other.xaml" />
                  </ResourceDictionary.MergedDictionaries>
                  <x:String x:Key="AppTitle">Todos</x:String>
                </ResourceDictionary>
              </Application.Resources>
            </Application>
            """;

        var transformed = XamlTransform.Transform(app, new Resolver());
        var (xaml, lineMap) = XamlTransform.ApplicationResources(transformed.RuntimeXaml, "ms-appx:///App.xaml", transformed.LineMap);
        var dictionary = XElement.Parse(xaml, LoadOptions.SetLineInfo);

        Assert.AreEqual("ResourceDictionary", dictionary.Name.LocalName);
        CollectionAssert.AreEqual(new[] { "ms-appx:///Styles/Colors.xaml", "ms-appx:///Other.xaml" },
            dictionary.Descendants().Where(e => e.Attribute("Source") is not null).Select(e => (string)e.Attribute("Source")!).ToArray());
        var title = dictionary.Descendants().Single(e => e.Name.LocalName == "String");
        Assert.AreEqual("Todos", title.Value);
        // Parse errors in the dictionary point at the user's App.xaml.
        System.Xml.IXmlLineInfo at = title;
        Assert.AreEqual(8, lineMap.Single(m => m[0] == at.LineNumber && m[1] == at.LinePosition)[2]);
    }

    [TestMethod]
    public void XBindFunctionsAndCastsTranslateToCSharpWithWatchedPaths()
    {
        string? Resolve(string prefix) => prefix == "local" ? "App" : null;

        var call = XBindExpression.Translate("local:Format.Count(ViewModel.Items.Count, 'a \"b\"', 2, x:True)", "__s", Resolve)!;
        Assert.AreEqual("global::App.Format.Count(__s.ViewModel.Items.Count, \"a \\\"b\\\"\", 2, true)", call.CSharp);
        CollectionAssert.AreEqual(new[] { "ViewModel.Items.Count" }, call.Watch.ToArray());

        var cast = XBindExpression.Translate("(x:String)Tag", "__s", Resolve)!;
        Assert.AreEqual("((string)__s.Tag)", cast.CSharp);
        CollectionAssert.AreEqual(new[] { "Tag" }, cast.Watch.ToArray());

        var instance = XBindExpression.Translate("ViewModel.Format(Title)", "__s", Resolve)!;
        Assert.AreEqual("__s.ViewModel.Format(__s.Title)", instance.CSharp);
        CollectionAssert.AreEquivalent(new[] { "ViewModel", "Title" }, instance.Watch.ToArray());

        Assert.IsNull(XBindExpression.Translate("unknown:Format.Count(A)", "__s", Resolve));
    }

    [TestMethod]
    public void TemplateEventsAndFunctionsBecomeHooksWithGeneratedMethods()
    {
        var result = Transform("""
            <ListView><ListView.ItemTemplate><DataTemplate x:DataType="models:TodoItem">
              <Button Click="Delete_Click" Content="{x:Bind models:Fmt.Label(Title), Mode=OneWay}" />
            </DataTemplate></ListView.ItemTemplate></ListView>
            """);

        var button = Runtime(result).Descendants().Single(e => e.Name.LocalName == "Button");
        var id = (string?)button.Attribute(XName.Get("Hook.Id", "using:WinApp.DevTools.Managed.HotReload"));
        Assert.AreEqual("App.MainPage#0", id);
        var hook = result.Manifest.Hooks[id!];
        Assert.AreEqual("Delete_Click", hook.Events.Single().Handler);
        Assert.AreEqual(XamlTransform.FunctionPrefix + "0", hook.Bindings.Single().Function);
        var function = result.Functions.Single();
        Assert.AreEqual("App.Models.TodoItem", function.SourceType);
        Assert.AreEqual("global::App.Models.Fmt.Label(__s.Title)", function.Expression);
        Assert.IsNull(button.Attribute("Click"));
    }

    private sealed class StrictResolver : IXamlTypeResolver
    {
        private readonly Resolver _inner = new();
        public string? Resolve(string xmlNamespace, string localName) => localName == "NoSuchControl" ? null : _inner.Resolve(xmlNamespace, localName);
        public bool HasEvent(string typeName, string member) => _inner.HasEvent(typeName, member);
        public bool IsWindow(string typeName) => _inner.IsWindow(typeName);
        public bool IsTemplate(string typeName) => _inner.IsTemplate(typeName);
        public bool IsKnownNamespace(string xmlNamespace) => xmlNamespace == "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    }

    [TestMethod]
    public void UnknownTypesAreReportedLikeTheXamlCompiler()
    {
        var result = XamlTransform.Transform($"<Page x:Class=\"App.MainPage\" {Header}>\n<StackPanel>\n  <NoSuchControl />\n</StackPanel></Page>", new StrictResolver());

        StringAssert.EndsWith(result.Errors.Single(), "Unknown type 'NoSuchControl' in XML namespace 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'");
        Assert.IsEmpty(Transform("<StackPanel><TextBlock /></StackPanel>").Errors, "Namespaces the resolver can't list are never errors.");
    }

    [TestMethod]
    public void BindParsingKeepsNestedMarkupAndFunctionPaths()
    {
        var bind = XamlTransform.ParseBind("{x:Bind IsDone, Converter={StaticResource BoolToVisibility}, Mode=OneWay}", null)!;

        Assert.AreEqual("IsDone", bind.Path);
        Assert.AreEqual("{StaticResource BoolToVisibility}", bind.Converter);
        Assert.AreEqual("OneWay", bind.Mode);
        Assert.AreEqual("Format(Title)", XamlTransform.ParseBind("{x:Bind Format(Title)}", null)!.Path);
        Assert.AreEqual("OneWay", XamlTransform.ParseBind("{x:Bind Title}", "OneWay")!.Mode);
    }

    [TestMethod]
    public void MultiArgumentFunctionsStaticMembersBindBackAndEventBindings()
    {
        var result = XamlTransform.Transform($$"""
            <Page x:Class="App.MainPage" {{Header}} xmlns:local="using:App">
              <StackPanel>
                <TextBlock Text="{x:Bind local:Facts.Pair(ViewModel.Name, ViewModel.Count), Mode=OneWay}" />
                <TextBlock Text="{x:Bind local:Facts.Banner}" />
                <TextBox Text="{x:Bind ViewModel.GetName(), Mode=TwoWay, BindBack=ViewModel.SetName}" />
                <Button Click="{x:Bind ViewModel.Increment}" />
              </StackPanel>
            </Page>
            """, new Resolver());

        Assert.IsEmpty(result.Warnings);
        CollectionAssert.AreEqual(
            new[] { "global::App.Facts.Pair(__s.ViewModel.Name, __s.ViewModel.Count)", "global::App.Facts.Banner", "__s.ViewModel.GetName()" },
            result.Functions.Select(f => f.Expression).ToArray());
        Assert.AreEqual("__s.ViewModel.SetName(__value);", result.Functions[2].BindBack);
        var click = result.Manifest.Events.Single();
        Assert.AreEqual(("Increment", "ViewModel"), (click.Handler, click.Path));
    }

    [TestMethod]
    public void InvalidClassAndElementNamesAreErrors()
    {
        var result = XamlTransform.Transform($"<Page x:Class=\"App.My Page\" {Header}><TextBlock x:Name=\"My Title\" /></Page>", new Resolver());

        Assert.HasCount(2, result.Errors);
        StringAssert.Contains(result.Errors[0], "x:Class 'App.My Page' is not a valid type name");
        StringAssert.Contains(result.Errors[1], "x:Name 'My Title' is not a valid name");
    }

    private sealed class TypedResolver : IXamlTypeResolver
    {
        private readonly Resolver _inner = new();
        public string? Resolve(string xmlNamespace, string localName) => _inner.Resolve(xmlNamespace, localName);
        public bool HasEvent(string typeName, string member) => _inner.HasEvent(typeName, member);
        public bool IsWindow(string typeName) => _inner.IsWindow(typeName);
        public bool IsTemplate(string typeName) => _inner.IsTemplate(typeName);

        public string? MemberType(string typeName, string member) => (typeName, member) switch
        {
            ("App.MainPage", "ViewModel") => "App.MainViewModel",
            ("App.MainViewModel", "Status") => "System.String",
            ("App.Models.TodoItem", "Title") => "System.String",
            ("Microsoft.UI.Xaml.Controls.TextBox", "Text") => "System.String",
            ("System.String", _) => XamlTransform.UnknownType,
            _ => null,
        };
    }

    [TestMethod]
    public void XBindPathsAreCheckedLikeTheCompiler()
    {
        var result = XamlTransform.Transform($$"""
            <Page x:Class="App.MainPage" {{Header}}>
              <StackPanel>
                <TextBlock Text="{x:Bind ViewModel.Statuz, Mode=OneWay}" />
                <TextBlock Text="{x:Bind ViewModel.Status, Mode=OneWay}" />
                <TextBlock Text="{x:Bind NotesBox.Text, Mode=OneWay}" />
                <TextBox x:Name="NotesBox" />
                <ListView><ListView.ItemTemplate><DataTemplate x:DataType="models:TodoItem">
                  <StackPanel><TextBlock Text="{x:Bind Nope}" /><TextBlock Text="{x:Bind Title}" /><TextBox x:Name="Inner" /><TextBlock Text="{x:Bind Inner.Text}" /></StackPanel>
                </DataTemplate></ListView.ItemTemplate></ListView>
              </StackPanel>
            </Page>
            """, new TypedResolver());

        Assert.HasCount(2, result.Errors);
        StringAssert.StartsWith(result.Errors[0], "(5,");
        StringAssert.EndsWith(result.Errors[0], "Invalid binding path 'ViewModel.Statuz' : Property 'Statuz' not found on type 'MainViewModel'.");
        StringAssert.StartsWith(result.Errors[1], "(10,");
        StringAssert.EndsWith(result.Errors[1], "Invalid binding path 'Nope' : Property 'Nope' not found on type 'TodoItem'.");
        var inner = XElement.Parse(result.RuntimeXaml).Descendants().Last(e => e.Name.LocalName == "TextBlock");
        Assert.AreEqual("{Binding Text, ElementName=Inner, Mode=OneTime}", (string?)inner.Attribute("Text"));
    }

    [TestMethod]
    public void UpdateSourceTriggerIsKept()
    {
        var bind = XamlTransform.ParseBind("{x:Bind Title, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", null)!;
        Assert.AreEqual("{Binding Title, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", XamlTransform.ToBinding(bind));
    }

    [TestMethod]
    public void RuntimeMarkupKeepsTheSourceLines()
    {
        var source = $"<Page x:Class=\"App.MainPage\" {Header}>\n  <StackPanel>\n    <TextBlock\n        Text=\"{{x:Bind Title}}\"\n        Click=\"OnClick\"\n        Foreground=\"Oranj\" />\n    <TextBlock Text=\"a &amp; b\" Tag=\"x&quot;y\" />\n  </StackPanel>\n</Page>";
        var result = XamlTransform.Transform(source, new Resolver());

        var runtime = XElement.Parse(result.RuntimeXaml, LoadOptions.SetLineInfo);
        var first = runtime.Descendants().First(e => e.Name.LocalName == "TextBlock");
        var foreground = (System.Xml.IXmlLineInfo)first.Attribute("Foreground")!;
        Assert.AreEqual(8, foreground.LineNumber, result.RuntimeXaml);
        Assert.AreEqual(9, foreground.LinePosition);
        var second = runtime.Descendants().Last(e => e.Name.LocalName == "TextBlock");
        Assert.AreEqual(9, ((System.Xml.IXmlLineInfo)second).LineNumber);
        Assert.AreEqual("a & b", (string?)second.Attribute("Text"));
        Assert.AreEqual("x\"y", (string?)second.Attribute("Tag"));
    }
}