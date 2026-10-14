// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    [DataRow("zzznotthere", 0)]
    [DataRow("SendButton", 1)]
    public async Task SearchAsync_ControlViewCycleThroughWebView2_Terminates(string query, int expected)
    {
        var tree = new WebView2CycleTree();
        tree.Install();
        var svc = NewService();

        // Without cycle protection the walk never ends; the deadline turns that into a failure.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var results = await svc.SearchAsync(tree.Target, new UiSelector { Query = query }, 50, deadline.Token);

        Assert.HasCount(expected, results);
        tree.AssertEachElementExpandedOnce();
    }

    [TestMethod]
    public async Task FindSingleElementAsync_RequireUnique_ControlViewCycleThroughWebView2_Terminates()
    {
        var tree = new WebView2CycleTree();
        tree.Install();
        var svc = NewService();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await svc.FindSingleElementAsync(
            tree.Target, new UiSelector { Query = "zzznotthere" }, requireUnique: true, deadline.Token);

        Assert.IsNull(result);
        tree.AssertEachElementExpandedOnce();
    }

    [TestMethod]
    public async Task InspectAsync_ControlViewCycleThroughWebView2_ListsEachElementOnce()
    {
        var tree = new WebView2CycleTree();
        tree.Install();
        var svc = NewService();

        var elements = await svc.InspectAsync(tree.Target, null, 40, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            tree.AutomationIds,
            elements.Select(element => element.AutomationId).ToArray());
    }

    /// <summary>
    /// The Control View a WinUI 3 window with a WebView2 reports: the WebView2's children include
    /// the window's XAML input site, which contains the WebView2 again.
    /// </summary>
    private sealed class WebView2CycleTree
    {
        private readonly Dictionary<IUIAutomationElement, IUIAutomationElement> _firstChild = [];
        private readonly Dictionary<IUIAutomationElement, IUIAutomationElement> _nextSibling = [];
        private readonly Dictionary<IUIAutomationElement, int> _expansions = [];
        private readonly List<(string AutomationId, IUIAutomationElement Element)> _elements = [];

        public WebView2CycleTree()
        {
            Root = Element("window", 0x100);
            var bridge = Element("bridge", 0x200);
            var browser = Element("browser", 0x300);
            var inputSite = Element("inputSite", 0x400);
            var webView = Element("webView", 0x500);
            var send = Element("SendButton", 0);

            _firstChild[Root] = bridge;
            _firstChild[bridge] = browser;
            _nextSibling[browser] = inputSite;
            _firstChild[inputSite] = webView;
            _nextSibling[webView] = send;
            _firstChild[webView] = browser;
        }

        public IUIAutomationElement Root { get; }

        public UiTarget Target { get; } = new()
        {
            ProcessId = Environment.ProcessId,
            ProcessName = "webview2-cycle",
            WindowHandle = 0x100,
            IsExplicitWindow = true,
        };

        public string[] AutomationIds => _elements.Select(element => element.AutomationId).ToArray();

        public void Install()
        {
            var walker = ComProxy<IUIAutomationTreeWalker>((method, args) =>
            {
                var element = (IUIAutomationElement)args![0]!;
                switch (method.Name)
                {
                    case "GetFirstChildElement":
                        _expansions[element] = _expansions.GetValueOrDefault(element) + 1;
                        return _firstChild.GetValueOrDefault(element);
                    case "GetNextSiblingElement":
                        return _nextSibling.GetValueOrDefault(element);
                    default:
                        return ThrowCom();
                }
            });

            UiAutomationService.s_getRootElement = (_, _, _) => Root;
            UiAutomationService.s_findAllDescendants = (_, _) => ElementArray();
            UiAutomationService.s_getControlViewWalker = _ => walker;
            UiAutomationService.s_getExplicitIdentityWalker = _ => walker;
            UiAutomationService.s_elementFromHandle = (_, _) => Root;
            UiAutomationService.s_findInvokableAncestor = (_, _, _) => null;
        }

        public void AssertEachElementExpandedOnce()
        {
            foreach (var (automationId, element) in _elements.Where(entry => entry.Element != Root))
            {
                Assert.IsTrue(_expansions.GetValueOrDefault(element) <= 1, $"{automationId} children were read more than once.");
            }
        }

        private IUIAutomationElement Element(string automationId, nint hwnd)
        {
            var element = ComProxy<IUIAutomationElement>((method, _) => method.Name switch
            {
                "FindAll" => ElementArray(),
                "get_CurrentNativeWindowHandle" => new HWND(hwnd),
                "get_CurrentAutomationId" => StringBstr(automationId),
                "get_CurrentName" => StringBstr(automationId),
                "get_CurrentClassName" => EmptyBstr(),
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId,
                "get_CurrentBoundingRectangle" => new RECT { left = 1, top = 2, right = 11, bottom = 12 },
                "get_CurrentIsEnabled" => new BOOL(true),
                "get_CurrentIsOffscreen" => new BOOL(false),
                "GetCurrentPattern" => null,
                _ => ThrowCom(),
            });
            _elements.Add((automationId, element));
            return element;
        }
    }
}
