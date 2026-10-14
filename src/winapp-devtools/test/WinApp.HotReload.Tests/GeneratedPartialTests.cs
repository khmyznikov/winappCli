// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WinApp.HotReload.Tests;

[TestClass]
public sealed class GeneratedPartialTests
{
    [TestMethod]
    public void FilledCollectionsAreThoseTheReplayedConstructorAddsTo()
    {
        // WinUI Gallery's ItemsRepeaterPage: the constructor calls a method that fills a field.
        var parts = CSharpSyntaxTree.ParseText("""
            public sealed partial class Page1
            {
                public ObservableCollection<int>? BarItems;
                public List<string> Names { get; } = new();
                private readonly List<int> _log = new();
                public static List<int> Shared = new();

                public Page1(object? argument = null)
                {
                    InitializeComponent();
                    InitializeData();
                    this.Names.Add("a");
                    _log.Add(argument!.GetHashCode());
                    Shared.Add(1);
                    Nav.MenuItems.Add(new object());
                }

                private void InitializeData()
                {
                    BarItems ??= new();
                    BarItems.Add(1);
                }
            }
            """).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        var constructor = parts[0].Members.OfType<ConstructorDeclarationSyntax>().Single();

        // _log is only filled by a statement that uses the constructor's argument, which a reload leaves out.
        var filled = GeneratedPartial.FilledCollections(constructor, parts, GeneratedPartial.ArgumentStatements(constructor));

        CollectionAssert.AreEquivalent(new[] { "BarItems", "Names" }, filled);

        // Each goes back to its value before the constructor ran: null, or its initializer (a get-only property's
        // through its backing field).
        Assert.AreEqual("this.BarItems = default!;", GeneratedPartial.ResetStatement("BarItems", parts));
        StringAssert.Contains(GeneratedPartial.ResetStatement("Names", parts), "List<string> __value = new();");
        StringAssert.Contains(GeneratedPartial.ResetStatement("Names", parts), "<Names>k__BackingField");
        Assert.AreEqual("global::System.Runtime.CompilerServices.Unsafe.AsRef(in this._log) = new();", GeneratedPartial.ResetStatement("_log", parts));
    }
}
