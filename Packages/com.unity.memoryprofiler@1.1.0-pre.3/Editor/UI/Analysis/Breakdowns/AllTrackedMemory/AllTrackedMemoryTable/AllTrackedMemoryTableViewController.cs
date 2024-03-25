#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.MemoryProfiler.Editor.Containers;

namespace Unity.MemoryProfiler.Editor.UI
{
    class AllTrackedMemoryTableViewController : TreeViewController<AllTrackedMemoryModel, AllTrackedMemoryModel.ItemData>, IAnalysisViewSelectable
    {
        public string data = "allMemory";
        const string k_UxmlAssetGuid = "e7ac30fe2b076984e978d41347c5f0e0";

        const string k_UssClass_Dark = "all-tracked-memory-table-view__dark";
        const string k_UssClass_Light = "all-tracked-memory-table-view__light";
        const string k_UssClass_Cell_Unreliable = "analysis-view__text__information-unreliable-or-unavailable";
        const string k_UxmlIdentifier_TreeView = "all-tracked-memory-table-view__tree-view";
        const string k_UxmlIdentifier_TreeViewColumn__Description = "all-tracked-memory-table-view__tree-view__column__description";
        const string k_UxmlIdentifier_TreeViewColumn__Size = "all-tracked-memory-table-view__tree-view__column__size";
        const string k_UxmlIdentifier_TreeViewColumn__SizeBar = "all-tracked-memory-table-view__tree-view__column__size-bar";
        const string k_UxmlIdentifier_TreeViewColumn__ResidentSize = "all-tracked-memory-table-view__tree-view__column__resident-size";
        const string k_UxmlIdentifier_LoadingOverlay = "all-tracked-memory-table-view__loading-overlay";
        const string k_UxmlIdentifier_ErrorLabel = "all-tracked-memory-table-view__error-label";
        const string k_ErrorMessage = "Snapshot is from an outdated Unity version that is not fully supported.";
        const string k_NotAvailable = "N/A";
        const string k_UnreliableTooltip = "The memory profiler cannot certainly attribute which part of the " +
            "resident memory belongs to graphics, as some of it might be included in the \"untracked\" memory.\n\n" +
            "Change focus to \"Allocated Memory\" to inspect graphics in detail.\n\n" +
            "We also recommend using a platform profiler for checking the residency status of graphics memory.";

        // Model.
        readonly CachedSnapshot m_Snapshot;
        readonly bool m_BuildOnLoad;
        readonly bool m_CompareMode;
        readonly bool m_DisambiguateUnityObjects;
        readonly IResponder m_Responder;
        readonly Dictionary<string, Comparison<TreeViewItemData<AllTrackedMemoryModel.ItemData>>> m_SortComparisons;
        // View.
        int? m_SelectAfterLoadItemId;
        AllTrackedMemoryTableMode m_TableMode;
		int m_selectItemId = -1;
        TreeViewItemData<AllTrackedMemoryModel.ItemData> m_selectedItems;
        ActivityIndicatorOverlay m_LoadingOverlay;
        Label m_ErrorLabel;

        public AllTrackedMemoryTableViewController(
            CachedSnapshot snapshot,
            ToolbarSearchField searchField = null,
            bool buildOnLoad = true,
            bool compareMode = false,
            bool disambiguateUnityObjects = false,
            IResponder responder = null)
            : base(idOfDefaultColumnWithPercentageBasedWidth: null)
        {
            m_Snapshot = snapshot;
            m_SearchField = searchField;
            m_BuildOnLoad = buildOnLoad;
            m_CompareMode = compareMode;
            m_DisambiguateUnityObjects = disambiguateUnityObjects;
            m_Responder = responder;

            m_SelectAfterLoadItemId = null;
            m_TableMode = AllTrackedMemoryTableMode.CommittedAndResident;

            SearchFilterChanged += OnSearchFilterChanged;

            // Sort comparisons for each column.
            m_SortComparisons = new()
            {
                { k_UxmlIdentifier_TreeViewColumn__Description, (x, y) => string.Compare(x.data.Name, y.data.Name, StringComparison.OrdinalIgnoreCase) },
                { k_UxmlIdentifier_TreeViewColumn__Size, (x, y) => x.data.Size.Committed.CompareTo(y.data.Size.Committed) },
                { k_UxmlIdentifier_TreeViewColumn__ResidentSize, (x, y) => x.data.Size.Resident.CompareTo(y.data.Size.Resident) },
                { k_UxmlIdentifier_TreeViewColumn__SizeBar, (x, y) => m_TableMode != AllTrackedMemoryTableMode.OnlyResident ? x.data.Size.Committed.CompareTo(y.data.Size.Committed) : x.data.Size.Resident.CompareTo(y.data.Size.Resident) },
            };
        }

        public AllTrackedMemoryModel Model => m_Model;

        public IScopedFilter<string> SearchFilter { get; private set; }

        public ITextFilter ItemNameFilter { get; private set; }

        public IEnumerable<ITextFilter> ItemPathFilter { get; private set; }

        public bool ExcludeAll { get; private set; }

        ToolbarSearchField m_SearchField = null;
        protected override ToolbarSearchField SearchField => m_SearchField;

        void OnSearchFilterChanged(IScopedFilter<string> searchFilter)
        {
            SetFilters(searchFilter);
        }

        public void SetFilters(
            IScopedFilter<string> searchFilter = null,
            ITextFilter itemNameFilter = null,
            IEnumerable<ITextFilter> itemPathFilter = null,
            bool excludeAll = false)
        {
            SearchFilter = searchFilter;
            ItemNameFilter = itemNameFilter;
            ItemPathFilter = itemPathFilter;
            ExcludeAll = excludeAll;
            if (IsViewLoaded)
                BuildModelAsync();
        }

        public void SetColumnsVisibility(AllTrackedMemoryTableMode mode)
        {
            if (m_TableMode == mode)
                return;
            if (mode != AllTrackedMemoryTableMode.OnlyExport && mode != AllTrackedMemoryTableMode.OnExportAllUnityObject && mode != AllTrackedMemoryTableMode.OnExportAllOfMemory)
                m_TableMode = mode;
            var columns = m_TreeView.columns;
            switch (mode)
            {
                case AllTrackedMemoryTableMode.OnExportAllOfMemory:
                    ExportAllOfMemoryDataToFile();
                    break;
				case AllTrackedMemoryTableMode.OnlyExport:
                    if (m_selectItemId != -1)
                    {
                        ExportCurrentSelectedItemToFile();
                    }
                    columns[k_UxmlIdentifier_TreeViewColumn__Size].visible = true;
                    columns[k_UxmlIdentifier_TreeViewColumn__ResidentSize].visible = true;
                    break;
                case AllTrackedMemoryTableMode.OnlyResident:
                    columns[k_UxmlIdentifier_TreeViewColumn__Size].visible = false;
                    columns[k_UxmlIdentifier_TreeViewColumn__ResidentSize].visible = true;
                    break;
                case AllTrackedMemoryTableMode.OnlyCommitted:
                    columns[k_UxmlIdentifier_TreeViewColumn__Size].visible = true;
                    columns[k_UxmlIdentifier_TreeViewColumn__ResidentSize].visible = false;
                    break;
                case AllTrackedMemoryTableMode.CommittedAndResident:
                    columns[k_UxmlIdentifier_TreeViewColumn__Size].visible = true;
                    columns[k_UxmlIdentifier_TreeViewColumn__ResidentSize].visible = true;
                    break;
            }

            if (IsViewLoaded)
                BuildModelAsync();
        }

        public bool TrySelectCategory(IAnalysisViewSelectable.Category category)
        {
            int itemId = (int)category;

            // If tree view isn't loaded & populated yet, we have to delay
            // selection until async process is finished
            if (!TrySelectAndExpandTreeViewItem(itemId))
                m_SelectAfterLoadItemId = itemId;

            // Currently we have only "All Tracked View" categories to select,
            // so we always return true
            return true;
        }

        protected override VisualElement LoadView()
        {
            var view = ViewControllerUtility.LoadVisualTreeFromUxml(k_UxmlAssetGuid);
            if (view == null)
                throw new InvalidOperationException("Unable to create view from Uxml. Uxml must contain at least one child element.");
            view.style.flexGrow = 1;

            var themeUssClass = (EditorGUIUtility.isProSkin) ? k_UssClass_Dark : k_UssClass_Light;
            view.AddToClassList(themeUssClass);

            GatherReferencesInView(view);

            return view;
        }

        protected override void ViewLoaded()
        {
            ConfigureTreeView();

            if (m_BuildOnLoad)
                BuildModelAsync();
            else
                m_LoadingOverlay.Hide();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                m_BuildModelWorker?.Dispose();

            base.Dispose(disposing);
        }

        void GatherReferencesInView(VisualElement view)
        {
            m_TreeView = view.Q<MultiColumnTreeView>(k_UxmlIdentifier_TreeView);
            m_LoadingOverlay = view.Q<ActivityIndicatorOverlay>(k_UxmlIdentifier_LoadingOverlay);
            m_ErrorLabel = view.Q<Label>(k_UxmlIdentifier_ErrorLabel);
        }

        bool CanShowResidentMemory()
        {
            return m_Snapshot.HasSystemMemoryResidentPages && !m_CompareMode;
        }

        protected override void ConfigureTreeView()
        {
            base.ConfigureTreeView();

            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__Description, "Description", 0, BindCellForDescriptionColumn(), AllTrackedMemoryDescriptionCell.Instantiate);
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__Size, "Allocated Size", 120, BindCellForSizeColumn());
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__SizeBar, "% Impact", 180, BindCellForMemoryBarColumn(), MakeMemoryBarCell);
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__ResidentSize, "Resident Size", 120, BindCellForResidentSizeColumn(), visible: CanShowResidentMemory());
        }

        void ConfigureTreeViewColumn(string columnName, string columnTitle, int width, Action<VisualElement, int> bindCell, Func<VisualElement> makeCell = null, bool visible = true)
        {
            var column = m_TreeView.columns[columnName];
            column.title = columnTitle;
            column.bindCell = bindCell;
            column.visible = visible;
            if (width != 0)
            {
                column.width = width;
                column.minWidth = width / 2;
                column.maxWidth = width * 2;
            }
            if (makeCell != null)
                column.makeCell = makeCell;
        }

        protected override void BuildModelAsync()
        {
            // Cancel existing build if necessary.
            m_BuildModelWorker?.Dispose();

            // Show loading UI.
            m_LoadingOverlay.Show();

            // Dispatch asynchronous build.
            // Note: AsyncWorker is executed on another thread and can't use MemoryProfilerSettings.ShowReservedMemoryBreakdown.
            // We retrieve global setting immediately while on a main thread and pass it by value to the worker
            var snapshot = m_Snapshot;
            var args = new AllTrackedMemoryModelBuilder.BuildArgs(
                SearchFilter,
                ItemNameFilter,
                ItemPathFilter,
                ExcludeAll,
                MemoryProfilerSettings.ShowReservedMemoryBreakdown,
                m_DisambiguateUnityObjects,
                m_TableMode == AllTrackedMemoryTableMode.OnlyCommitted,
                ProcessObjectSelected);
            var sortComparison = BuildSortComparisonFromTreeView();
            m_BuildModelWorker = new AsyncWorker<AllTrackedMemoryModel>();
            m_BuildModelWorker.Execute(() =>
            {
                try
                {
                    // Build the data model.
                    var modelBuilder = new AllTrackedMemoryModelBuilder();
                    var model = modelBuilder.Build(snapshot, args);

                    // Sort it according to the current sort descriptors.
                    model.Sort(sortComparison);

                    return model;
                }
                catch (UnsupportedSnapshotVersionException)
                {
                    return null;
                }
                catch (System.Threading.ThreadAbortException)
                {
                    // We expect a ThreadAbortException to be thrown when cancelling an in-progress builder. Do not log an error to the console.
                    return null;
                }
                catch (Exception _e)
                {
                    Debug.LogError($"{_e.Message}\n{_e.StackTrace}");
                    return null;
                }
            }, (model) =>
                {
                    // Update model.
                    m_Model = model;

                    var success = model != null;
                    if (success)
                    {
                        // Refresh UI with new data model.
                        RefreshView();
                    }
                    else
                    {
                        // Display error message.
                        m_ErrorLabel.text = k_ErrorMessage;
                        UIElementsHelper.SetElementDisplay(m_ErrorLabel, true);
                    }

                    // Hide loading UI.
                    m_LoadingOverlay.Hide();

                    // Notify responder.
                    m_Responder?.Reloaded(this, success);

                    // Dispose asynchronous worker.
                    m_BuildModelWorker.Dispose();

                    // Update usage counters
                    MemoryProfilerAnalytics.AddAllTrackedMemoryUsage(SearchFilter != null, MemoryProfilerSettings.ShowReservedMemoryBreakdown, m_TableMode);
                });
        }

        protected override void RefreshView()
        {
            base.RefreshView();

            if (m_SelectAfterLoadItemId.HasValue)
            {
                // At this point we expect that it can't fail
                TrySelectAndExpandTreeViewItem(m_SelectAfterLoadItemId.Value);
                m_SelectAfterLoadItemId = null;
            }
        }

        bool TrySelectAndExpandTreeViewItem(int itemId)
        {
            if (m_TreeView.viewController.GetIndexForId(itemId) == -1)
                return false;

            m_TreeView.SetSelectionById(itemId);
            m_TreeView.ExpandItem(itemId);
            m_TreeView.Focus();
            m_TreeView.schedule.Execute(() => m_TreeView.ScrollToItemById(itemId));

            return true;
        }

        Action<VisualElement, int> BindCellForDescriptionColumn()
        {
            const string k_NoName = "<No Name>";
            return (element, rowIndex) =>
            {
                var cell = (AllTrackedMemoryDescriptionCell)element;
                var itemData = m_TreeView.GetItemDataForIndex<AllTrackedMemoryModel.ItemData>(rowIndex);

                var displayText = itemData.Name;
                if (string.IsNullOrEmpty(displayText))
                    displayText = k_NoName;
                cell.SetText(displayText);

                var secondaryDisplayText = string.Empty;
                var childCount = itemData.ChildCount;
                if (childCount > 0)
                    secondaryDisplayText = $"({childCount:N0} Item{((childCount > 1) ? "s" : string.Empty)})";
                cell.SetSecondaryText(secondaryDisplayText);

                if (itemData.Unreliable)
                {
                    cell.tooltip = k_UnreliableTooltip;
                    cell.AddToClassList(k_UssClass_Cell_Unreliable);
                }
                else
                {
                    cell.tooltip = string.Empty;
                    cell.RemoveFromClassList(k_UssClass_Cell_Unreliable);
                }
            };
        }

        Action<VisualElement, int> BindCellForSizeColumn()
        {
            return (element, rowIndex) =>
            {
                var itemData = m_TreeView.GetItemDataForIndex<AllTrackedMemoryModel.ItemData>(rowIndex);
                var size = itemData.Size.Committed;
                var cell = (Label)element;

                if (!itemData.Unreliable)
                {
                    cell.text = EditorUtility.FormatBytes((long)size);
                    cell.tooltip = $"{itemData.Size.Committed:N0} B";
                    cell.displayTooltipWhenElided = false;
                    cell.RemoveFromClassList(k_UssClass_Cell_Unreliable);
                }
                else
                {
                    cell.text = k_NotAvailable;
                    cell.tooltip = k_UnreliableTooltip;
                    cell.AddToClassList(k_UssClass_Cell_Unreliable);
                }
            };
        }

        Action<VisualElement, int> BindCellForResidentSizeColumn()
        {
            return (element, rowIndex) =>
            {
                var itemData = m_TreeView.GetItemDataForIndex<AllTrackedMemoryModel.ItemData>(rowIndex);
                var size = itemData.Size.Resident;
                var cell = (Label)element;

                if (!itemData.Unreliable)
                {
                    cell.text = EditorUtility.FormatBytes((long)size);
                    cell.tooltip = $"{size:N0} B";
                    cell.displayTooltipWhenElided = false;
                    cell.RemoveFromClassList(k_UssClass_Cell_Unreliable);
                }
                else
                {
                    cell.text = k_NotAvailable;
                    cell.tooltip = k_UnreliableTooltip;
                    cell.AddToClassList(k_UssClass_Cell_Unreliable);
                }
            };
        }

        Action<VisualElement, int> BindCellForMemoryBarColumn()
        {
            return (element, rowIndex) =>
            {
                var maxValue = m_TableMode != AllTrackedMemoryTableMode.OnlyResident ?
                    m_Model.TotalMemorySize.Committed : m_Model.TotalMemorySize.Resident;

                var item = m_TreeView.GetItemDataForIndex<AllTrackedMemoryModel.ItemData>(rowIndex);
                var cell = element as MemoryBar;

                if (!item.Unreliable)
                    cell.Set(item.Size, maxValue, maxValue);
                else
                    cell.SetEmpty();
            };
        }

        VisualElement MakeMemoryBarCell()
        {
            var bar = new MemoryBar();
            bar.Mode = m_TableMode switch
            {
                AllTrackedMemoryTableMode.OnlyCommitted => MemoryBarElement.VisibilityMode.CommittedOnly,
                AllTrackedMemoryTableMode.OnlyResident => MemoryBarElement.VisibilityMode.ResidentOnly,
                AllTrackedMemoryTableMode.CommittedAndResident => MemoryBarElement.VisibilityMode.CommittedAndResident,
				AllTrackedMemoryTableMode.OnlyExport => MemoryBarElement.VisibilityMode.None,
				AllTrackedMemoryTableMode.OnExportAllUnityObject => MemoryBarElement.VisibilityMode.None,
				AllTrackedMemoryTableMode.OnExportAllOfMemory => MemoryBarElement.VisibilityMode.None,
                _ => throw new NotImplementedException(),
            };
            return bar;
        }

        protected override void OnTreeItemSelected(int itemId, AllTrackedMemoryModel.ItemData itemData)
        {
            // Invoke the selection processor for the selected item.
            m_Model.SelectionProcessor?.Invoke(itemId, itemData);
        }

        void ProcessObjectSelected(int itemId, AllTrackedMemoryModel.ItemData itemData)
        {
            m_Responder?.SelectedItem(itemId, this, itemData);
        }

        protected override void OnMenuItemSelected(int itemId, AllTrackedMemoryModel.ItemData itemData, TreeViewItemData<AllTrackedMemoryModel.ItemData> selectedItems)
        {
            // Invoke the selection processor for the selected menu item.
            m_selectItemId = itemId;
            m_selectedItems = selectedItems;
        }

        #region 测试代码
        TreeViewItemData<AllTrackedMemoryModel.ItemData> FindNodeChild(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, List<string> treePaths)
        {
            var root = node;

            foreach (var subTreeName in treePaths)
            {
                if (root.hasChildren)
                {
                    foreach (var childNode in root.children)
                    {
                        if (childNode.data.Name == subTreeName)
                        {
                            root = childNode;
                            break;
                        }
                    }
                }
            }
            
            // 检查是否找到子tree
            if (root.data.Name == treePaths[^1])
            {
                return root;
            }

            return default;
        }


        void RecordUntrackedData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, StreamWriter sWriter)
        {
            foreach (var childNode in node.children)
            {
                if (childNode.data.Name == "Private")
                {
                    sWriter.WriteLine($"{node.data.Name}\t{childNode.data.Name}\t{childNode.data.Size.Committed}\t{childNode.data.ChildCount}");
                    break;
                }
            }
        }        
        
        void RecordExecutablesMappedData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, StreamWriter sWriter)
        {
            // 需要统计dxcache的内存大小， 其他dll总大小
            var dxCaches = node.children.Where(item => item.data.Name.Contains("DXCache")).ToList();
            ulong dxSize = 0;
            foreach (var childNode in dxCaches)
            {
                dxSize += childNode.data.Size.Committed;
            }

            var data = node.data;
            sWriter.WriteLine($"{data.Name}\tDxCache\t{dxSize}\t{dxCaches.Count}");
            sWriter.WriteLine($"{data.Name}\tOther Dll\t{data.Size.Committed - dxSize}\t{data.ChildCount -dxCaches.Count}");
        }
        
        void RecordNativeData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, StreamWriter sWriter)
        {
            // Native.Unity Subsystems.Manager.Il2CPPMemoryAllocator
            // Native.Unity Subsystems.UnsafeUtility.Malloc(Persistent)
            // Native.Unity Subsystems.SerializedFile
            // Native.Unity Subsystems.PersistentManager.Remapper
            // Native.Reserved
            // Native.Native Objects
            var data = node.data;
            var target = FindNodeChild(
                node, 
                new List<string>() { "Unity Subsystems", "Managers", "IL2CPPMemoryAllocator" }
                );
            sWriter.WriteLine($"{data.Name}\t{target.data.Name}\t{target.data.Size.Committed}\t{1}");
            
            
            target = FindNodeChild(
                node, 
                new List<string>() { "Unity Subsystems", "UnsafeUtility", "Malloc(Persistent)" }
            );
            sWriter.WriteLine($"{data.Name}\t{target.data.Name}\t{target.data.Size.Committed}\t1");
            
            
            target = FindNodeChild(
                node, 
                new List<string>() { "Unity Subsystems", "SerializedFile" }
            );
            sWriter.WriteLine($"{data.Name}\t{target.data.Name}\t{target.data.Size.Committed}\t{target.data.ChildCount}");
            
            target = FindNodeChild(
                node, 
                new List<string>() { "Unity Subsystems", "PersistentManager.Remapper" }
            );
            sWriter.WriteLine($"{data.Name}\t{target.data.Name}\t{target.data.Size.Committed}\t1");
            
            target = FindNodeChild(
                node, 
                new List<string>() { "Reserved" }
            );
            sWriter.WriteLine($"{data.Name}\t{target.data.Name}\t{target.data.Size.Committed}\t1");
            
            target = FindNodeChild(
                node, 
                new List<string>() { "Native Objects" }
            );
            sWriter.WriteLine($"{data.Name}\t{target.data.Name}\t{target.data.Size.Committed}\t{target.data.ChildCount}");

        }

        void RecordGraphicsData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, StreamWriter sWriter)
        {
            // 导出 gfx 和 computerbuffers
        }
        
        void RecordManagedData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, StreamWriter sWriter)
        {

            foreach (var nodeChild in node.children)
            {
                if (nodeChild.data.Name == "Reserved" || nodeChild.data.Name == "Managed Objects")
                {
                    var data = nodeChild.data;
                    var count = data.ChildCount > 0 ? data.ChildCount : 1;
                    sWriter.WriteLine($"{node.data.Name}\t{data.Name}\t{data.Size.Committed}\t{count}");
                }
            }
        }


        #region 自动化流程获取数据
        public class AllMemoryClass
        {
            public string GroupName { get; set; }
            public List<SubMemoryData> SubData { get; set; }
            public ulong AllocateSize { get; set; }  //当前节点名内存总占用
            public int Count { get; set; }
            public int ChildCount { get; set; }
        }
        
        public class SubMemoryData
        {
            public string ItemName { get; set; }
            public ulong AllocateSize { get; set; }   //当前节点名内存总占用
            public int Count { get; set; }
            public int ChildCount { get; set; }
            public List<SubMemoryData> SubData { get; set; }
        }
        
        /// <summary>
        /// 判断数据是否加载完毕
        /// </summary>
        /// <returns></returns>
        public bool IsModelNone()
        {
            if (Model!=null)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        public string GetData()
        {
            List<AllMemoryClass> result = new List<AllMemoryClass>();
            var rootNodes = Model.RootNodes;
            if (rootNodes.Count <= 0)
                return null;
            foreach (var node in rootNodes)
            {
                var data = node.data;
                AllMemoryClass element = new AllMemoryClass();
                switch (data.Name)
                {
                    case "Untracked*":
                        GetUntrackedData(node,ref result);
                        break;
                    case "Executables & Mapped":
                        GetExecutablesMappedData(node,ref result);
                        break;
                    case "Native":
                        GetNativeData(node, ref result);
                        break;
                    case "Graphics (Estimated)":
                        GetGraphicsData(node, ref result);
                        break;
                    case "Managed":
                        GetManagedData(node, ref result);
                        break;
                }
            }
            return JsonConvert.SerializeObject(result);
        }
        
        void GetUntrackedData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node,ref List<AllMemoryClass> result)
        {
            AllMemoryClass element = new AllMemoryClass();
            element.GroupName = node.data.Name;
            element.AllocateSize = node.data.Size.Committed;
            element.Count = 1;
            element.ChildCount = node.children.Count();
            element.SubData = new List<SubMemoryData>();
            foreach (var childNode in node.children)
            {
                SubMemoryData subData = new SubMemoryData();
                subData.ItemName = childNode.data.Name;
                subData.AllocateSize = childNode.data.Size.Committed;
                subData.Count = 1;
                subData.ChildCount = childNode.children.Count();
                subData.SubData = new List<SubMemoryData>();
                element.SubData.Add(subData);
            }
            result.Add(element);
        }
        void GetExecutablesMappedData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node,ref List<AllMemoryClass> result)
        {
            
            // 需要统计dxcache的内存大小， 其他dll总大小
            var dxCaches = node.children.Where(item => item.data.Name.Contains("DXCache")).ToList();
            ulong dxSize = 0;
            foreach (var childNode in dxCaches)
            {
                dxSize += childNode.data.Size.Committed;
            }

            var data = node.data;
            AllMemoryClass element = new AllMemoryClass();
            element.GroupName = data.Name;
            element.AllocateSize = data.Size.Committed;
            element.ChildCount = data.ChildCount;
            element.Count = 1;
            element.SubData = new List<SubMemoryData>();
            SubMemoryData subData1 = new SubMemoryData();
            subData1.ItemName = "DxCache";
            subData1.AllocateSize = dxSize;
            subData1.Count = 1;
            subData1.ChildCount = dxCaches.Count;
            subData1.SubData = new List<SubMemoryData>();
            element.SubData.Add(subData1);
            SubMemoryData subData2 = new SubMemoryData();
            subData2.ItemName = "Other Dll";
            subData2.AllocateSize = data.Size.Committed - dxSize;
            subData2.Count = 1;
            subData2.ChildCount = data.ChildCount -dxCaches.Count;
            subData2.SubData = new List<SubMemoryData>();
            element.SubData.Add(subData2);
            result.Add(element);
        }
        void GetNativeData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, ref List<AllMemoryClass> result)
        {
            var data = node.data;
            AllMemoryClass element = new AllMemoryClass();
            element.GroupName = data.Name;
            element.AllocateSize = data.Size.Committed;
            element.ChildCount = data.ChildCount;
            element.Count = 1;
            element.SubData = new List<SubMemoryData>();
            foreach (var subItemData in node.children)
            {
                SubMemoryData sub = new SubMemoryData();
                sub.Count = 1;
                sub.AllocateSize = subItemData.data.Size.Committed;
                sub.ChildCount = subItemData.data.ChildCount;
                sub.SubData = new List<SubMemoryData>();
                sub.ItemName = subItemData.data.Name;
                if (sub.ChildCount!=0)
                {
                    foreach (var item in subItemData.children)
                    {
                        SubMemoryData su = new SubMemoryData();
                        su.Count = 1;
                        su.AllocateSize = item.data.Size.Committed;
                        su.ChildCount = item.data.ChildCount;
                        su.SubData = new List<SubMemoryData>();
                        su.ItemName = item.data.Name;
                        if (item.data.Name == "Managers")
                        {
                            foreach (var subItem in item.children)
                            {
                                if (subItem.data.Name == "IL2CPPMemoryAllocator")
                                {
                                    SubMemoryData il2cppData = new SubMemoryData();
                                    il2cppData.Count = 1;
                                    il2cppData.AllocateSize = subItem.data.Size.Committed;
                                    il2cppData.SubData = new List<SubMemoryData>();
                                    il2cppData.ItemName = subItem.data.Name;
                                    il2cppData.ChildCount = subItem.data.ChildCount;
                                    su.SubData.Add(il2cppData);
                                    break;
                                }
                            }
                        }
                        else if (item.data.Name == "UnsafeUtility")
                        {
                            foreach (var subItem in item.children)
                            {
                                if (subItem.data.Name == "Malloc(Persistent)")
                                {
                                    SubMemoryData Malloc = new SubMemoryData();
                                    Malloc.Count = 1;
                                    Malloc.AllocateSize = subItem.data.Size.Committed;
                                    Malloc.SubData = new List<SubMemoryData>();
                                    Malloc.ItemName = subItem.data.Name;
                                    Malloc.ChildCount = subItem.data.ChildCount;
                                    su.SubData.Add(Malloc);
                                    break;
                                }
                            }
                        }
                        else if (item.data.Name == "Rendering") //Rendering
                        {
                            foreach (var subItem in item.children)
                            {
                                if (subItem.data.Name == "ComputeBuffers")
                                {
                                    SubMemoryData ComputeBu = new SubMemoryData();
                                    ComputeBu.Count = 1;
                                    ComputeBu.AllocateSize = subItem.data.Size.Committed;
                                    ComputeBu.SubData = new List<SubMemoryData>();
                                    ComputeBu.ItemName = subItem.data.Name;
                                    ComputeBu.ChildCount = subItem.data.ChildCount;
                                    su.SubData.Add(ComputeBu);
                                    break;
                                }
                            }
                        }
                        sub.SubData.Add(su);
                    }
                }
                element.SubData.Add(sub);
            }
            result.Add(element);
        }
        void GetGraphicsData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, ref List<AllMemoryClass> result)
        {
            // 导出 gfx 和 computerbuffers
            var data = node.data;
            AllMemoryClass element = new AllMemoryClass();
            element.GroupName = data.Name;
            element.Count = 1;
            element.AllocateSize = data.Size.Committed;
            element.ChildCount = data.ChildCount;
            element.SubData = new List<SubMemoryData>();
            if (node.children.Count()!=0)
            {
                foreach (var item in node.children)
                {
                    if (item.data.Name=="")  //获取数据 "Rendering:ComputeBuffers"
                    {
                        SubMemoryData noNameData = new SubMemoryData();
                        noNameData.Count = 1;
                        noNameData.SubData = new List<SubMemoryData>();
                        noNameData.AllocateSize = item.data.Size.Committed;
                        noNameData.ChildCount = item.data.ChildCount;
                        noNameData.ItemName = "<No Name>";
                        SubMemoryData elementComputeBuffer = new SubMemoryData();
                        int count = 0;
                        foreach (var itemData in item.children)
                        {
                            if (itemData.data.Name =="Rendering:ComputeBuffers")
                            {
                                elementComputeBuffer.ItemName = itemData.data.Name;
                                elementComputeBuffer.AllocateSize += itemData.data.Size.Committed;
                                count += 1;
                            }
                        }
                        if (count!=0)
                        {
                            elementComputeBuffer.Count = count;
                            elementComputeBuffer.ChildCount = 0;
                            elementComputeBuffer.SubData = new List<SubMemoryData>();
                            noNameData.SubData.Add(elementComputeBuffer);
                        }
                        element.SubData.Add(noNameData);
                    }
                    else
                    {
                        SubMemoryData su = new SubMemoryData();
                        su.Count = 1;
                        su.SubData = new List<SubMemoryData>();
                        su.AllocateSize = item.data.Size.Committed;
                        su.ItemName = item.data.Name;
                        su.ChildCount = item.data.ChildCount;
                        element.SubData.Add(su);
                    }
                }
            }
            result.Add(element);
        }
        void GetManagedData(TreeViewItemData<AllTrackedMemoryModel.ItemData> node, ref List<AllMemoryClass> result)
        {
            AllMemoryClass element = new AllMemoryClass();
            element.GroupName = node.data.Name;
            element.AllocateSize = node.data.Size.Committed;
            element.Count = 1;
            element.ChildCount = node.data.ChildCount;
            element.SubData = new List<SubMemoryData>();
            foreach (var nodeChild in node.children)
            {
                if (nodeChild.data.Name == "Reserved")
                {
                    var data = nodeChild.data;
                    SubMemoryData subData = new SubMemoryData();
                    subData.ItemName = data.Name;
                    subData.AllocateSize = data.Size.Committed;
                    subData.ChildCount = data.ChildCount;
                    subData.Count = 1;
                    subData.SubData = new List<SubMemoryData>();
                    element.SubData.Add(subData);
                }
                else if (nodeChild.data.Name == "Managed Objects")
                {
                    var data = nodeChild.data;
                    SubMemoryData subData = new SubMemoryData();
                    subData.ItemName = data.Name;
                    subData.AllocateSize = data.Size.Committed;
                    subData.ChildCount = data.ChildCount;
                    subData.SubData = new List<SubMemoryData>();
                    //提炼Top20子元素数据
                    int getCount = 0;
                    foreach (var child in nodeChild.children)
                    {
                        SubMemoryData sub = new SubMemoryData();
                        sub.ItemName = child.data.Name;
                        sub.AllocateSize = child.data.Size.Committed;
                        sub.ChildCount = child.data.ChildCount;
                        sub.Count = 1;
                        sub.SubData = new List<SubMemoryData>();
                        subData.SubData.Add(sub);
                        getCount += 1;
                        if (getCount>=20)
                        {
                            break;
                        }
                    }
                    subData.Count = 1;
                    element.SubData.Add(subData);
                }
            }
            result.Add(element);
        }
        #endregion

        void ExportAllOfMemoryDataToFile()
        {
            var rootNodes = Model.RootNodes;
            if (rootNodes.Count <= 0)
                return;
            var path = EditorUtility.SaveFilePanel(
                "Export Selected Items", 
                Application.dataPath,
                $"MPT_AllofMemory_{DateTime.Now.ToString("yyyyMMdd_HHmm")}", 
                "tab"
                );
            
            using var writer = new StreamWriter(path);
            writer.WriteLine("Group Name\tItem Name\tAllocated Size\tCount");
            
            foreach (var node in rootNodes)
            {
                var data = node.data;
                switch (data.Name)
                {
                    case "Untracked*":
                        RecordUntrackedData(node, writer);
                        break;
                    case "Executables & Mapped":
                        RecordExecutablesMappedData(node, writer);
                        break;
                    case "Native":
                        RecordNativeData(node, writer);
                        break;
                    case "Graphics (Estimated)":
                        RecordGraphicsData(node, writer);
                        break;
                    case "Managed":
                        RecordManagedData(node, writer);
                        break;
                }
            }
        }
        
        void ExportCurrentSelectedItemToFile()
        {
            if (m_selectItemId == -1)
                return;
            if (!m_selectedItems.hasChildren)
                return;
            
            
            var path = EditorUtility.SaveFilePanel("Export Selected Items", "",
                $"MPT_{m_selectedItems.data.Name}_{DateTime.Now.ToString("yyyyMMdd_HHmm")}", "tab");
            if (string.IsNullOrEmpty(path))
                return;
            using var writer = new StreamWriter(path);
            int idx = 0;
            var tableHeader = new List<string>() { "Name", "Size", "RootReference", "ChildCount", "Unreliable" };
            var item = m_selectedItems.children.First();
            
            // Native需求解析一下metaData数据 先增加列
            var sourceIndex = item.data.Source.Index;
            MetaDataHelpers.GenerateMetaDataString(m_Snapshot, (int)sourceIndex, out var metaData);
            if (metaData != null)
            {
                foreach (var key in metaData)
                {
                    tableHeader.Add(key.Item1);
                }
            }
            writer.WriteLine(string.Join('\t', tableHeader));

            bool parseStringValue = m_selectedItems.data.Name == "System.String";
            
            foreach (var itemData in m_selectedItems.children)
            {
                var data = itemData.data;
                string objName =  String.Empty;
                if (parseStringValue)
                {
                    // 获取快照中托管对象string的原始值，页面显示的值可能不完整
                    ObjectData objectData = ObjectData.FromSourceLink(m_Snapshot, data.Source);
                    ManagedObjectInfo managedObjectInfo = objectData.GetManagedObject(m_Snapshot);
                    objName = managedObjectInfo.ReadString(m_Snapshot);
                }
                else
                {
                    objName = data.Name;
                }
                
                MetaDataHelpers.GenerateMetaDataString(m_Snapshot, (int)data.Source.Index, out var generatedString);
                var dataItem = new List<string>() {objName, data.Size.Committed.ToString(), data.Source.Index.ToString(), data.ChildCount.ToString(), data.Unreliable.ToString() };
                if (generatedString != null)
                {
                    foreach (var key in generatedString)
                        dataItem.Add(key.Item2);
                }
                writer.WriteLine(string.Join('\t', dataItem));
                EditorUtility.DisplayProgressBar("Exporting", $"{idx}/{m_selectedItems.children.Count()}", idx / (float)m_selectedItems.children.Count());
                ++idx;
            }
            writer.Dispose();
            EditorUtility.ClearProgressBar();
        }

        #endregion


        Comparison<TreeViewItemData<AllTrackedMemoryModel.ItemData>> BuildSortComparisonFromTreeView()
        {
            var sortedColumns = m_TreeView.sortedColumns;
            if (sortedColumns == null)
                return null;

            var sortComparisons = new List<Comparison<TreeViewItemData<AllTrackedMemoryModel.ItemData>>>();
            foreach (var sortedColumnDescription in sortedColumns)
            {
                if (sortedColumnDescription == null)
                    continue;

                var sortComparison = m_SortComparisons[sortedColumnDescription.columnName];

                // Invert the comparison's input arguments depending on the sort direction.
                var sortComparisonWithDirection = (sortedColumnDescription.direction == SortDirection.Ascending) ? sortComparison : (x, y) => sortComparison(y, x);
                sortComparisons.Add(sortComparisonWithDirection);
            }

            return (x, y) =>
            {
                var result = 0;
                foreach (var sortComparison in sortComparisons)
                {
                    result = sortComparison.Invoke(x, y);
                    if (result != 0)
                        break;
                }

                return result;
            };
        }

        public interface IResponder
        {
            // Invoked when an item is selected in the table. Arguments are the view controller, and the item's data.
            void SelectedItem(
                int itemId,
                AllTrackedMemoryTableViewController viewController,
                AllTrackedMemoryModel.ItemData itemData);

            // Invoked after the table has been reloaded. Success argument is true if a model was successfully built or false it there was an error when building the model.
            void Reloaded(AllTrackedMemoryTableViewController viewController, bool success);
        }
    }
}
#endif
